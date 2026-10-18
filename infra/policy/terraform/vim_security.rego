package terraform.vim.security

# Policy-as-code gate over the Terraform PLAN (terraform show -json tfplan),
# not the .tf source. Values that are only known at plan time -- variables,
# module outputs, conditionals like "public access = !private" -- are checked
# as they will actually be applied.
#
# Checkov (CI iac-scan job) covers generic Azure misconfiguration. These rules
# encode the decisions this platform has made and must never drift from:
#
#   1. No anonymous/public blob access on any storage account.
#   2. Storage shared keys disabled (identity-only access).
#   3. HTTPS only (storage, web app, function app).
#   4. Minimum TLS 1.2 (storage, SQL, web app, function app).
#   5. Azure SQL is Microsoft Entra-only (no SQL logins/passwords).
#   6. Key Vault has purge protection (and RBAC authorisation).
#   7. In prod, no data service accepts public network traffic.
#   8. Document Intelligence has API keys (local auth) disabled.
#
# Run locally (PowerShell, from the repo root):
#   terraform -chdir=infra/terraform/environments/dev plan -out tfplan
#   terraform -chdir=infra/terraform/environments/dev show -json tfplan > plan.json
#   conftest test --policy infra/policy/terraform --namespace terraform.vim.security plan.json
#
# Unit tests: conftest verify --policy infra/policy/terraform
#
# The --namespace flag matters: conftest only evaluates the "main" namespace
# by default, and a policy in any other package silently evaluates zero rules
# and reports success.

import rego.v1

# -- Helpers -------------------------------------------------------------------

# Resources that will exist after apply: creates, updates, replacements and
# unchanged ("no-op") resources -- an existing resource that is already
# non-compliant must still be reported. Pure deletes are skipped.
resources contains r if {
	some r in input.resource_changes
	r.change.actions != ["delete"]
}

resources_of(kind) := {r |
	some r in resources
	r.type == kind
}

after(r) := r.change.after

environment := input.variables.environment.value

is_prod if environment == "prod"

site_config(r) := sc if {
	some sc in after(r).site_config
}

# -- 1. No public blob access ----------------------------------------------------
deny contains msg if {
	some r in resources_of("azurerm_storage_account")
	after(r).allow_nested_items_to_be_public != false
	msg := sprintf("%s: allow_nested_items_to_be_public must be false (no anonymous blob access to vendor documents).", [r.address])
}

deny contains msg if {
	some r in resources_of("azurerm_storage_container")
	after(r).container_access_type != "private"
	msg := sprintf("%s: container_access_type must be \"private\".", [r.address])
}

# -- 2. Shared keys disabled ------------------------------------------------------
deny contains msg if {
	some r in resources_of("azurerm_storage_account")
	after(r).shared_access_key_enabled != false
	msg := sprintf("%s: shared_access_key_enabled must be false. Access is by managed identity and RBAC only.", [r.address])
}

# -- 3. HTTPS only -------------------------------------------------------------------
deny contains msg if {
	some r in resources_of("azurerm_storage_account")
	after(r).https_traffic_only_enabled != true
	msg := sprintf("%s: https_traffic_only_enabled must be true.", [r.address])
}

app_kinds := {"azurerm_linux_web_app", "azurerm_linux_function_app"}

deny contains msg if {
	some kind in app_kinds
	some r in resources_of(kind)
	after(r).https_only != true
	msg := sprintf("%s: https_only must be true.", [r.address])
}

deny contains msg if {
	some kind in app_kinds
	some r in resources_of(kind)
	site_config(r).ftps_state != "Disabled"
	msg := sprintf("%s: site_config.ftps_state must be \"Disabled\".", [r.address])
}

# -- 4. Minimum TLS 1.2 ---------------------------------------------------------------
deny contains msg if {
	some r in resources_of("azurerm_storage_account")
	after(r).min_tls_version != "TLS1_2"
	msg := sprintf("%s: min_tls_version must be \"TLS1_2\".", [r.address])
}

deny contains msg if {
	some r in resources_of("azurerm_mssql_server")
	after(r).minimum_tls_version != "1.2"
	msg := sprintf("%s: minimum_tls_version must be \"1.2\".", [r.address])
}

deny contains msg if {
	some kind in app_kinds
	some r in resources_of(kind)
	site_config(r).minimum_tls_version != "1.2"
	msg := sprintf("%s: site_config.minimum_tls_version must be \"1.2\".", [r.address])
}

deny contains msg if {
	some kind in app_kinds
	some r in resources_of(kind)
	not site_config(r)
	msg := sprintf("%s: site_config is missing, so TLS/FTPS settings cannot be verified.", [r.address])
}

# -- 5. SQL Entra-only authentication -------------------------------------------------
deny contains msg if {
	some r in resources_of("azurerm_mssql_server")
	not sql_entra_only(r)
	msg := sprintf("%s: azuread_administrator.azuread_authentication_only must be true (no SQL logins or passwords).", [r.address])
}

sql_entra_only(r) if {
	some admin in after(r).azuread_administrator
	admin.azuread_authentication_only == true
}

# Azure gives every SQL server an internal administrator_login, even an
# Entra-only one, and Terraform reads it back after the first apply. That
# placeholder cannot sign in (password authentication is off), so it is
# accepted when it is unchanged on an Entra-only server. Anything else --
# setting a login in configuration (it appears on create, or as a change),
# or a login on a server that is not Entra-only -- is denied.
deny contains msg if {
	some r in resources_of("azurerm_mssql_server")
	login := object.get(after(r), "administrator_login", null)
	login != null
	login != ""
	not sql_placeholder_login(r, login)
	msg := sprintf("%s: administrator_login must not be set; the server has no SQL admin login.", [r.address])
}

sql_placeholder_login(r, login) if {
	sql_entra_only(r)
	object.get(object.get(r.change, "before", {}), "administrator_login", null) == login
}

# -- 6. Key Vault purge protection --------------------------------------------------------
deny contains msg if {
	some r in resources_of("azurerm_key_vault")
	after(r).purge_protection_enabled != true
	msg := sprintf("%s: purge_protection_enabled must be true.", [r.address])
}

deny contains msg if {
	some r in resources_of("azurerm_key_vault")
	after(r).rbac_authorization_enabled != true
	msg := sprintf("%s: rbac_authorization_enabled must be true (no access policies).", [r.address])
}

# -- 7. Prod: no public network access on data services ------------------------------------
data_service_kinds := {
	"azurerm_mssql_server",
	"azurerm_key_vault",
	"azurerm_storage_account",
	"azurerm_cognitive_account",
}

deny contains msg if {
	is_prod
	some kind in data_service_kinds
	some r in resources_of(kind)
	after(r).public_network_access_enabled != false
	msg := sprintf("%s: public_network_access_enabled must be false in prod. Reach it through its private endpoint (enable_private_networking = true).", [r.address])
}

# -- 8. Document Intelligence: no API keys ----------------------------------------------
deny contains msg if {
	some r in resources_of("azurerm_cognitive_account")
	after(r).local_auth_enabled != false
	msg := sprintf("%s: local_auth_enabled must be false (managed identity only, no API keys).", [r.address])
}
