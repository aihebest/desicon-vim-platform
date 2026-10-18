variable "log_analytics_name" {
  description = "Name of the Log Analytics workspace, e.g. law-vim-dev-weu."
  type        = string
}

variable "app_insights_name" {
  description = "Name of the Application Insights component, e.g. appi-vim-dev-weu."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group to create the monitoring resources in."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "retention_in_days" {
  description = "Log Analytics retention in days (30-730). Retention beyond the included period is billed."
  type        = number
  default     = 30

  validation {
    condition     = var.retention_in_days >= 30 && var.retention_in_days <= 730
    error_message = "retention_in_days must be between 30 and 730."
  }
}

variable "daily_quota_gb" {
  description = "Daily ingestion cap in GB. -1 means unlimited. Keep a cap in dev so a logging loop cannot run up a bill."
  type        = number
  default     = -1
}

variable "app_insights_local_auth_enabled" {
  description = "Whether Application Insights accepts telemetry authenticated only by the connection string's ingestion key. Set false once the app sends telemetry with its managed identity."
  type        = bool
  default     = true
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
}
