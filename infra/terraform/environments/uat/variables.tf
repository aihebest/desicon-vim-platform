###############################################################################
# Variables -- uat environment
#
# Defaults here are the sizing for uat. Values without defaults are
# identifiers specific to Desicon's tenant; supply them in
# uat.auto.tfvars locally (see example.auto.tfvars.example) or as
# TF_VAR_* environment variables in GitHub Actions (see deploy.yml).
###############################################################################

# -- Subscription and placement ------------------------------------------------------
variable "subscription_id" {
  description = "Azure subscription ID to deploy into."
  type        = string
}

variable "environment" {
  description = "Environment name. Fixed for this root; used in names and tags."
  type        = string
  default     = "uat"

  validation {
    condition     = var.environment == "uat"
    error_message = "This root deploys the uat environment only."
  }
}

variable "location" {
  description = <<-EOT
    Azure region. West Europe is the default because every service used here
    (App Service .NET 10 on Linux, Azure SQL serverless, Document Intelligence
    with the prebuilt invoice model, Defender for Storage malware scanning) is
    available there. South Africa North is geographically closer to Port
    Harcourt and may matter for latency or data-residency policy -- check
    each service's availability there (especially Document Intelligence and
    Defender malware scanning) before switching. Changing the region after the
    first apply re-creates every resource.
  EOT
  type        = string
  default     = "westeurope"
}

variable "enable_private_networking" {
  description = "Create a VNet, private endpoints and private DNS, and turn off public network access on SQL, Storage, Key Vault and Document Intelligence. Database migrations then need a runner inside the VNet (see docs)."
  type        = bool
  default     = false
}

variable "vnet_address_space" {
  description = "VNet address space (only used when enable_private_networking = true)."
  type        = list(string)
  default     = ["10.61.0.0/24"]
}

variable "app_subnet_prefix" {
  description = "App Service integration subnet (only used when enable_private_networking = true)."
  type        = string
  default     = "10.61.0.0/26"
}

variable "pe_subnet_prefix" {
  description = "Private endpoint subnet (only used when enable_private_networking = true)."
  type        = string
  default     = "10.61.0.64/27"
}

# -- Identity (created outside Terraform; see docs/operations/infrastructure.md) --------
variable "sql_admin_group_name" {
  description = "Display name of the Entra security group that administers SQL, e.g. grp-vim-sql-admins-uat."
  type        = string
}

variable "sql_admin_group_object_id" {
  description = "Object ID of the SQL admin Entra security group."
  type        = string
}

variable "entra_workbench_client_id" {
  description = "Application (client) ID of the 'Desicon VIM Workbench (uat)' app registration created by infra/scripts/bootstrap-entra-app.ps1."
  type        = string
}

variable "keyvault_secrets_officer_object_ids" {
  description = "Entra object IDs (prefer one group) allowed to set Key Vault secret values (none are needed today; Business Central signs in without a secret)."
  type        = list(string)
  default     = []
}

# -- Business Central -------------------------------------------------------------------
variable "business_central_tenant_id" {
  description = "The Entra tenant Business Central lives in: desiconerp.com (separate from the tenant the platform runs in)."
  type        = string
  default     = "desiconerp.com"
}

variable "business_central_environment" {
  description = "Business Central environment name, e.g. 'Sandbox' for dev/uat and 'Production' for prod."
  type        = string
  default     = "Sandbox"
}

variable "business_central_company_id" {
  description = "Business Central company ID (GUID). Optional: leave empty to look the company up by business_central_company_name."
  type        = string
  default     = ""
}

variable "business_central_company_name" {
  description = "Business Central company name, looked up once to find its ID."
  type        = string
  default     = "Desicon Engineering Ltd"
}

variable "business_central_client_id" {
  description = "Client ID of the VIM Business Central connector app registration (infra/scripts/bootstrap-bc-connector.ps1). Empty = Business Central checks stay off."
  type        = string
  default     = ""
}

# -- Mailbox ----------------------------------------------------------------------------
variable "mailbox_user_id" {
  description = "UPN of the shared invoice mailbox the function app reads, e.g. invoices@example.com. Use a separate test mailbox outside prod."
  type        = string
}

