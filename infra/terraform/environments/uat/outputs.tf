output "resource_group_name" {
  description = "Resource group holding every resource in this environment."
  value       = azurerm_resource_group.main.name
}

output "web_app_name" {
  description = "Web app name (deploy target; also the managed identity's display name for create-sql-users.sql)."
  value       = module.app_hosting.web_app_name
}

output "web_app_url" {
  description = "Workbench URL. Add <url>/signin-oidc as a redirect URI on the Entra app registration."
  value       = "https://${module.app_hosting.web_app_default_hostname}"
}

output "web_app_principal_id" {
  description = "Object ID of the web app's managed identity."
  value       = module.app_hosting.web_app_principal_id
}

output "function_app_name" {
  description = "Function app name (deploy target; also the managed identity's display name)."
  value       = module.app_hosting.function_app_name
}

output "function_app_principal_id" {
  description = "Object ID of the function app's managed identity. Pass to infra/scripts/grant-mailbox-access.ps1."
  value       = module.app_hosting.function_app_principal_id
}

output "sql_server_name" {
  description = "SQL logical server name."
  value       = module.sql.server_name
}

output "sql_server_fqdn" {
  description = "SQL server FQDN, for create-sql-users.sql and the migration step."
  value       = module.sql.server_fqdn
}

output "sql_database_name" {
  description = "Application database name."
  value       = module.sql.database_name
}

output "key_vault_name" {
  description = "Key Vault name. Set the bc-client-secret secret here."
  value       = module.keyvault.name
}

output "key_vault_uri" {
  description = "Key Vault URI."
  value       = module.keyvault.uri
}

output "documents_storage_account_name" {
  description = "Documents storage account name."
  value       = module.storage.name
}

output "docintel_endpoint" {
  description = "Document Intelligence endpoint."
  value       = module.docintel.endpoint
}

output "private_networking_enabled" {
  description = "Whether data services are reachable only through private endpoints."
  value       = var.enable_private_networking
}
