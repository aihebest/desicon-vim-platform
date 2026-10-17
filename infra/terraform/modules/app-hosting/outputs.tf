output "plan_id" {
  description = "Resource ID of the App Service plan."
  value       = azurerm_service_plan.this.id
}

output "web_app_id" {
  description = "Resource ID of the web app."
  value       = azurerm_linux_web_app.web.id
}

output "web_app_name" {
  description = "Web app name. Its managed identity has the same display name (used in CREATE USER [...] FROM EXTERNAL PROVIDER)."
  value       = azurerm_linux_web_app.web.name
}

output "web_app_default_hostname" {
  description = "Default hostname, e.g. app-vim-dev-weu-a1b2.azurewebsites.net."
  value       = azurerm_linux_web_app.web.default_hostname
}

output "web_app_principal_id" {
  description = "Object ID of the web app's system-assigned managed identity."
  value       = azurerm_linux_web_app.web.identity[0].principal_id
}

output "function_app_id" {
  description = "Resource ID of the function app."
  value       = azurerm_linux_function_app.func.id
}

output "function_app_name" {
  description = "Function app name. Its managed identity has the same display name."
  value       = azurerm_linux_function_app.func.name
}

output "function_app_principal_id" {
  description = "Object ID of the function app's system-assigned managed identity (needed by grant-mailbox-access.ps1)."
  value       = azurerm_linux_function_app.func.identity[0].principal_id
}

output "host_storage_account_id" {
  description = "Resource ID of the Functions host storage account."
  value       = azurerm_storage_account.host.id
}

output "host_storage_account_name" {
  description = "Name of the Functions host storage account."
  value       = azurerm_storage_account.host.name
}
