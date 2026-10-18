variable "name" {
  description = "Cognitive account name, e.g. di-vim-dev-weu-a1b2."
  type        = string
}

variable "custom_subdomain_name" {
  description = "Globally unique custom subdomain (required for Entra auth and private endpoints). Usually the same as name."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group to create the account in."
  type        = string
}

variable "location" {
  description = "Azure region. Check Document Intelligence availability (and the prebuilt invoice model) in the chosen region."
  type        = string
}

variable "sku_name" {
  description = "S0 (standard, pay per page) or F0 (free, limited pages, no private endpoints)."
  type        = string
  default     = "S0"

  validation {
    condition     = contains(["S0", "F0"], var.sku_name)
    error_message = "sku_name must be S0 or F0."
  }
}

variable "public_network_access_enabled" {
  description = "Whether the public endpoint accepts traffic. false when private networking is enabled."
  type        = bool
}

variable "log_analytics_workspace_id" {
  description = "Log Analytics workspace for diagnostic logs."
  type        = string
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
}
