output "role_assignment_ids" {
  description = "Map of role assignment IDs keyed by assignment name."
  value       = { for k, ra in azurerm_role_assignment.this : k => ra.id }
}
