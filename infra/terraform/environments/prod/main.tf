###############################################################################
# Desicon VIM Platform -- environment root
#
# This file is IDENTICAL in environments/dev, uat and prod. Everything that
# differs between environments lives in variables.tf defaults and in the
# environment's *.auto.tfvars / TF_VAR_* values. If you change this file,
# copy the change to the other two (CI does not check this for you; a diff of
# the three files should always be empty).
###############################################################################

data "azurerm_client_config" "current" {}

# Four random characters appended to names that must be globally unique in
# Azure (Key Vault, storage, SQL server, web/function apps, Document
# Intelligence subdomain). Generated once and kept in state.
resource "random_string" "suffix" {
  length  = 4
  upper   = false
  special = false
  numeric = true
  lower   = true
}

locals {
  # Short region codes used in names: rg-vim-dev-weu etc.
  region_short_codes = {
    westeurope         = "weu"
    northeurope        = "neu"
    southafricanorth   = "san"
    southafricawest    = "saw"
    uksouth            = "uks"
    ukwest             = "ukw"
    francecentral      = "frc"
    germanywestcentral = "gwc"
  }
  location_key = lower(replace(var.location, " ", ""))
  region_short = lookup(local.region_short_codes, local.location_key, substr(local.location_key, 0, 4))

  base         = "vim-${var.environment}-${local.region_short}" # vim-dev-weu
  base_compact = "vim${var.environment}${local.region_short}"   # vimdevweu (storage: no hyphens)
  suffix       = random_string.suffix.result

  names = {
    resource_group = "rg-${local.base}"
    log_analytics  = "law-${local.base}"
    app_insights   = "appi-${local.base}"
    key_vault      = "kv-${local.base}-${local.suffix}"
    storage        = "st${local.base_compact}${local.suffix}"
    host_storage   = "stfn${local.base_compact}${local.suffix}"
    sql_server     = "sql-${local.base}-${local.suffix}"
    docintel       = "di-${local.base}-${local.suffix}"
    plan           = "asp-${local.base}"
    web_app        = "app-${local.base}-${local.suffix}"
    function_app   = "func-${local.base}-${local.suffix}"
    vnet           = "vnet-${local.base}"
  }

  # ASPNETCORE_ENVIRONMENT / AZURE_FUNCTIONS_ENVIRONMENT. Deliberately not
  # "Development" in Azure dev: that name switches on the developer exception
  # page, which would show stack traces to anyone who can reach the site.
  dotnet_environment = {
    dev  = "Dev"
    uat  = "Uat"
    prod = "Production"
  }[var.environment]

  private = var.enable_private_networking

  tags = {
    application    = "vim"
    owner          = "ICT"
    business_owner = "Finance"
    environment    = var.environment
    managed_by     = "terraform"
  }
}

resource "azurerm_resource_group" "main" {
  name     = local.names.resource_group
  location = var.location
  tags     = local.tags
}

# -- Observability (everything else sends its logs here) ------------------------
module "monitoring" {
  source = "../../modules/monitoring"

  log_analytics_name  = local.names.log_analytics
  app_insights_name   = local.names.app_insights
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location

  retention_in_days = var.log_retention_days
  daily_quota_gb    = var.log_daily_quota_gb

  tags = local.tags
}

# -- Data services --------------------------------------------------------------
module "keyvault" {
  source = "../../modules/keyvault"

  name                = local.names.key_vault
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location
  tenant_id           = data.azurerm_client_config.current.tenant_id

  public_network_access_enabled = !local.private
  secrets_officer_object_ids    = var.keyvault_secrets_officer_object_ids

  log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
  tags                       = local.tags
}

module "storage" {
  source = "../../modules/storage"

  name                = local.names.storage
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location
  replication_type    = var.storage_replication_type

  documents_container_name      = "case-documents"
  public_network_access_enabled = !local.private

  immutability_mode           = var.documents_immutability_mode
  immutability_period_in_days = var.documents_immutability_period_in_days
  immutability_locked         = var.documents_immutability_locked

  defender_enabled                  = var.defender_for_storage_enabled
  malware_scanning_cap_gb_per_month = var.malware_scanning_cap_gb_per_month

  log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
  tags                       = local.tags
}

module "sql" {
  source = "../../modules/sql"

  name                = local.names.sql_server
  database_name       = "VimDb"
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location
  tenant_id           = data.azurerm_client_config.current.tenant_id

  entra_admin_group_name      = var.sql_admin_group_name
  entra_admin_group_object_id = var.sql_admin_group_object_id

