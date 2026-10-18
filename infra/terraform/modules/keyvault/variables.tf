variable "name" {
  description = "Key Vault name (3-24 characters, globally unique), e.g. kv-vim-dev-weu-a1b2."
  type        = string

  validation {
    condition     = can(regex("^[a-zA-Z][a-zA-Z0-9-]{1,22}[a-zA-Z0-9]$", var.name))
    error_message = "Key Vault names are 3-24 characters, start with a letter, and contain only letters, digits and hyphens."
  }
}

variable "resource_group_name" {
  description = "Resource group to create the vault in."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "tenant_id" {
  description = "Entra tenant ID that authenticates requests to the vault."
  type        = string
}

variable "public_network_access_enabled" {
  description = "Whether the vault's public endpoint accepts traffic. false when private networking is enabled."
  type        = bool
}

variable "public_default_action" {
  description = "network_acls default action used only while public access is enabled. Allow is required for App Service Key Vault references when the app is not VNet-integrated."
  type        = string
  default     = "Allow"

  validation {
    condition     = contains(["Allow", "Deny"], var.public_default_action)
    error_message = "public_default_action must be Allow or Deny."
  }
}

variable "ip_rules" {
  description = "Public IPs/CIDRs allowed through the vault firewall (only meaningful when public_default_action is Deny)."
  type        = list(string)
  default     = []
}

variable "secrets_officer_object_ids" {
  description = "Entra object IDs (prefer a group) granted Key Vault Secrets Officer so they can set bc-client-secret by hand."
  type        = list(string)
  default     = []
}

variable "log_analytics_workspace_id" {
  description = "Log Analytics workspace for diagnostic logs."
  type        = string
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
}
