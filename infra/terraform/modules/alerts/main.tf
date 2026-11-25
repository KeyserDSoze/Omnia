variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "name_prefix" { type = string }
variable "alert_email" {
  description = "Where alerts go. Empty creates the rules without a notification target."
  type        = string
  default     = ""
}
variable "web_app_ids" {
  description = "Web app label => resource id."
  type        = map(string)
}
variable "service_plan_id" { type = string }
variable "postgres_id" { type = string }
variable "tags" {
  type    = map(string)
  default = {}
}

locals {
  has_target = var.alert_email != ""
}

resource "azurerm_monitor_action_group" "this" {
  count               = local.has_target ? 1 : 0
  name                = "${var.name_prefix}-alerts"
  resource_group_name = var.resource_group_name
  short_name          = "tao-alerts"
  tags                = var.tags

  email_receiver {
    name                    = "owner"
    email_address           = var.alert_email
    use_common_alert_schema = true
  }
}

# Server errors on any web app: a broken release or a lost database.
resource "azurerm_monitor_metric_alert" "http_5xx" {
  for_each            = var.web_app_ids
  name                = "${var.name_prefix}-${each.key}-5xx"
  resource_group_name = var.resource_group_name
  scopes              = [each.value]
  description         = "More than 5 server errors in 15 minutes on ${each.key}."
  severity            = 2
  frequency           = "PT5M"
  window_size         = "PT15M"
  tags                = var.tags

  criteria {
    metric_namespace = "Microsoft.Web/sites"
    metric_name      = "Http5xx"
    aggregation      = "Total"
    operator         = "GreaterThan"
    threshold        = 5
  }

  dynamic "action" {
    for_each = azurerm_monitor_action_group.this
    content { action_group_id = action.value.id }
  }
}

resource "azurerm_monitor_metric_alert" "plan_cpu" {
  name                = "${var.name_prefix}-plan-cpu"
  resource_group_name = var.resource_group_name
  scopes              = [var.service_plan_id]
  description         = "The App Service plan stays above 85% CPU for 15 minutes."
  severity            = 3
  frequency           = "PT5M"
  window_size         = "PT15M"
  tags                = var.tags

  criteria {
    metric_namespace = "Microsoft.Web/serverfarms"
    metric_name      = "CpuPercentage"
    aggregation      = "Average"
    operator         = "GreaterThan"
    threshold        = 85
  }

  dynamic "action" {
    for_each = azurerm_monitor_action_group.this
    content { action_group_id = action.value.id }
  }
}

resource "azurerm_monitor_metric_alert" "plan_memory" {
  name                = "${var.name_prefix}-plan-memory"
  resource_group_name = var.resource_group_name
  scopes              = [var.service_plan_id]
  description         = "Keycloak and the API are close to the plan's memory."
  severity            = 3
  frequency           = "PT5M"
  window_size         = "PT15M"
  tags                = var.tags

  criteria {
    metric_namespace = "Microsoft.Web/serverfarms"
    metric_name      = "MemoryPercentage"
    aggregation      = "Average"
    operator         = "GreaterThan"
    threshold        = 90
  }

  dynamic "action" {
    for_each = azurerm_monitor_action_group.this
    content { action_group_id = action.value.id }
  }
}

resource "azurerm_monitor_metric_alert" "postgres_storage" {
  name                = "${var.name_prefix}-db-storage"
  resource_group_name = var.resource_group_name
  scopes              = [var.postgres_id]
  description         = "The database storage is above 80%: the server does not grow by itself."
  severity            = 2
  frequency           = "PT15M"
  window_size         = "PT1H"
  tags                = var.tags

  criteria {
    metric_namespace = "Microsoft.DBforPostgreSQL/flexibleServers"
    metric_name      = "storage_percent"
    aggregation      = "Maximum"
    operator         = "GreaterThan"
    threshold        = 80
  }

  dynamic "action" {
    for_each = azurerm_monitor_action_group.this
    content { action_group_id = action.value.id }
  }
}

resource "azurerm_monitor_metric_alert" "postgres_cpu" {
  name                = "${var.name_prefix}-db-cpu"
  resource_group_name = var.resource_group_name
  scopes              = [var.postgres_id]
  description         = "The burstable database is out of CPU credits or saturated."
  severity            = 3
  frequency           = "PT5M"
  window_size         = "PT30M"
  tags                = var.tags

  criteria {
    metric_namespace = "Microsoft.DBforPostgreSQL/flexibleServers"
    metric_name      = "cpu_percent"
    aggregation      = "Average"
    operator         = "GreaterThan"
    threshold        = 85
  }

  dynamic "action" {
    for_each = azurerm_monitor_action_group.this
    content { action_group_id = action.value.id }
  }
}

output "action_group_id" { value = one(azurerm_monitor_action_group.this[*].id) }