  sku_name                    = var.sql_sku_name
  max_size_gb                 = var.sql_max_size_gb
  auto_pause_delay_in_minutes = var.sql_auto_pause_delay_in_minutes
  min_capacity                = var.sql_min_capacity
  zone_redundant              = var.sql_zone_redundant
  backup_storage_redundancy   = var.sql_backup_storage_redundancy
  short_term_retention_days   = var.sql_short_term_retention_days
  long_term_retention         = var.sql_long_term_retention

  public_network_access_enabled = !local.private
  allow_azure_services          = !local.private
  allowed_ip_addresses          = var.sql_allowed_ip_addresses

  log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
  tags                       = local.tags
}

module "docintel" {
  source = "../../modules/docintel"

  name                  = local.names.docintel
  custom_subdomain_name = local.names.docintel
  resource_group_name   = azurerm_resource_group.main.name
  location              = var.docintel_location != null ? var.docintel_location : var.location
  sku_name              = var.docintel_sku_name

  public_network_access_enabled = !local.private

  log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
  tags                       = local.tags
}

# -- Private networking (prod; optional elsewhere) --------------------------------
module "network" {
  source = "../../modules/network"
  count  = local.private ? 1 : 0

  vnet_name           = local.names.vnet
  name_suffix         = local.base
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location

  address_space     = var.vnet_address_space
  app_subnet_prefix = var.app_subnet_prefix
  pe_subnet_prefix  = var.pe_subnet_prefix

  # Keys are static so Terraform can plan before the target resources exist.
  private_endpoints = {
    sql = {
      resource_id      = module.sql.server_id
      subresource_name = "sqlServer"
      dns_zone_key     = "sql"
    }
    docs-blob = {
      resource_id      = module.storage.id
      subresource_name = "blob"
      dns_zone_key     = "blob"
    }
    vault = {
      resource_id      = module.keyvault.id
      subresource_name = "vault"
      dns_zone_key     = "vault"
    }
    docintel = {
      resource_id      = module.docintel.id
      subresource_name = "account"
      dns_zone_key     = "cognitive"
    }
    host-blob = {
      resource_id      = module.app_hosting.host_storage_account_id
      subresource_name = "blob"
      dns_zone_key     = "blob"
    }
    host-queue = {
      resource_id      = module.app_hosting.host_storage_account_id
      subresource_name = "queue"
      dns_zone_key     = "queue"
    }
    host-table = {
      resource_id      = module.app_hosting.host_storage_account_id
      subresource_name = "table"
      dns_zone_key     = "table"
    }
  }

  log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
  tags                       = local.tags
}

# -- Compute ------------------------------------------------------------------------
module "app_hosting" {
  source = "../../modules/app-hosting"

  resource_group_name = azurerm_resource_group.main.name
  location            = var.location

  plan_name                 = local.names.plan
  web_app_name              = local.names.web_app
  function_app_name         = local.names.function_app
  host_storage_account_name = local.names.host_storage

  host_storage_public_network_access_enabled = !local.private

  sku_name               = var.app_service_sku_name
  worker_count           = var.app_service_worker_count
  zone_balancing_enabled = var.app_service_zone_balancing_enabled

  app_subnet_id = one(module.network[*].app_subnet_id)

  dotnet_environment       = local.dotnet_environment
  sql_connection_string    = module.sql.connection_string
  documents_blob_endpoint  = module.storage.primary_blob_endpoint
  documents_container_name = module.storage.documents_container_name
  docintel_endpoint        = module.docintel.endpoint
  key_vault_uri            = module.keyvault.uri

  business_central = {
    tenant_id   = var.business_central_tenant_id
    environment = var.business_central_environment
    company_id  = var.business_central_company_id
    client_id   = var.business_central_client_id
  }

  app_insights_connection_string = module.monitoring.app_insights_connection_string
  entra_tenant_id                = data.azurerm_client_config.current.tenant_id
  entra_workbench_client_id      = var.entra_workbench_client_id
  mailbox_user_id                = var.mailbox_user_id
  vim_settings                   = var.vim_settings

  log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
  tags                       = local.tags
}

module "role_assignments" {
  source = "../../modules/role-assignments"

  web_app_principal_id           = module.app_hosting.web_app_principal_id
  function_app_principal_id      = module.app_hosting.function_app_principal_id
  documents_storage_account_id   = module.storage.id
  documents_storage_account_name = module.storage.name
  docintel_account_id            = module.docintel.id
  key_vault_id                   = module.keyvault.id
  host_storage_account_id        = module.app_hosting.host_storage_account_id
}
