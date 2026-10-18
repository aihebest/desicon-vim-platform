output "id" {
  description = "Resource ID of the Document Intelligence account."
  value       = azurerm_cognitive_account.this.id
}

output "name" {
  description = "Name of the Document Intelligence account."
  value       = azurerm_cognitive_account.this.name
}

output "endpoint" {
  description = "Endpoint URL. Set as DocumentIntelligence__Endpoint."
  value       = azurerm_cognitive_account.this.endpoint
}
