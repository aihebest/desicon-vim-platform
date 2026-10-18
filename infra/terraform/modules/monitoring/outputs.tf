output "log_analytics_workspace_id" {
  description = "Resource ID of the Log Analytics workspace. Every diagnostic setting points here."
  value       = azurerm_log_analytics_workspace.this.id
}

output "log_analytics_workspace_name" {
  description = "Name of the Log Analytics workspace."
  value       = azurerm_log_analytics_workspace.this.name
}

output "app_insights_id" {
  description = "Resource ID of the Application Insights component."
  value       = azurerm_application_insights.this.id
}

output "app_insights_connection_string" {
  description = "Application Insights connection string, set as APPLICATIONINSIGHTS_CONNECTION_STRING on both apps."
  value       = azurerm_application_insights.this.connection_string
  sensitive   = true
}
