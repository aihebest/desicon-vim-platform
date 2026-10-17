###############################################################################
# Providers and remote state -- prod environment
#
# Provider pin: azurerm ~> 4.54. 4.54.0 is the first 4.x release whose
# linux_web_app / linux_function_app accept dotnet_version = "10.0"; an older
# 4.x provider rejects it at plan time.
###############################################################################

terraform {
  required_version = ">= 1.9"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.54"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # Partial configuration. The storage account, container and key are passed
  # at init time:
  #   terraform init -backend-config=backend.hcl
  # (copy backend.hcl.example to backend.hcl). The state storage account is
  # created once by infra/scripts/bootstrap-state.ps1 -- Terraform cannot
  # create the storage account that holds its own state.
  #
  # use_azuread_auth: read/write state with your Entra identity (az login, or
  # the GitHub OIDC identity in CI) and the "Storage Blob Data Contributor"
  # role, not with a storage account key.
  backend "azurerm" {
    use_azuread_auth = true
  }
}

provider "azurerm" {
  subscription_id = var.subscription_id

  # The storage accounts have shared keys disabled. This makes the provider
  # use Entra ID for any storage data-plane call instead of trying an account
  # key first and failing with KeyBasedAuthenticationNotPermitted.
  storage_use_azuread = true

  features {
    key_vault {
      # Never purge on destroy: purge protection forbids it anyway, and a
      # recoverable vault is the point.
      purge_soft_delete_on_destroy    = false
      recover_soft_deleted_key_vaults = true
    }

    resource_group {
      prevent_deletion_if_contains_resources = true
    }

    cognitive_account {
      purge_soft_delete_on_destroy = false
    }
  }
}
