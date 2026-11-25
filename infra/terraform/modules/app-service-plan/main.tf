variable "name" { type = string }
variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "sku_name" {
  description = "B2 hosts the API and Keycloak with room for the JVM; B1 is too tight for both."
  type        = string
  default     = "B2"
}
variable "tags" {
  type    = map(string)
  default = {}
}

resource "azurerm_service_plan" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  os_type             = "Linux"
  sku_name            = var.sku_name
  tags                = var.tags
}

output "id" { value = azurerm_service_plan.this.id }
output "name" { value = azurerm_service_plan.this.name }
