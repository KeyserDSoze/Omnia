variable "resource_group_id" { type = string }
variable "name" { type = string }
variable "amount_eur" {
  description = "Monthly budget of the resource group."
  type        = number
}
variable "alert_email" {
  description = "Budget notifications need a recipient; empty creates no budget."
  type        = string
  default     = ""
}
variable "start_date" {
  description = "First day of a month, e.g. 2026-11-01T00:00:00Z."
  type        = string
}

resource "azurerm_consumption_budget_resource_group" "this" {
  count             = var.alert_email == "" ? 0 : 1
  name              = var.name
  resource_group_id = var.resource_group_id
  amount            = var.amount_eur
  time_grain        = "Monthly"

  time_period {
    start_date = var.start_date
  }

  notification {
    enabled        = true
    threshold      = 50
    operator       = "GreaterThan"
    threshold_type = "Actual"
    contact_emails = [var.alert_email]
  }

  notification {
    enabled        = true
    threshold      = 80
    operator       = "GreaterThan"
    threshold_type = "Actual"
    contact_emails = [var.alert_email]
  }

  notification {
    enabled        = true
    threshold      = 100
    operator       = "GreaterThan"
    threshold_type = "Forecasted"
    contact_emails = [var.alert_email]
  }

  lifecycle {
    ignore_changes = [time_period]
  }
}
