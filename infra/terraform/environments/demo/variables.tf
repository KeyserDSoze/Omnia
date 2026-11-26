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
  description = "OpenAI-compatible base URL of an external model; empty uses the Foundry model created here."
  type        = string
  default     = ""
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

variable "deploy_ai" {
  description = "Create an Azure AI Foundry account with one pay-as-you-go model for the assistant."
  type        = bool
  default     = true
}

variable "ai_location" {
  type    = string
  default = "swedencentral"
}

variable "ai_model" {
  type    = string
  default = "gpt-6-luna"
}

variable "ai_model_version" {
  type    = string
  default = "2026-09-22"
}

variable "ai_capacity" {
  description = "Thousands of tokens per minute."
  type        = number
  default     = 50
}

variable "api_hostname" {
  description = "Public API hostname (CNAME to the API app + asuid TXT record). Set it only after the DNS records exist: the certificate request waits for them."
  type        = string
  default     = ""
}
