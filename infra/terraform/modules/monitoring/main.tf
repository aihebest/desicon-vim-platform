###############################################################################
# Desicon VIM Platform -- monitoring module
#
# One Log Analytics workspace per environment, and one workspace-based
# Application Insights component that writes into it.
#
# Why one workspace: every other module sends its diagnostic logs here, so a
# single KQL query can join "the web app threw an exception" with "the storage
# account refused a write" and "SQL audited a failed login" on the same
# timeline. Splitting them across workspaces makes incident review slower for
# no benefit at this size.
#
# Cost: Log Analytics is consumption-priced (per GB ingested, plus retention
# beyond the free period). daily_quota_gb caps ingestion; leave it set in dev.
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

resource "azurerm_log_analytics_workspace" "this" {
  name                = var.log_analytics_name
  resource_group_name = var.resource_group_name
  location            = var.location

  sku               = "PerGB2018"
  retention_in_days = var.retention_in_days
  daily_quota_gb    = var.daily_quota_gb

  tags = var.tags
}

resource "azurerm_application_insights" "this" {
  name                = var.app_insights_name
  resource_group_name = var.resource_group_name
  location            = var.location
  workspace_id        = azurerm_log_analytics_workspace.this.id
  application_type    = "web"

  # When false, telemetry is only accepted from callers that authenticate with
  # Entra ID (the app must then configure the Azure Monitor exporter with a
  # TokenCredential, i.e. its managed identity, and hold "Monitoring Metrics
  # Publisher" on this component). Left as a variable so the app team can
  # switch it once the code supports it; the connection string alone carries
  # only an ingestion key, not a secret that grants read access.
  local_authentication_enabled = var.app_insights_local_auth_enabled

  tags = var.tags
}

# The workspace's own audit trail (who queried what, who changed settings).
resource "azurerm_monitor_diagnostic_setting" "workspace" {
  name                       = "diag-${var.log_analytics_name}"
  target_resource_id         = azurerm_log_analytics_workspace.this.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.this.id

  enabled_log {
    category_group = "audit"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}
