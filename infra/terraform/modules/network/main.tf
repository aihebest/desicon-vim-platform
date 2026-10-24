###############################################################################
# Desicon VIM Platform -- network module (optional)
#
# Only instantiated when enable_private_networking = true (the environment
# root uses count on this module). It creates:
#
#   - A virtual network with two subnets:
#       snet-app : delegated to App Service. The web app and function app
#                  "integrate" here, so their OUTBOUND calls (to SQL, Storage,
#                  Key Vault, Document Intelligence) leave from a private
#                  address inside the VNet.
#       snet-pe  : holds the private endpoints -- private IP addresses that
#                  stand in for each data service inside the VNet.
#   - Private DNS zones (privatelink.*), linked to the VNet, so that the normal
#     hostname (e.g. sql-vim-prod-weu-xxxx.database.windows.net) resolves to
#     the private IP from inside the VNet instead of the public one.
#   - One private endpoint per entry in var.private_endpoints.
#
# With this in place the data services can have public network access turned
# off entirely. Inbound traffic to the web app itself is unaffected: staff
# still reach https://<app>.azurewebsites.net over the internet (Entra sign-in
# protects it).
###############################################################################

terraform {
  required_version = ">= 1.9"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = ">= 4.54, < 5.11"
    }
  }
}

locals {
  # Zone names are fixed by Azure for the public cloud.
  dns_zones = {
    sql       = "privatelink.database.windows.net"
    blob      = "privatelink.blob.core.windows.net"
    queue     = "privatelink.queue.core.windows.net"
    table     = "privatelink.table.core.windows.net"
    vault     = "privatelink.vaultcore.azure.net"
    cognitive = "privatelink.cognitiveservices.azure.com"
  }
}

resource "azurerm_virtual_network" "this" {
  name                = var.vnet_name
  resource_group_name = var.resource_group_name
  location            = var.location
  address_space       = var.address_space

  tags = var.tags
}

# -- Network security groups ---------------------------------------------------
# No custom rules: Azure's default rules (allow within VNet, deny inbound from
# the internet) are what we want. They exist so traffic can be logged and so
# rules can be added later without re-creating subnets.
resource "azurerm_network_security_group" "app" {
  name                = "nsg-${var.vnet_name}-app"
  resource_group_name = var.resource_group_name
  location            = var.location
  tags                = var.tags
}

resource "azurerm_network_security_group" "pe" {
  name                = "nsg-${var.vnet_name}-pe"
  resource_group_name = var.resource_group_name
  location            = var.location
  tags                = var.tags
}

resource "azurerm_subnet" "app" {
  name                 = "snet-app"
  resource_group_name  = var.resource_group_name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = [var.app_subnet_prefix]

  delegation {
    name = "app-service"
    service_delegation {
      name    = "Microsoft.Web/serverFarms"
      actions = ["Microsoft.Network/virtualNetworks/subnets/action"]
    }
  }
}

resource "azurerm_subnet" "pe" {
  name                 = "snet-pe"
  resource_group_name  = var.resource_group_name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = [var.pe_subnet_prefix]

  # Lets the NSG apply to private endpoint traffic.
  private_endpoint_network_policies = "Enabled"
}

resource "azurerm_subnet_network_security_group_association" "app" {
  subnet_id                 = azurerm_subnet.app.id
  network_security_group_id = azurerm_network_security_group.app.id
}

resource "azurerm_subnet_network_security_group_association" "pe" {
  subnet_id                 = azurerm_subnet.pe.id
  network_security_group_id = azurerm_network_security_group.pe.id
}

# -- Private DNS ------------------------------------------------------------------
resource "azurerm_private_dns_zone" "this" {
  for_each = local.dns_zones

  name                = each.value
  resource_group_name = var.resource_group_name
  tags                = var.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "this" {
  for_each = local.dns_zones

  name                  = "link-${var.vnet_name}-${each.key}"
  resource_group_name   = var.resource_group_name
  private_dns_zone_name = azurerm_private_dns_zone.this[each.key].name
  virtual_network_id    = azurerm_virtual_network.this.id
  registration_enabled  = false
  tags                  = var.tags
}

# -- Private endpoints ------------------------------------------------------------
# Keys of var.private_endpoints must be static strings in the caller (they
# are used as for_each keys); the resource IDs can be unknown until apply.
resource "azurerm_private_endpoint" "this" {
  for_each = var.private_endpoints

  name                = "pe-${var.name_suffix}-${each.key}"
  resource_group_name = var.resource_group_name
  location            = var.location
  subnet_id           = azurerm_subnet.pe.id

  private_service_connection {
    name                           = "psc-${var.name_suffix}-${each.key}"
    private_connection_resource_id = each.value.resource_id
    subresource_names              = [each.value.subresource_name]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [azurerm_private_dns_zone.this[each.value.dns_zone_key].id]
  }

  tags = var.tags

  depends_on = [azurerm_private_dns_zone_virtual_network_link.this]
}

# -- Diagnostics ------------------------------------------------------------------
resource "azurerm_monitor_diagnostic_setting" "vnet" {
  name                       = "diag-${var.vnet_name}"
  target_resource_id         = azurerm_virtual_network.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log {
    category_group = "allLogs"
  }

  enabled_metric {
    category = "AllMetrics"
  }
}

resource "azurerm_monitor_diagnostic_setting" "nsg" {
  for_each = {
    app = azurerm_network_security_group.app.id
    pe  = azurerm_network_security_group.pe.id
  }

  name                       = "diag-nsg-${each.key}"
  target_resource_id         = each.value
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log {
    category_group = "allLogs"
  }
}
