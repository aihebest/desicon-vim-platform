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

###############################################################################
# Operational alerts
#
# Failed vendor notifications: the outbox dispatcher logs VIM_EMAIL_FAILURE
# (severity Error) when an email fails for the second time or is given up on.
# Without this, a lost mailbox or permission silently stops every vendor
# notice (as happened in dev on 22 Oct 2026).
#
# Cost: one log alert rule evaluated every 15 minutes, about USD 1.50/month;
# email notifications from an action group are free up to 1,000/month.
###############################################################################

locals {
  alerts_enabled = length(var.alert_emails) > 0
}

resource "azurerm_monitor_action_group" "ops" {
  count               = local.alerts_enabled ? 1 : 0
  name                = "ag-${var.alert_name_prefix}-ops"
  resource_group_name = var.resource_group_name
  short_name          = "vimops"

  dynamic "email_receiver" {
    for_each = var.alert_emails
    content {
      name                    = "email-${email_receiver.key}"
      email_address           = email_receiver.value
      use_common_alert_schema = true
    }
  }

  tags = var.tags
}

resource "azurerm_monitor_scheduled_query_rules_alert_v2" "email_failures" {
  count               = local.alerts_enabled ? 1 : 0
  name                = "alert-${var.alert_name_prefix}-email-failures"
  resource_group_name = var.resource_group_name
  location            = var.location
  description         = "Vendor / staff notification emails are failing. Check the Mailbox:UserId mailbox still exists and the function app still has Exchange 'Application Mail.Send' on it (infra/scripts/grant-mailbox-access.ps1)."
  display_name        = "VIM: notification emails failing"
  severity            = 2

  scopes                  = [azurerm_application_insights.this.id]
  evaluation_frequency    = "PT15M"
  window_duration         = "PT15M"
  auto_mitigation_enabled = true

  criteria {
    query                   = <<-KQL
      traces
      | where message startswith "VIM_EMAIL_FAILURE"
    KQL
    time_aggregation_method = "Count"
    operator                = "GreaterThan"
    threshold               = 0

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.ops[0].id]
  }

  tags = var.tags
}
