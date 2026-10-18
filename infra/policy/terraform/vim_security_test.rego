package terraform.vim.security_test

# Unit tests for vim_security.rego. Run with:
#   conftest verify --policy infra/policy/terraform
#
# Each test builds a minimal plan document. compliant_* helpers describe the
# platform's intended configuration; each "deny" test breaks exactly one thing
# and asserts that a message mentioning it comes back.

import data.terraform.vim.security
import rego.v1

plan(env, changes) := {
	"variables": {"environment": {"value": env}},
	"resource_changes": changes,
}

change(type, name, after) := {
	"address": sprintf("%s.%s", [type, name]),
	"type": type,
	"change": {"actions": ["create"], "after": after},
}

compliant_storage := {
	"allow_nested_items_to_be_public": false,
	"shared_access_key_enabled": false,
	"https_traffic_only_enabled": true,
	"min_tls_version": "TLS1_2",
	"public_network_access_enabled": false,
}

compliant_sql := {
	"minimum_tls_version": "1.2",
	"administrator_login": null,
	"public_network_access_enabled": false,
	"azuread_administrator": [{"azuread_authentication_only": true}],
}

compliant_kv := {
	"purge_protection_enabled": true,
	"rbac_authorization_enabled": true,
	"public_network_access_enabled": false,
}

compliant_cog := {
	"local_auth_enabled": false,
	"public_network_access_enabled": false,
}

compliant_app := {
	"https_only": true,
	"site_config": [{"minimum_tls_version": "1.2", "ftps_state": "Disabled"}],
}

compliant_changes := [
	change("azurerm_storage_account", "docs", compliant_storage),
	change("azurerm_storage_container", "docs", {"container_access_type": "private"}),
	change("azurerm_mssql_server", "sql", compliant_sql),
	change("azurerm_key_vault", "kv", compliant_kv),
	change("azurerm_cognitive_account", "di", compliant_cog),
	change("azurerm_linux_web_app", "web", compliant_app),
	change("azurerm_linux_function_app", "func", compliant_app),
]

denies(input_doc) := msgs if {
	msgs := security.deny with input as input_doc
}

any_contains(msgs, fragment) if {
	some m in msgs
	contains(m, fragment)
}

# -- The intended configuration passes in every environment -----------------------
test_compliant_prod_plan_passes if {
	count(denies(plan("prod", compliant_changes))) == 0
}

test_compliant_dev_plan_passes if {
	count(denies(plan("dev", compliant_changes))) == 0
}

# -- 1. Public blob access -----------------------------------------------------------
test_public_blob_access_denied if {
	bad := object.union(compliant_storage, {"allow_nested_items_to_be_public": true})
	any_contains(denies(plan("dev", [change("azurerm_storage_account", "docs", bad)])), "allow_nested_items_to_be_public")
}

test_public_container_denied if {
	msgs := denies(plan("dev", [change("azurerm_storage_container", "docs", {"container_access_type": "blob"})]))
	any_contains(msgs, "container_access_type")
}

# -- 2. Shared keys ------------------------------------------------------------------
test_shared_keys_denied if {
	bad := object.union(compliant_storage, {"shared_access_key_enabled": true})
	any_contains(denies(plan("dev", [change("azurerm_storage_account", "docs", bad)])), "shared_access_key_enabled")
}

# -- 3. HTTPS only -------------------------------------------------------------------
test_storage_http_denied if {
	bad := object.union(compliant_storage, {"https_traffic_only_enabled": false})
	any_contains(denies(plan("dev", [change("azurerm_storage_account", "docs", bad)])), "https_traffic_only_enabled")
}

test_web_app_http_denied if {
	bad := object.union(compliant_app, {"https_only": false})
	any_contains(denies(plan("dev", [change("azurerm_linux_web_app", "web", bad)])), "https_only")
}

