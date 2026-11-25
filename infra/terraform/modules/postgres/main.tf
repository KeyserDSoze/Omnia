variable "name" { type = string }
variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "sku_name" {
  type    = string
  default = "B_Standard_B1ms"
}
variable "storage_mb" {
  type    = number
  default = 32768
}
variable "postgres_version" {
  type    = string
  default = "17"
}
variable "databases" {
  type    = list(string)
  default = ["tao", "keycloak"]
}
variable "tags" {
  type    = map(string)
  default = {}
}

resource "random_password" "admin" {
  length  = 32
  special = false
}

resource "azurerm_postgresql_flexible_server" "this" {
  name                          = var.name
  resource_group_name           = var.resource_group_name
  location                      = var.location
  version                       = var.postgres_version
  sku_name                      = var.sku_name
  storage_mb                    = var.storage_mb
  auto_grow_enabled             = false
  administrator_login           = "taoadmin"
  administrator_password        = random_password.admin.result
  backup_retention_days         = 7
  geo_redundant_backup_enabled  = false
  public_network_access_enabled = true
  tags                          = var.tags

  lifecycle {
    ignore_changes = [zone, high_availability[0].standby_availability_zone]
  }
}

# pgvector must be allow-listed before the API migration runs CREATE EXTENSION.
resource "azurerm_postgresql_flexible_server_configuration" "extensions" {
  name      = "azure.extensions"
  server_id = azurerm_postgresql_flexible_server.this.id
  value     = "VECTOR"
}

resource "azurerm_postgresql_flexible_server_database" "this" {
  for_each  = toset(var.databases)
  name      = each.value
  server_id = azurerm_postgresql_flexible_server.this.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

# Azure services (the web apps) reach the server; nothing else is open by default.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  name             = "AllowAzureServices"
  server_id        = azurerm_postgresql_flexible_server.this.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

output "id" { value = azurerm_postgresql_flexible_server.this.id }
output "name" { value = azurerm_postgresql_flexible_server.this.name }
output "fqdn" { value = azurerm_postgresql_flexible_server.this.fqdn }
output "administrator_login" { value = azurerm_postgresql_flexible_server.this.administrator_login }
output "administrator_password" {
  value     = random_password.admin.result
  sensitive = true
}
