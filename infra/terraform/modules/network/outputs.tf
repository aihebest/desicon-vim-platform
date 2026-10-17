output "vnet_id" {
  description = "Resource ID of the virtual network."
  value       = azurerm_virtual_network.this.id
}

output "app_subnet_id" {
  description = "Subnet the web app and function app integrate with (outbound)."
  value       = azurerm_subnet.app.id
}

output "pe_subnet_id" {
  description = "Subnet holding the private endpoints."
  value       = azurerm_subnet.pe.id
}

output "private_dns_zone_ids" {
  description = "Map of private DNS zone IDs keyed by sql, blob, queue, table, vault, cognitive."
  value       = { for k, z in azurerm_private_dns_zone.this : k => z.id }
}
