variable "name" {
  description = "Storage account name (3-24 lowercase letters/digits, globally unique), e.g. stvimdevweua1b2."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9]{3,24}$", var.name))
    error_message = "Storage account names are 3-24 characters, lowercase letters and digits only."
  }
}

variable "resource_group_name" {
  description = "Resource group to create the account in."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "replication_type" {
  description = "Replication: LRS (dev), ZRS or GZRS (prod, survives a zone or region outage)."
  type        = string
  default     = "LRS"

  validation {
    condition     = contains(["LRS", "ZRS", "GRS", "GZRS", "RAGRS", "RAGZRS"], var.replication_type)
    error_message = "replication_type must be one of LRS, ZRS, GRS, GZRS, RAGRS, RAGZRS."
  }
}

variable "documents_container_name" {
  description = "Container for case documents. The app reads this from Storage__DocumentsContainer."
  type        = string
  default     = "case-documents"
}

variable "soft_delete_retention_days" {
  description = "Days a deleted blob or container can be recovered."
  type        = number
  default     = 30
}

variable "public_network_access_enabled" {
  description = "Whether the account's public endpoint accepts traffic. false when private networking is enabled."
  type        = bool
}

variable "public_default_action" {
  description = "network_rules default action used only while public access is enabled."
  type        = string
  default     = "Allow"

  validation {
    condition     = contains(["Allow", "Deny"], var.public_default_action)
    error_message = "public_default_action must be Allow or Deny."
  }
}

variable "ip_rules" {
  description = "Public IPv4 addresses or ranges allowed through the storage firewall. Bare IPs only; Azure rejects /31 and /32 suffixes here."
  type        = list(string)
  default     = []
}

variable "immutability_mode" {
  description = <<-EOT
    Write-once-read-many (WORM) protection for case documents:
      "none"      - no immutability (default). Versioning + soft delete still apply.
      "container" - time-based retention policy on the case-documents container.
      "account"   - account-level, version-level immutability. FORCES A NEW
                    STORAGE ACCOUNT if changed after creation.
    Agree the retention period with Finance and Legal before enabling.
  EOT
  type        = string
  default     = "none"

  validation {
    condition     = contains(["none", "container", "account"], var.immutability_mode)
    error_message = "immutability_mode must be none, container or account."
  }
}

variable "immutability_period_in_days" {
  description = "Retention period for the immutability policy, in days (e.g. 2555 for roughly 7 years)."
  type        = number
  default     = 2555
}

variable "immutability_locked" {
  description = "Lock the immutability policy. IRREVERSIBLE: a locked policy can only be lengthened, never removed or shortened."
  type        = bool
  default     = false
}

variable "defender_enabled" {
  description = "Enable Microsoft Defender for Storage with on-upload malware scanning on this account."
  type        = bool
  default     = true
}

variable "malware_scanning_cap_gb_per_month" {
  description = "Maximum GB scanned per month (-1 for no cap). Scanning stops for the rest of the month when reached, so size it above expected volumes."
  type        = number
  default     = 50
}

variable "log_analytics_workspace_id" {
  description = "Log Analytics workspace for diagnostic logs."
  type        = string
}

variable "tags" {
  description = "Tags applied to every resource."
  type        = map(string)
}
