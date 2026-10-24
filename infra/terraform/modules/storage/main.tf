###############################################################################
# Desicon VIM Platform -- documents storage module
#
# The account that holds every vendor invoice, delivery note, GRV and other
# supporting document, in the "case-documents" container. These documents are
# externally supplied (so potentially malicious) and commercially sensitive,
# so the account is locked down:
#
#   - No shared keys (shared_access_key_enabled = false). Nothing can use an
#     account key or a key-signed SAS. The apps authenticate with their managed
#     identities and the "Storage Blob Data Contributor" role.
#   - No anonymous/public blob access, TLS 1.2 minimum, HTTPS only.
#   - Blob versioning + 30-day soft delete for blobs and containers: an
#     overwritten or deleted document can be recovered.
#   - Microsoft Defender for Storage with malware scanning on upload: each new
#     blob is scanned and the result written to the blob's index tags
#     ("Malware Scanning scan result"). The app should not release a document
#     to reviewers until that tag says "No threats found".
#   - Optional immutability (WORM) -- see var.immutability_mode. Off by default
#     because a LOCKED policy can never be removed and blocks deletion of the
#     account and resource group until every blob's retention has expired.
#
# Terraform itself never touches the blob data plane here: the container and
# the immutability policy are created through Azure Resource Manager
# (storage_account_id), so this works even with shared keys disabled and the
# public endpoint switched off.
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

resource "azurerm_storage_account" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location

  account_kind             = "StorageV2"
  account_tier             = "Standard"
  account_replication_type = var.replication_type
  access_tier              = "Hot"

  # -- Security posture (enforced by infra/policy/terraform) ----------------
  https_traffic_only_enabled        = true
  min_tls_version                   = "TLS1_2"
  allow_nested_items_to_be_public   = false
  shared_access_key_enabled         = false
  default_to_oauth_authentication   = true
  infrastructure_encryption_enabled = true
  cross_tenant_replication_enabled  = false
  allowed_copy_scope                = "AAD"
  local_user_enabled                = false
  sftp_enabled                      = false
  public_network_access_enabled     = var.public_network_access_enabled

  network_rules {
    # Same reasoning as the Key Vault module: with public access on (dev, no
    # VNet), the apps arrive from App Service's shared outbound addresses, so
    # the firewall cannot usefully restrict them and Entra RBAC is the control.
    # With public access off, Deny is stated explicitly.
    default_action = var.public_network_access_enabled ? var.public_default_action : "Deny"
    bypass         = ["AzureServices", "Logging", "Metrics"]
    ip_rules       = var.ip_rules
  }

  blob_properties {
    versioning_enabled  = true
    change_feed_enabled = true

    delete_retention_policy {
      days = var.soft_delete_retention_days
    }

    container_delete_retention_policy {
      days = var.soft_delete_retention_days
    }
  }

  # Account-level, version-level WORM. Only created when
  # immutability_mode = "account". Changing this block forces a NEW storage
  # account (all documents would need migrating), so decide before go-live.
  dynamic "immutability_policy" {
    for_each = var.immutability_mode == "account" ? [1] : []
    content {
      allow_protected_append_writes = false
      state                         = var.immutability_locked ? "Locked" : "Unlocked"
      period_since_creation_in_days = var.immutability_period_in_days
    }
  }

  tags = var.tags

  # A storage account the deploy agent cannot reach hangs against the SDK's
  # default 60-minute timeout instead of failing. Fail in 15 minutes instead.
  timeouts {
    create = "15m"
    update = "15m"
  }
}

resource "azurerm_storage_container" "documents" {
  name                  = var.documents_container_name
  storage_account_id    = azurerm_storage_account.this.id
  container_access_type = "private"
}

# Container-level, time-based WORM. Only created when
# immutability_mode = "container". Left unlocked unless immutability_locked is
# true: an unlocked policy can be shortened or removed, a locked one cannot.
resource "azurerm_storage_container_immutability_policy" "documents" {
  count = var.immutability_mode == "container" ? 1 : 0

  storage_container_resource_manager_id = azurerm_storage_container.documents.id
  immutability_period_in_days           = var.immutability_period_in_days
  locked                                = var.immutability_locked
}

# Microsoft Defender for Storage on this account only, with on-upload malware
# scanning. Billed per storage account per month plus per GB scanned; the cap
# below bounds the scanning part. See docs/operations/infrastructure.md.
resource "azurerm_security_center_storage_defender" "this" {
  count = var.defender_enabled ? 1 : 0

  storage_account_id                          = azurerm_storage_account.this.id
  override_subscription_settings_enabled      = true
  malware_scanning_on_upload_enabled          = true
  malware_scanning_on_upload_cap_gb_per_month = var.malware_scanning_cap_gb_per_month
  sensitive_data_discovery_enabled            = false
}

# -- Diagnostics ----------------------------------------------------------
# The account resource itself only emits metrics; read/write/delete logs live
# on the blob service sub-resource.
resource "azurerm_monitor_diagnostic_setting" "account" {
  name                       = "diag-${var.name}"
  target_resource_id         = azurerm_storage_account.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_metric {
    category = "Transaction"
  }
}

resource "azurerm_monitor_diagnostic_setting" "blob" {
  name                       = "diag-${var.name}-blob"
  target_resource_id         = "${azurerm_storage_account.this.id}/blobServices/default"
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log {
    category_group = "allLogs"
  }

  enabled_metric {
    category = "Transaction"
  }
}
