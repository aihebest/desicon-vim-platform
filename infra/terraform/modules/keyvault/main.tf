###############################################################################
# Desicon VIM Platform -- Key Vault module
#
# Holds any secret the platform cannot avoid. Today there are none: Business
# Central is reached through a federated credential (managed identity), not a
# client secret. If a secret is ever needed, Terraform creates the vault but
# NOT the secret value -- a secret written by Terraform also lands in the
# Terraform state file in plain text -- so an administrator sets it by hand.
#
# Posture:
#   - RBAC authorisation mode (no access policies). Who can read secrets is
#     decided by Azure role assignments, the same model as everything else.
#   - Purge protection on, soft delete 90 days: a deleted vault or secret can
#     be recovered for 90 days and cannot be permanently destroyed early, even
#     by an Owner. That also means the vault NAME is reserved for 90 days after
#     a destroy -- hence the random suffix in the name.
#   - public_network_access_enabled toggled by the environment root: true in
#     dev (no VNet), false in prod (reached over a private endpoint).
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

resource "azurerm_key_vault" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  tenant_id           = var.tenant_id
  sku_name            = "standard"

  rbac_authorization_enabled = true
  purge_protection_enabled   = true
  soft_delete_retention_days = 90

  public_network_access_enabled = var.public_network_access_enabled

  network_acls {
    # With public access disabled the private endpoint is the only path in and
    # these rules are moot, but Deny is stated so a later flip of the toggle
    # does not silently open the vault to the internet.
    #
    # With public access enabled (dev), Allow is deliberate: App Service
    # resolves @Microsoft.KeyVault(...) references from shared platform
    # infrastructure, which is NOT covered by the "AzureServices" bypass. A
    # Deny here without VNet integration makes every Key Vault reference fail
    # and the app setting shows the literal reference string instead of the
    # secret. Access still requires an Entra token with an RBAC role.
    default_action = var.public_network_access_enabled ? var.public_default_action : "Deny"
    bypass         = "AzureServices"
    ip_rules       = var.ip_rules
  }

  tags = var.tags
}

# The people (or group) allowed to set secret values by hand. Kept separate from the apps, which only get
# "Key Vault Secrets User" (read) in the role-assignments module.
resource "azurerm_role_assignment" "secrets_officers" {
  for_each = toset(var.secrets_officer_object_ids)

  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = each.value
}

resource "azurerm_monitor_diagnostic_setting" "this" {
  name                       = "diag-${var.name}"
  target_resource_id         = azurerm_key_vault.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  # "audit" = AuditEvent: every secret read, by whom, from where.
  enabled_log {
    category_group = "audit"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}
