output "server_id" {
  description = "Resource ID of the SQL logical server (private endpoint target)."
  value       = azurerm_mssql_server.this.id
}

output "server_name" {
  description = "SQL logical server name."
  value       = azurerm_mssql_server.this.name
}

output "server_fqdn" {
  description = "Fully qualified domain name, e.g. sql-vim-dev-weu-a1b2.database.windows.net."
  value       = azurerm_mssql_server.this.fully_qualified_domain_name
}

output "database_name" {
  description = "Application database name."
  value       = azurerm_mssql_database.this.name
}

output "connection_string" {
  description = "Password-less connection string (managed identity via Active Directory Default). Set as ConnectionStrings__VimDb."
  value       = "Server=tcp:${azurerm_mssql_server.this.fully_qualified_domain_name},1433;Database=${azurerm_mssql_database.this.name};Authentication=Active Directory Default;Encrypt=True;"
}
