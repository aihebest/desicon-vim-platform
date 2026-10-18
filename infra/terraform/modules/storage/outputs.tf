output "id" {
  description = "Resource ID of the documents storage account."
  value       = azurerm_storage_account.this.id
}

output "name" {
  description = "Name of the documents storage account."
  value       = azurerm_storage_account.this.name
}

output "primary_blob_endpoint" {
  description = "Primary blob endpoint, e.g. https://stvimdevweua1b2.blob.core.windows.net/. Set as Storage__BlobServiceUri."
  value       = azurerm_storage_account.this.primary_blob_endpoint
}

output "documents_container_name" {
  description = "Name of the case documents container."
  value       = azurerm_storage_container.documents.name
}