# -- Sizing: App Service ------------------------------------------------------------------
variable "app_service_sku_name" {
  description = "App Service plan SKU shared by the web and function apps."
  type        = string
  default     = "B1"
}

variable "app_service_worker_count" {
  description = "Number of plan instances."
  type        = number
  default     = 1
}

variable "app_service_zone_balancing_enabled" {
  description = "Spread instances across availability zones (Premium v3, 2+ instances, region must support it). Re-creates the plan if changed."
  type        = bool
  default     = false
}

# -- Sizing: SQL ------------------------------------------------------------------------
variable "sql_sku_name" {
  description = "Database SKU. GP_S_Gen5_* = serverless (auto-pause); GP_Gen5_* = provisioned."
  type        = string
  default     = "GP_S_Gen5_1"
}

variable "sql_max_size_gb" {
  description = "Maximum database size in GB."
  type        = number
  default     = 32
}

variable "sql_auto_pause_delay_in_minutes" {
  description = "Serverless only: idle minutes before auto-pause. -1 disables auto-pause (the first query after a pause takes up to a minute)."
  type        = number
  default     = 60
}

variable "sql_min_capacity" {
  description = "Serverless only: minimum vCores while running."
  type        = number
  default     = 0.5
}

variable "sql_zone_redundant" {
  description = "Zone-redundant database (not available for every SKU/region)."
  type        = bool
  default     = false
}

variable "sql_backup_storage_redundancy" {
  description = "Backup redundancy: Local, Zone, Geo or GeoZone."
  type        = string
  default     = "Local"
}

variable "sql_short_term_retention_days" {
  description = "Point-in-time restore window, 1-35 days."
  type        = number
  default     = 7
}

variable "sql_long_term_retention" {
  description = "Long-term retention backups (ISO 8601, PT0S = off). Agree periods with Finance."
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

variable "sql_allowed_ip_addresses" {
  description = "Bare public IPv4 addresses allowed through the SQL firewall (only while the public endpoint is on), e.g. the admin PC that runs create-sql-users.sql."
  type        = list(string)
  default     = []
}

# -- Sizing: storage, Document Intelligence, logs ------------------------------------------
variable "storage_replication_type" {
  description = "Documents storage replication: LRS, ZRS, GZRS..."
  type        = string
  default     = "LRS"
}

variable "documents_immutability_mode" {
  description = "none, container or account. See modules/storage/variables.tf before changing."
  type        = string
  default     = "none"
}

variable "documents_immutability_period_in_days" {
  description = "Immutability retention period in days."
  type        = number
  default     = 2555
}

variable "documents_immutability_locked" {
  description = "Lock the immutability policy (IRREVERSIBLE)."
  type        = bool
  default     = false
}

variable "defender_for_storage_enabled" {
  description = "Microsoft Defender for Storage with malware scanning on upload for the documents account."
  type        = bool
  default     = true
}

variable "malware_scanning_cap_gb_per_month" {
  description = "Monthly malware-scanning cap in GB (-1 = no cap)."
  type        = number
  default     = 10
}

variable "docintel_sku_name" {
  description = "Document Intelligence SKU: S0 (pay per page) or F0 (free, limited, no private endpoint)."
  type        = string
  default     = "S0"
}

variable "docintel_location" {
  description = "Region for Document Intelligence if it must differ from var.location (null = same region)."
  type        = string
  default     = null
}

variable "log_retention_days" {
  description = "Log Analytics retention in days."
  type        = number
  default     = 30
}

variable "alert_emails" {
  description = "Comma-separated email addresses for operational alerts (GitHub variable ALERT_EMAILS). Empty: no alerts."
  type        = string
  default     = ""
}

variable "log_daily_quota_gb" {
  description = "Daily Log Analytics ingestion cap in GB (-1 = unlimited)."
  type        = number
  default     = 2
}

variable "vim_settings" {
  description = "Business settings for the apps: workbench URL, Finance Manager and AP emails, review deadline, Business Central switch."
  type = object({
    workbench_url            = optional(string, "")
    finance_manager_email    = optional(string, "")
    accounts_payable_email   = optional(string, "")
    verification_hours       = optional(number, 48)
    verification_basis       = optional(string, "CalendarHours")
    business_central_enabled = optional(bool, false)
  })
  default = {}
}
