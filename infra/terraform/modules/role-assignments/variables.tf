variable "web_app_principal_id" {
  description = "Object ID of the web app's system-assigned managed identity."
  type        = string
}

variable "function_app_principal_id" {
  description = "Object ID of the function app's system-assigned managed identity."
  type        = string
}

variable "documents_storage_account_id" {
  description = "Resource ID of the documents storage account."
  type        = string
}

variable "docintel_account_id" {
  description = "Resource ID of the Document Intelligence account."
  type        = string
}

variable "key_vault_id" {
  description = "Resource ID of the Key Vault."
  type        = string
}

variable "host_storage_account_id" {
  description = "Resource ID of the Functions host storage account."
  type        = string
}

variable "documents_storage_account_name" {
  description = "Name of the documents storage account (used to make the custom role name unique)."
  type        = string
}

variable "documents_container_name" {
  description = "Name of the case documents container (the vendor portal may only add files there)."
  type        = string
  default     = "case-documents"
}

variable "portal_enabled" {
  description = "Whether the vendor portal app exists (a plain bool so Terraform can plan before the identity exists)."
  type        = bool
  default     = false
}

variable "portal_principal_id" {
  description = "Object ID of the vendor portal's managed identity (null when the portal is off)."
  type        = string
  default     = null
}
