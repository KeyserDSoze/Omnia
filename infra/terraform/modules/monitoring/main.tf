variable "log_analytics_name" { type = string }
variable "app_insights_name" { type = string }
variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "retention_days" {
  type    = number
  default = 30
}
variable "daily_cap_gb" {
  description = "Ingestion cap per day for both resources: the cost ceiling of monitoring."
  type        = number
  default     = 0.5
}
variable "tags" {
  type    = map(string)
  default = {}
}

resource "azurerm_log_analytics_workspace" "this" {
  name                = var.log_analytics_name
  resource_group_name = var.resource_group_name
  location            = var.location
  sku                 = "PerGB2018"
  retention_in_days   = var.retention_days
  daily_quota_gb      = var.daily_cap_gb
  tags                = var.tags
}

resource "azurerm_application_insights" "this" {
  name                         = var.app_insights_name
  resource_group_name          = var.resource_group_name
  location                     = var.location
  workspace_id                 = azurerm_log_analytics_workspace.this.id
  application_type             = "web"
  retention_in_days            = var.retention_days
  daily_data_cap_in_gb         = var.daily_cap_gb
  local_authentication_enabled = true
  tags                         = var.tags
}

output "log_analytics_workspace_id" { value = azurerm_log_analytics_workspace.this.id }
output "app_insights_id" { value = azurerm_application_insights.this.id }
output "app_insights_connection_string" {
  value     = azurerm_application_insights.this.connection_string
  sensitive = true
}
