###############################################################################
# Desicon VIM Platform -- app hosting module
#
# One Linux App Service plan running two apps:
#
#   1. The web app (Desicon.Vim.Web) -- the Blazor internal workbench staff
#      sign in to with Microsoft Entra ID.
#   2. The function app (Desicon.Vim.Functions) -- background timer jobs:
#      mailbox intake (every 5 min), SLA escalation (every 15 min) and outbox
#      notification dispatch (every 1 min).
#
# Sharing one plan means one monthly compute bill for both. The function app
# is on a dedicated (App Service) plan rather than Consumption, so "always on"
# keeps the timer triggers firing reliably and VNet integration is available.
#
# The function host needs its own storage account for timer leases and
# internal bookkeeping ("host storage"). It is kept separate from the
# documents account so that host traffic and permissions never mix with case
# documents. It is accessed by managed identity only:
# storage_uses_managed_identity = true makes the provider write the app
# setting AzureWebJobsStorage__accountName (and no key-bearing
# AzureWebJobsStorage). Do NOT also put AzureWebJobsStorage__accountName in
# app_settings: the provider filters that key out when reading back, so
# declaring it causes a permanent plan diff.
#
# Role assignments for both identities live in the role-assignments module.
###############################################################################

terraform {
  required_version = ">= 1.9"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = ">= 4.54, < 5.0"
    }
  }
}

locals {
  # Business Central client secret: a Key Vault reference, resolved by App
  # Service at runtime using the app's system-assigned identity. No version
  # in the URI, so a rotated secret is picked up automatically (App Service
  # re-reads references roughly every 24 hours, or on restart).
  bc_client_secret_reference = "@Microsoft.KeyVault(SecretUri=${var.key_vault_uri}secrets/${var.bc_client_secret_name})"

  # Settings both apps read. Keys use "__" because Linux App Service does not
  # allow ":" in setting names; .NET configuration maps "__" back to ":".
  shared_app_settings = {
    "ASPNETCORE_ENVIRONMENT"         = var.dotnet_environment
    "ConnectionStrings__VimDb"       = var.sql_connection_string
    "Storage__BlobServiceUri"        = var.documents_blob_endpoint
    "Storage__DocumentsContainer"    = var.documents_container_name
    "DocumentIntelligence__Endpoint" = var.docintel_endpoint
    "BusinessCentral__TenantId"      = var.business_central.tenant_id
    "BusinessCentral__Environment"   = var.business_central.environment
    "BusinessCentral__CompanyId"     = var.business_central.company_id
    "BusinessCentral__ClientId"      = var.business_central.client_id
    "BusinessCentral__ClientSecret"  = local.bc_client_secret_reference
    "BusinessCentral__Enabled"       = tostring(var.vim_settings.business_central_enabled)
    "Vim__WorkbenchUrl"              = coalesce(var.vim_settings.workbench_url, "https://${var.web_app_name}.azurewebsites.net")
    "Vim__FinanceManagerEmail"       = var.vim_settings.finance_manager_email
    "Vim__AccountsPayableEmail"      = var.vim_settings.accounts_payable_email
    "Vim__VerificationHours"         = tostring(var.vim_settings.verification_hours)
    "Vim__VerificationBasis"         = var.vim_settings.verification_basis
  }

  # WEBSITES_CONTAINER_START_TIME_LIMIT: a cold start on the small dev plan can
  # take a few minutes; the default 230 s limit made App Service give up.
  web_app_settings = merge(local.shared_app_settings, {
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = var.app_insights_connection_string
    "AzureAd__Instance"                     = "https://login.microsoftonline.com/"
    "AzureAd__TenantId"                     = var.entra_tenant_id
    "AzureAd__ClientId"                     = var.entra_workbench_client_id
    "WEBSITES_CONTAINER_START_TIME_LIMIT"   = "600"
  })

  # APPLICATIONINSIGHTS_CONNECTION_STRING is set through site_config below for
  # the function app (the provider manages that key itself).
  function_app_settings = merge(local.shared_app_settings, {
    "AZURE_FUNCTIONS_ENVIRONMENT"  = var.dotnet_environment
    "FUNCTIONS_WORKER_RUNTIME"     = "dotnet-isolated"
    "Mailbox__UserId"              = var.mailbox_user_id
    "Mailbox__ProcessedFolderName" = "Processed"
    "MailboxIntakeSchedule"        = var.function_schedules.mailbox_intake
    "SlaMonitorSchedule"           = var.function_schedules.sla_monitor
    "OutboxDispatchSchedule"       = var.function_schedules.outbox_dispatch
    "ScanResultSyncSchedule"       = var.function_schedules.scan_result_sync
    # Run the deployed zip directly (read-only wwwroot). The deploy action may
    # rewrite this value, so it is ignored after creation.
    "WEBSITE_RUN_FROM_PACKAGE" = "1"
  })
}