test_function_app_ftps_denied if {
	bad := object.union(compliant_app, {"site_config": [{"minimum_tls_version": "1.2", "ftps_state": "AllAllowed"}]})
	any_contains(denies(plan("dev", [change("azurerm_linux_function_app", "func", bad)])), "ftps_state")
}

# -- 4. TLS 1.2 ------------------------------------------------------------------------
test_storage_tls10_denied if {
	bad := object.union(compliant_storage, {"min_tls_version": "TLS1_0"})
	any_contains(denies(plan("dev", [change("azurerm_storage_account", "docs", bad)])), "min_tls_version")
}

test_sql_tls11_denied if {
	bad := object.union(compliant_sql, {"minimum_tls_version": "1.1"})
	any_contains(denies(plan("dev", [change("azurerm_mssql_server", "sql", bad)])), "minimum_tls_version")
}

test_web_app_tls10_denied if {
	bad := object.union(compliant_app, {"site_config": [{"minimum_tls_version": "1.0", "ftps_state": "Disabled"}]})
	any_contains(denies(plan("dev", [change("azurerm_linux_web_app", "web", bad)])), "minimum_tls_version")
}

# -- 5. SQL Entra-only ----------------------------------------------------------------
test_sql_without_entra_only_denied if {
	bad := object.union(compliant_sql, {"azuread_administrator": [{"azuread_authentication_only": false}]})
	any_contains(denies(plan("dev", [change("azurerm_mssql_server", "sql", bad)])), "azuread_authentication_only")
}

test_sql_without_entra_admin_denied if {
	bad := object.union(compliant_sql, {"azuread_administrator": []})
	any_contains(denies(plan("dev", [change("azurerm_mssql_server", "sql", bad)])), "azuread_authentication_only")
}

test_sql_admin_login_denied if {
	bad := object.union(compliant_sql, {"administrator_login": "sqladmin"})
	any_contains(denies(plan("dev", [change("azurerm_mssql_server", "sql", bad)])), "administrator_login")
}

# -- 6. Key Vault ------------------------------------------------------------------------
test_kv_without_purge_protection_denied if {
	bad := object.union(compliant_kv, {"purge_protection_enabled": false})
	any_contains(denies(plan("dev", [change("azurerm_key_vault", "kv", bad)])), "purge_protection_enabled")
}

test_kv_access_policies_denied if {
	bad := object.union(compliant_kv, {"rbac_authorization_enabled": false})
	any_contains(denies(plan("dev", [change("azurerm_key_vault", "kv", bad)])), "rbac_authorization_enabled")
}

# -- 7. Prod public network access ---------------------------------------------------------
test_prod_public_sql_denied if {
	bad := object.union(compliant_sql, {"public_network_access_enabled": true})
	any_contains(denies(plan("prod", [change("azurerm_mssql_server", "sql", bad)])), "must be false in prod")
}

test_prod_public_storage_denied if {
	bad := object.union(compliant_storage, {"public_network_access_enabled": true})
	any_contains(denies(plan("prod", [change("azurerm_storage_account", "docs", bad)])), "must be false in prod")
}

test_dev_public_storage_allowed if {
	ok := object.union(compliant_storage, {"public_network_access_enabled": true})
	count(denies(plan("dev", [change("azurerm_storage_account", "docs", ok)]))) == 0
}

# -- 8. Document Intelligence keys ---------------------------------------------------------
test_docintel_local_auth_denied if {
	bad := object.union(compliant_cog, {"local_auth_enabled": true})
	any_contains(denies(plan("dev", [change("azurerm_cognitive_account", "di", bad)])), "local_auth_enabled")
}

# -- Deletes are ignored --------------------------------------------------------------------
test_deleted_resource_ignored if {
	bad := {
		"address": "azurerm_storage_account.old",
		"type": "azurerm_storage_account",
		"change": {"actions": ["delete"], "after": null},
	}
	count(denies(plan("prod", [bad]))) == 0
}
