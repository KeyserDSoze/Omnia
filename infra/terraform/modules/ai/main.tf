variable "name" { type = string }
variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "model_name" { type = string }
variable "model_version" { type = string }
variable "capacity" {
  type        = number
  description = "Thousands of tokens per minute; pay-as-you-go, nothing is billed while idle."
  default     = 50
}
variable "tags" {
  type    = map(string)
  default = {}
}

resource "azurerm_cognitive_account" "this" {
  name                  = var.name
  resource_group_name   = var.resource_group_name
  location              = var.location
  kind                  = "AIServices"
  sku_name              = "S0"
  custom_subdomain_name = var.name
  local_auth_enabled    = true
  tags                  = var.tags
}

resource "azurerm_cognitive_deployment" "model" {
  name                 = var.model_name
  cognitive_account_id = azurerm_cognitive_account.this.id

  model {
    format  = "OpenAI"
    name    = var.model_name
    version = var.model_version
  }

  sku {
    name     = "GlobalStandard"
    capacity = var.capacity
  }
}

# OpenAI-compatible base URL used by Llm:Endpoint.
output "endpoint" { value = "https://${var.name}.openai.azure.com/openai/v1" }
output "model" { value = azurerm_cognitive_deployment.model.name }
output "primary_key" {
  value     = azurerm_cognitive_account.this.primary_access_key
  sensitive = true
}
