output "id" {
  description = "Resource ID of the Key Vault."
  value       = azurerm_key_vault.this.id
}

output "name" {
  description = "Name of the Key Vault."
  value       = azurerm_key_vault.this.name
}

output "uri" {
  description = "Vault URI, e.g. https://kv-vim-dev-weu-a1b2.vault.azure.net/ (with trailing slash)."
  value       = azurerm_key_vault.this.vault_uri
}
