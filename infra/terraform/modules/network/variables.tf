variable "vnet_name" {
  description = "Virtual network name, e.g. vnet-vim-prod-weu."
  type        = string
}

variable "name_suffix" {
  description = "Suffix used in private endpoint names, e.g. vim-prod-weu (gives pe-vim-prod-weu-sql)."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group to create the network resources in."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "address_space" {
  description = "VNet address space. Must not overlap Desicon's on-premises or other Azure ranges if peering/VPN is ever added."
  type        = list(string)
  default     = ["10.60.0.0/24"]
}

variable "app_subnet_prefix" {
  description = "App Service integration subnet. /26 (64 addresses) is the minimum Microsoft recommends for scale-out headroom."
  type        = string
  default     = "10.60.0.0/26"
}

variable "pe_subnet_prefix" {
  description = "Private endpoint subnet."
  type        = string
  default     = "10.60.0.64/27"
}

variable "private_endpoints" {
  description = "Private endpoints to create. Key = short name (static string). dns_zone_key is one of sql, blob, queue, table, vault, cognitive."
  type = map(object({
    resource_id      = string
    subresource_name = string
    dns_zone_key     = string
  }))
  default = {}

  validation {
    condition     = alltrue([for pe in values(var.private_endpoints) : contains(["sql", "blob", "queue", "table", "vault", "cognitive"], pe.dns_zone_key)])
    error_message = "dns_zone_key must be one of sql, blob, queue, table, vault, cognitive."
  }
}

variable "log_analytics_workspace_id" {
  description = "Log Analytics workspace for diagnostic logs."
  type        = string
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
}
