variable "name" { type = string }
variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "service_plan_id" { type = string }
variable "identity_id" { type = string }
variable "identity_client_id" { type = string }
variable "acr_login_server" { type = string }
variable "image" {
  description = "Repository:tag inside the registry."
  type        = string
}
variable "port" {
  type    = number
  default = 8080
}
variable "health_check_path" {
  type    = string
  default = null
}
variable "start_time_limit_seconds" {
  type    = number
  default = 230
}
variable "app_settings" {
  type      = map(string)
  default   = {}
  sensitive = true
}
variable "log_analytics_workspace_id" { type = string }
variable "tags" {
  type    = map(string)
  default = {}
}

# One always-on Linux container web app: pulls from the registry with a managed identity, reads
# secrets through Key Vault references and ships console logs (never HTTP logs, which carry query
# strings) to Log Analytics.
resource "azurerm_linux_web_app" "this" {
  name                                           = var.name
  resource_group_name                            = var.resource_group_name
  location                                       = var.location
  service_plan_id                                = var.service_plan_id
  https_only                                     = true
  public_network_access_enabled                  = true
  client_affinity_enabled                        = false
  key_vault_reference_identity_id                = var.identity_id
  ftp_publish_basic_authentication_enabled       = false
  webdeploy_publish_basic_authentication_enabled = false
  tags                                           = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [var.identity_id]
  }

  site_config {
    always_on                                     = true
    ftps_state                                    = "Disabled"
    http2_enabled                                 = true
    minimum_tls_version                           = "1.2"
    health_check_path                             = var.health_check_path
    health_check_eviction_time_in_min             = var.health_check_path == null ? null : 10
    container_registry_use_managed_identity       = true
    container_registry_managed_identity_client_id = var.identity_client_id

    application_stack {
      docker_registry_url = "https://${var.acr_login_server}"
      docker_image_name   = var.image
    }
  }

  app_settings = merge({
    WEBSITES_PORT                       = tostring(var.port)
    WEBSITES_ENABLE_APP_SERVICE_STORAGE = "false"
    WEBSITES_CONTAINER_START_TIME_LIMIT = tostring(var.start_time_limit_seconds)
    DOCKER_ENABLE_CI                    = "false"
  }, var.app_settings)

  lifecycle {
    # Images are rolled by the deployment (push + restart), not by Terraform.
    ignore_changes = [site_config[0].application_stack[0].docker_image_name]
  }
}

resource "azurerm_monitor_diagnostic_setting" "logs" {
  name                       = "console-and-platform"
  target_resource_id         = azurerm_linux_web_app.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log { category = "AppServiceConsoleLogs" }
  enabled_log { category = "AppServicePlatformLogs" }
}

output "id" { value = azurerm_linux_web_app.this.id }
output "name" { value = azurerm_linux_web_app.this.name }
output "default_hostname" { value = azurerm_linux_web_app.this.default_hostname }
output "url" { value = "https://${azurerm_linux_web_app.this.default_hostname}" }
