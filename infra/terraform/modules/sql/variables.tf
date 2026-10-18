variable "name" {
  description = "SQL logical server name (globally unique), e.g. sql-vim-dev-weu-a1b2."
  type        = string
}

variable "database_name" {
  description = "Application database name."
  type        = string
  default     = "VimDb"
}

variable "resource_group_name" {
  description = "Resource group to create the server in."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "tenant_id" {
  description = "Entra tenant ID of the administrator group."
  type        = string
}

variable "entra_admin_group_name" {
  description = "Display name of the Entra security group that administers the SQL server, e.g. grp-vim-sql-admins-dev."
  type        = string
}

variable "entra_admin_group_object_id" {
  description = "Object ID of that Entra security group."
  type        = string
}

variable "sku_name" {
  description = "Database SKU. GP_S_Gen5_1 / GP_S_Gen5_2 = General Purpose serverless (auto-pauses, good for dev); GP_Gen5_2 = provisioned General Purpose."
  type        = string
  default     = "GP_S_Gen5_1"
}

variable "max_size_gb" {
  description = "Maximum database size in GB."
  type        = number
  default     = 32
}

variable "auto_pause_delay_in_minutes" {
  description = "Serverless only: minutes of inactivity before the database pauses (and stops billing for compute). -1 disables auto-pause. Minimum 15 when enabled."
  type        = number
  default     = 60
}

variable "min_capacity" {
  description = "Serverless only: minimum vCores while running."
  type        = number
  default     = 0.5
}

variable "zone_redundant" {
  description = "Spread database replicas across availability zones (prod)."
  type        = bool
  default     = false
}

variable "backup_storage_redundancy" {
  description = "Backup storage redundancy: Local, Zone, Geo or GeoZone."
  type        = string
  default     = "Local"

  validation {
    condition     = contains(["Local", "Zone", "Geo", "GeoZone"], var.backup_storage_redundancy)
    error_message = "backup_storage_redundancy must be Local, Zone, Geo or GeoZone."
  }
}

variable "short_term_retention_days" {
  description = "Point-in-time restore window in days (1-35)."
  type        = number
  default     = 7
}

variable "long_term_retention" {
  description = "Long-term retention (LTR) backups, ISO 8601 durations. PT0S disables that tier. Agree the periods with Finance (statutory record keeping)."
  type = object({
    weekly_retention  = string
    monthly_retention = string
    yearly_retention  = string
    week_of_year      = number
  })
  default = {
    weekly_retention  = "PT0S"
    monthly_retention = "PT0S"
    yearly_retention  = "PT0S"
    week_of_year      = 1
  }
}

variable "public_network_access_enabled" {
  description = "Whether the server's public endpoint accepts traffic. false when private networking is enabled."
  type        = bool
}

variable "allow_azure_services" {
  description = "Create the 0.0.0.0 'Allow Azure services' firewall rule (public endpoint only). Needed in dev where the apps are not VNet-integrated."
  type        = bool
  default     = false
}

variable "allowed_ip_addresses" {
  description = "Bare public IPv4 addresses allowed through the SQL firewall, e.g. the admin workstation that runs create-sql-users.sql."
  type        = list(string)
  default     = []

  validation {
    condition     = alltrue([for v in var.allowed_ip_addresses : can(regex("^([0-9]{1,3}\\.){3}[0-9]{1,3}$", v))])
    error_message = "Each entry must be a bare IPv4 address, e.g. 203.0.113.5 (no CIDR suffix)."
  }
}

variable "log_analytics_workspace_id" {
  description = "Log Analytics workspace for audit and diagnostic logs."
  type        = string
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
}