# -- Host storage for the Functions runtime ------------------------------------
resource "azurerm_storage_account" "host" {
  name                = var.host_storage_account_name
  resource_group_name = var.resource_group_name
  location            = var.location

  account_kind             = "StorageV2"
  account_tier             = "Standard"
  account_replication_type = "LRS"

  https_traffic_only_enabled       = true
  min_tls_version                  = "TLS1_2"
  allow_nested_items_to_be_public  = false
  shared_access_key_enabled        = false
  default_to_oauth_authentication  = true
  cross_tenant_replication_enabled = false
  allowed_copy_scope               = "AAD"
  local_user_enabled               = false
  public_network_access_enabled    = var.host_storage_public_network_access_enabled

  network_rules {
    default_action = var.host_storage_public_network_access_enabled ? "Allow" : "Deny"
    bypass         = ["AzureServices", "Logging", "Metrics"]
  }

  blob_properties {
    delete_retention_policy {
      days = 7
    }
    container_delete_retention_policy {
      days = 7
    }
  }

  tags = var.tags

  timeouts {
    create = "15m"
    update = "15m"
  }
}

# -- App Service plan --------------------------------------------------------------
resource "azurerm_service_plan" "this" {
  name                = var.plan_name
  resource_group_name = var.resource_group_name
  location            = var.location
  os_type             = "Linux"
  sku_name            = var.sku_name
  worker_count        = var.worker_count

  # Zone balancing needs Premium v3 and at least 2 (Azure recommends 3) workers.
  zone_balancing_enabled = var.zone_balancing_enabled

  tags = var.tags
}

# -- Web app: Desicon.Vim.Web ----------------------------------------------------
resource "azurerm_linux_web_app" "web" {
  name                = var.web_app_name
  resource_group_name = var.resource_group_name
  location            = var.location
  service_plan_id     = azurerm_service_plan.this.id

  https_only                    = true
  public_network_access_enabled = true # staff reach it over the internet; Entra sign-in protects it
  virtual_network_subnet_id     = var.app_subnet_id

  # Blazor interactive server keeps a SignalR (WebSocket) circuit per browser
  # tab, held in that instance's memory. With more than one instance, every
  # request from a browser must return to the same instance: ARR affinity.
  client_affinity_enabled = true

  # No FTP or "publishing profile" passwords. GitHub Actions deploys with an
  # Entra token (OIDC), which works with basic auth switched off.
  ftp_publish_basic_authentication_enabled       = false
  webdeploy_publish_basic_authentication_enabled = false

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on                         = var.always_on
    ftps_state                        = "Disabled"
    minimum_tls_version               = "1.2"
    scm_minimum_tls_version           = "1.2"
    http2_enabled                     = true
    websockets_enabled                = true
    health_check_path                 = "/health"
    health_check_eviction_time_in_min = 5
    vnet_route_all_enabled            = var.app_subnet_id != null

    application_stack {
      dotnet_version = "10.0"
    }
  }

  app_settings = local.web_app_settings

  # Written to the app's own file system (/home/LogFiles) for troubleshooting.
  # Never shown to users; the app's error page is controlled by the code.
  logs {
    detailed_error_messages = true
    failed_request_tracing  = true

    http_logs {
      file_system {
        retention_in_days = 7
        retention_in_mb   = 35
      }
    }
  }

  tags = var.tags
}

# -- Function app: Desicon.Vim.Functions -------------------------------------------
resource "azurerm_linux_function_app" "func" {
  name                = var.function_app_name
  resource_group_name = var.resource_group_name
  location            = var.location
  service_plan_id     = azurerm_service_plan.this.id

  storage_account_name          = azurerm_storage_account.host.name
  storage_uses_managed_identity = true

  functions_extension_version = "~4"

  https_only                    = true
  public_network_access_enabled = true # needed so GitHub-hosted runners can reach the SCM (deployment) site
  virtual_network_subnet_id     = var.app_subnet_id

  ftp_publish_basic_authentication_enabled       = false
  webdeploy_publish_basic_authentication_enabled = false

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on               = true # timer triggers on a dedicated plan only fire reliably with always_on
    ftps_state              = "Disabled"
    minimum_tls_version     = "1.2"
    scm_minimum_tls_version = "1.2"
    http2_enabled           = true
    vnet_route_all_enabled  = var.app_subnet_id != null

    # No inbound access restriction on purpose: the app has only timer
    # triggers (no HTTP endpoints to protect), and denying the main site risks
    # blocking the Always On keep-alive that keeps those timers firing.

    application_insights_connection_string = var.app_insights_connection_string

    application_stack {
      dotnet_version              = "10.0"
      use_dotnet_isolated_runtime = true
    }
  }

  app_settings = local.function_app_settings

  tags = var.tags

  lifecycle {
    ignore_changes = [
      app_settings["WEBSITE_RUN_FROM_PACKAGE"],
    ]
  }
}

# -- Diagnostics -------------------------------------------------------------------
resource "azurerm_monitor_diagnostic_setting" "plan" {
  name                       = "diag-${var.plan_name}"
  target_resource_id         = azurerm_service_plan.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_metric {
    category = "AllMetrics"
  }
}

resource "azurerm_monitor_diagnostic_setting" "web" {
  name                       = "diag-${var.web_app_name}"
  target_resource_id         = azurerm_linux_web_app.web.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log {
    category_group = "allLogs"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}

resource "azurerm_monitor_diagnostic_setting" "func" {
  name                       = "diag-${var.function_app_name}"
  target_resource_id         = azurerm_linux_function_app.func.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log {
    category_group = "allLogs"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}

resource "azurerm_monitor_diagnostic_setting" "host_storage" {
  name                       = "diag-${var.host_storage_account_name}"
  target_resource_id         = azurerm_storage_account.host.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_metric {
    category = "Transaction"
  }
}
