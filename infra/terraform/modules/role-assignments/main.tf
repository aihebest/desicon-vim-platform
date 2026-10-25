###############################################################################
# Desicon VIM Platform -- role assignments module
#
# Who may do what, data-plane only, by managed identity. These replace every
# password/key a traditional deployment would put in configuration.
#
#   Identity        Role                              Scope
#   --------------  --------------------------------  ---------------------------
#   web + func      Storage Blob Data Contributor     documents storage account
#   web + func      Cognitive Services User           Document Intelligence
#   web + func      Key Vault Secrets User            Key Vault (read secrets)
#   func            Storage Blob Data Owner           Functions host storage
#   func            Storage Queue Data Contributor    Functions host storage
#   func            Storage Table Data Contributor    Functions host storage
#
# The vendor portal gets one narrow custom role: add NEW files to the documents
# container (vendor uploads). It cannot read, list, overwrite or delete any file,
# so a portal bug can never expose another vendor's documents.
#
# Microsoft Graph mailbox access for the function app is NOT granted here --
# see infra/scripts/grant-mailbox-access.ps1 and the note in
# docs/operations/infrastructure.md.
#
# for_each keys are static strings so that Terraform can plan them before the
# identities exist (their principal IDs are unknown until apply).
#
# Role assignments take a few minutes to propagate. On the very first deploy
# the function host may log storage authorisation errors for a short while
# after apply; it recovers on its own.
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
  assignments = {
    web_documents_blob = {
      scope     = var.documents_storage_account_id
      role      = "Storage Blob Data Contributor"
      principal = var.web_app_principal_id
    }
    func_documents_blob = {
      scope     = var.documents_storage_account_id
      role      = "Storage Blob Data Contributor"
      principal = var.function_app_principal_id
    }
    web_docintel = {
      scope     = var.docintel_account_id
      role      = "Cognitive Services User"
      principal = var.web_app_principal_id
    }
    func_docintel = {
      scope     = var.docintel_account_id
      role      = "Cognitive Services User"
      principal = var.function_app_principal_id
    }
    web_keyvault_secrets = {
      scope     = var.key_vault_id
      role      = "Key Vault Secrets User"
      principal = var.web_app_principal_id
    }
    func_keyvault_secrets = {
      scope     = var.key_vault_id
      role      = "Key Vault Secrets User"
      principal = var.function_app_principal_id
    }
    func_host_blob_owner = {
      scope     = var.host_storage_account_id
      role      = "Storage Blob Data Owner"
      principal = var.function_app_principal_id
    }
    func_host_queue = {
      scope     = var.host_storage_account_id
      role      = "Storage Queue Data Contributor"
      principal = var.function_app_principal_id
    }
    # Not strictly required for timer triggers, but the Functions host writes
    # diagnostic events to Table storage and logs warnings without it.
    func_host_table = {
      scope     = var.host_storage_account_id
      role      = "Storage Table Data Contributor"
      principal = var.function_app_principal_id
    }
  }
}

# Defender for Storage writes malware scan results as blob index tags. Reading
# tags is not part of "Storage Blob Data Contributor", and "Owner" grants far
# more (it can change access policies). This custom role grants only tag read,
# scoped to the documents account. Creating a role definition needs
# Microsoft.Authorization/roleDefinitions/write — see docs/operations/infrastructure.md.
resource "azurerm_role_definition" "blob_tag_reader" {
  name        = "VIM Blob Index Tag Reader (${var.documents_storage_account_name})"
  scope       = var.documents_storage_account_id
  description = "Read blob index tags (malware scan results) on the VIM documents account."

  permissions {
    data_actions = ["Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/read"]
  }

  assignable_scopes = [var.documents_storage_account_id]
}

resource "azurerm_role_assignment" "func_blob_tag_reader" {
  scope              = var.documents_storage_account_id
  role_definition_id = azurerm_role_definition.blob_tag_reader.role_definition_resource_id
  principal_id       = var.function_app_principal_id
  principal_type     = "ServicePrincipal"
}

resource "azurerm_role_assignment" "this" {
  for_each = local.assignments

  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = each.value.principal

  # Managed identities are service principals. Stating this avoids a Graph
  # lookup that can fail while a brand-new identity is still replicating.
  principal_type = "ServicePrincipal"
}

resource "azurerm_role_definition" "portal_blob_uploader" {
  count = var.portal_enabled ? 1 : 0

  name        = "VIM Vendor Upload (${var.documents_storage_account_name})"
  scope       = var.documents_storage_account_id
  description = "Create new blobs only (vendor portal uploads). No read, list or delete."

  permissions {
    data_actions = [
      "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write",
      "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/add/action",
    ]
  }

  assignable_scopes = [var.documents_storage_account_id]
}

resource "azurerm_role_assignment" "portal_blob_uploader" {
  count = var.portal_enabled ? 1 : 0

  scope              = "${var.documents_storage_account_id}/blobServices/default/containers/${var.documents_container_name}"
  role_definition_id = azurerm_role_definition.portal_blob_uploader[0].role_definition_resource_id
  principal_id       = var.portal_principal_id
  principal_type     = "ServicePrincipal"
}
