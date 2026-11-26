variable "subscription_id" {
  description = "Azure subscription that receives everything."
  type        = string
}

variable "location" {
  type    = string
  default = "northeurope"
}

variable "environment" {
  type    = string
  default = "demo"
}

variable "pages_origin" {
  description = "Origin of the site that hosts the web app (GitHub Pages with a custom domain)."
  type        = string
  default     = "https://taoai.me"
}

variable "pages_base_path" {
  type    = string
  default = ""
}

variable "keycloak_hostname" {
  description = "Public sign-in hostname (CNAME to the Keycloak app + asuid TXT record); empty uses the azurewebsites.net name."
  type        = string
  default     = "auth.taoai.me"
}

variable "app_service_sku" {
  type    = string
  default = "B2"
}

variable "postgres_sku" {
  type    = string
  default = "B_Standard_B1ms"
}

variable "alert_email" {
  description = "Recipient of alerts and budget notifications; empty creates the rules without notifications."
  type        = string
  default     = ""
}

variable "monthly_budget_eur" {
  type    = number
  default = 120
}

variable "budget_start_date" {
  type    = string
  default = "2026-11-01T00:00:00Z"
}

variable "microsoft_client_id" {
  description = "App registration used to sign in with Microsoft and to read Microsoft 365 (NOT the deployment identity)."
  type        = string
  default     = ""
}

variable "microsoft_client_secret" {
  type      = string
  default   = ""
  sensitive = true
}

variable "microsoft_tenant" {
  type    = string
  default = "common"
}

variable "google_client_id" {
  type    = string
  default = ""
}

variable "google_client_secret" {
  type      = string
  default   = ""
  sensitive = true
}

variable "llm_endpoint" {
  type    = string
  default = ""
}

variable "llm_model" {
  type    = string
  default = ""
}

variable "llm_api_key" {
  type      = string
  default   = ""
  sensitive = true
}
