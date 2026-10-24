###############################################################################
# Desicon VIM Platform -- Azure SQL module
#
# The transactional store: cases, workflow state, review decisions, the outbox
# and the append-only audit table [vim].[AuditEvents].
#
# Posture:
#   - Microsoft Entra-only authentication. There is no SQL login and no
#     password anywhere -- not in Terraform, not in state, not in app settings.
#     The server administrator is an Entra SECURITY GROUP, so admin access is
#     granted and revoked by group membership, not by editing this code.
#   - TDE (encryption at rest) is on by default with a service-managed key.
#   - Minimum TLS 1.2.
#   - Auditing sent to Log Analytics (server-level policy, covers every
#     database on the server).
#   - Public endpoint toggled by the environment root; prod uses a private
#     endpoint created in the network module.
#
# What this module deliberately does NOT do: create database users for the
# apps' managed identities. CREATE USER ... FROM EXTERNAL PROVIDER is a
# statement run inside the database by an Entra admin; ARM (and therefore
# Terraform's azurerm provider) cannot do it. Run
# infra/scripts/create-sql-users.sql once after the first apply.
###############################################################################

terraform {
  required_version = ">= 1.9"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = ">= 4.54, < 5.11"
    }
  }
}

locals {
  # Serverless SKUs look like GP_S_Gen5_2. auto-pause and min_capacity can only
  # be sent for those; sending them for a provisioned SKU is an API error.
  is_serverless = can(regex("_S_", var.sku_name))
}

resource "azurerm_mssql_server" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  version             = "12.0"
  minimum_tls_version = "1.2"

  public_network_access_enabled = var.public_network_access_enabled

  # Free "express" vulnerability assessment. Stated explicitly: the provider
  # default is false, and leaving it unset can switch it off on a later apply.
  express_vulnerability_assessment_enabled = true

  azuread_administrator {
    login_username              = var.entra_admin_group_name
    object_id                   = var.entra_admin_group_object_id
    tenant_id                   = var.tenant_id
    azuread_authentication_only = true
  }

  # A server identity, so the server can look up Entra principals when a
  # service principal (rather than a signed-in person) runs CREATE USER ...
  # FROM EXTERNAL PROVIDER. Only needed if that is ever automated; grant this
  # identity the "Directory Readers" Entra role in that case.
  identity {
    type = "SystemAssigned"
  }

  tags = var.tags
}

resource "azurerm_mssql_database" "this" {
  name      = var.database_name
  server_id = azurerm_mssql_server.this.id

  sku_name    = var.sku_name
  max_size_gb = var.max_size_gb
  collation   = "SQL_Latin1_General_CP1_CI_AS"

  auto_pause_delay_in_minutes = local.is_serverless ? var.auto_pause_delay_in_minutes : null
  min_capacity                = local.is_serverless ? var.min_capacity : null

  zone_redundant       = var.zone_redundant
  storage_account_type = var.backup_storage_redundancy

  # TDE with a service-managed key. Stated, not left to the default, so it is
  # visible to the policy checks and to a reviewer.
  transparent_data_encryption_enabled = true

  short_term_retention_policy {
    retention_days = var.short_term_retention_days
  }

  long_term_retention_policy {
    weekly_retention  = var.long_term_retention.weekly_retention
    monthly_retention = var.long_term_retention.monthly_retention
    yearly_retention  = var.long_term_retention.yearly_retention
    week_of_year      = var.long_term_retention.week_of_year
  }

  # No lifecycle.prevent_destroy: Terraform does not allow it to vary per
  # environment, and dev must be disposable. Production protection comes from
  # the GitHub "prod" environment reviewers (a destroy shows in the plan they
  # approve), point-in-time restore, and long-term retention backups. Consider
  # an Azure resource lock (CanNotDelete) on the prod resource group.
  tags = var.tags
}

# -- Firewall (public endpoint only) -----------------------------------------
# "Allow Azure services" is the special 0.0.0.0 rule. It admits traffic from
# any Azure-hosted address (including other customers' resources), so it is
# only acceptable because authentication is Entra-only. Used in dev, where the
# apps have no VNet integration and reach SQL from App Service's shared
# outbound addresses. Never created when the public endpoint is off.
resource "azurerm_mssql_firewall_rule" "azure_services" {
  count = var.public_network_access_enabled && var.allow_azure_services ? 1 : 0

  name             = "AllowAllWindowsAzureIps"
  server_id        = azurerm_mssql_server.this.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_mssql_firewall_rule" "allowed" {
  for_each = var.public_network_access_enabled ? toset(var.allowed_ip_addresses) : toset([])

  name             = "allow-${replace(each.value, ".", "-")}"
  server_id        = azurerm_mssql_server.this.id
  start_ip_address = each.value
  end_ip_address   = each.value
}

# -- Auditing to Log Analytics ------------------------------------------------
# Server-level auditing with Azure Monitor as the destination. Azure delivers
# server audit events through a diagnostic setting on the hidden "master"
# database, so that setting is created first.
resource "azurerm_monitor_diagnostic_setting" "master_audit" {
  name                       = "diag-${var.name}-audit"
  target_resource_id         = "${azurerm_mssql_server.this.id}/databases/master"
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log {
    category = "SQLSecurityAuditEvents"
  }
}

resource "azurerm_mssql_server_extended_auditing_policy" "this" {
  server_id              = azurerm_mssql_server.this.id
  log_monitoring_enabled = true

  depends_on = [azurerm_monitor_diagnostic_setting.master_audit]
}

# Performance and error telemetry for the application database. Audit events
# are deliberately not listed here: they already arrive via master above, and
# listing them twice doubles the ingestion bill.
resource "azurerm_monitor_diagnostic_setting" "database" {
  name                       = "diag-${var.database_name}"
  target_resource_id         = azurerm_mssql_database.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log { category = "SQLInsights" }
  enabled_log { category = "AutomaticTuning" }
  enabled_log { category = "QueryStoreRuntimeStatistics" }
  enabled_log { category = "Errors" }
  enabled_log { category = "DatabaseWaitStatistics" }
  enabled_log { category = "Timeouts" }
  enabled_log { category = "Blocks" }
  enabled_log { category = "Deadlocks" }

  enabled_metric { category = "Basic" }
  enabled_metric { category = "InstanceAndAppAdvanced" }
  enabled_metric { category = "WorkloadManagement" }
}
