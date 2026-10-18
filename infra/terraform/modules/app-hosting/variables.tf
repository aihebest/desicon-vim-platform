variable "resource_group_name" {
  description = "Resource group to create the hosting resources in."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "plan_name" {
  description = "App Service plan name, e.g. asp-vim-dev-weu."
  type        = string
}

variable "web_app_name" {
  description = "Web app name (globally unique; becomes <name>.azurewebsites.net)."
  type        = string
}

variable "function_app_name" {
  description = "Function app name (globally unique)."
  type        = string
}

variable "host_storage_account_name" {
  description = "Functions host storage account name (3-24 lowercase letters/digits, globally unique)."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9]{3,24}$", var.host_storage_account_name))
    error_message = "Storage account names are 3-24 characters, lowercase letters and digits only."
  }
}

variable "host_storage_public_network_access_enabled" {
  description = "Whether the host storage account's public endpoint accepts traffic. false when private networking is enabled (private endpoints for blob/queue/table are then required)."
  type        = bool
}

variable "sku_name" {
  description = "App Service plan SKU. B1 (Basic) is the cheapest that supports always_on and VNet integration; P0v3/P1v3 (Premium v3) add zone balancing, more memory and deployment slots."
  type        = string
  default     = "B1"
}

variable "worker_count" {
  description = "Number of plan instances. With more than one, ARR affinity keeps each Blazor circuit on one instance."
  type        = number
  default     = 1
}

variable "zone_balancing_enabled" {
  description = "Spread instances across availability zones (Premium v3, worker_count >= 2). Changing it re-creates the plan."
  type        = bool
  default     = false
}

variable "always_on" {
  description = "Keep the web app loaded (avoids a slow first request after idle). Not available on Free/Shared tiers."
  type        = bool
  default     = true
}

variable "app_subnet_id" {
  description = "Subnet for regional VNet integration (outbound). null = no VNet integration."
  type        = string
  default     = null
}

variable "dotnet_environment" {
  description = "Value for ASPNETCORE_ENVIRONMENT / AZURE_FUNCTIONS_ENVIRONMENT. Selects appsettings.<value>.json."
  type        = string
}

variable "sql_connection_string" {
  description = "Password-less SQL connection string (ConnectionStrings__VimDb)."
  type        = string
}

variable "documents_blob_endpoint" {
  description = "Primary blob endpoint of the documents account (Storage__BlobServiceUri)."
  type        = string
}

variable "documents_container_name" {
  description = "Documents container name (Storage__DocumentsContainer)."
  type        = string
}

variable "docintel_endpoint" {
  description = "Document Intelligence endpoint (DocumentIntelligence__Endpoint)."
  type        = string
}

variable "key_vault_uri" {
  description = "Key Vault URI with trailing slash, used to build the bc-client-secret reference."
  type        = string
}

variable "bc_client_secret_name" {
  description = "Name of the Key Vault secret holding the Business Central client secret. Its value is set by an administrator, never by Terraform."
  type        = string
  default     = "bc-client-secret"
}

variable "business_central" {
  description = "Business Central API connection details (non-secret). The secret itself comes from Key Vault."
  type = object({
    tenant_id   = string
    environment = string
    company_id  = string
    client_id   = string
  })
}

variable "app_insights_connection_string" {
  description = "Application Insights connection string."
  type        = string
  sensitive   = true
}

variable "entra_tenant_id" {
  description = "Entra tenant ID for staff sign-in (AzureAd__TenantId)."
  type        = string
}

variable "entra_workbench_client_id" {
  description = "Application (client) ID of the workbench app registration created by infra/scripts/bootstrap-entra-app.ps1 (AzureAd__ClientId)."
  type        = string
}

variable "mailbox_user_id" {
  description = "UPN of the shared invoice mailbox the function app reads (Mailbox__UserId), e.g. invoices@example.com."
  type        = string
}

variable "log_analytics_workspace_id" {
  description = "Log Analytics workspace for diagnostic logs."
  type        = string
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
}

variable "vim_settings" {
  description = "Business settings read by both apps (section \"Vim\" in .NET configuration)."
  type = object({
    workbench_url            = optional(string, "")
    finance_manager_email    = optional(string, "")
    accounts_payable_email   = optional(string, "")
    verification_hours       = optional(number, 48)
    verification_basis       = optional(string, "CalendarHours")
    business_central_enabled = optional(bool, false)
  })
  default = {}

  validation {
    condition     = contains(["CalendarHours", "WorkingHours"], var.vim_settings.verification_basis)
    error_message = "verification_basis must be CalendarHours or WorkingHours."
  }
}

variable "function_schedules" {
  description = "NCRONTAB schedules for the timer functions. The Functions host has no fallback: a missing setting stops that function loading."
  type = object({
    mailbox_intake   = optional(string, "0 */5 * * * *")
    sla_monitor      = optional(string, "0 */15 * * * *")
    outbox_dispatch  = optional(string, "0 */1 * * * *")
    scan_result_sync = optional(string, "30 */1 * * * *")
  })
  default = {}
}
