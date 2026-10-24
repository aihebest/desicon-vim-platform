###############################################################################
# Desicon VIM Platform -- Azure AI Document Intelligence module
#
# Reads invoice PDFs and images and returns structured fields (vendor, invoice
# number, dates, totals, line items) using the prebuilt invoice model. The app
# uses those fields to pre-fill the case and to flag likely duplicates; a
# person still verifies them.
#
# The Azure resource type is still called "FormRecognizer" (the service's old
# name). Posture:
#   - local_auth_enabled = false: API keys are switched off, so the only way in
#     is an Entra token. The apps call it with their managed identities and the
#     "Cognitive Services User" role.
#   - A custom subdomain is required for Entra (token) authentication and for
#     private endpoints; the endpoint becomes https://<subdomain>.cognitiveservices.azure.com/.
#
# Cost: S0 is pay-per-page analysed (consumption); F0 is a free tier with a low
# monthly page limit and cannot be used with private endpoints.
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

resource "azurerm_cognitive_account" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  kind                = "FormRecognizer"
  sku_name            = var.sku_name

  custom_subdomain_name         = var.custom_subdomain_name
  local_auth_enabled            = false
  public_network_access_enabled = var.public_network_access_enabled

  # Outbound calls from the service are not needed for the prebuilt models.
  outbound_network_access_restricted = true

  network_acls {
    default_action = var.public_network_access_enabled ? "Allow" : "Deny"
    ip_rules       = []
  }

  identity {
    type = "SystemAssigned"
  }

  tags = var.tags
}

resource "azurerm_monitor_diagnostic_setting" "this" {
  name                       = "diag-${var.name}"
  target_resource_id         = azurerm_cognitive_account.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log {
    category_group = "allLogs"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}
