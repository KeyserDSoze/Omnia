data "azurerm_client_config" "current" {}

locals {
  suffix         = substr(sha1(var.subscription_id), 0, 5)
  prefix         = "tao-${var.environment}"
  api_app_name   = "${local.prefix}-${local.suffix}-api"
  kc_app_name    = "${local.prefix}-${local.suffix}-kc"
  kc_default_url = "https://${local.kc_app_name}.azurewebsites.net"
  kc_url         = var.keycloak_hostname == "" ? local.kc_default_url : "https://${var.keycloak_hostname}"
  issuer         = "${local.kc_url}/auth/realms/tao"
  app_url        = "${var.pages_origin}${var.pages_base_path}"
  social         = compact([var.microsoft_client_id != "" ? "microsoft" : "", var.google_client_id != "" ? "google" : ""])
  tags = {
    app         = "tao"
    environment = var.environment
    managed-by  = "terraform"
  }
  # Key Vault does not store an empty secret: an unset optional value is the literal "unset".
  or_unset = { for k, v in {
    microsoft_client_secret = var.microsoft_client_secret
    google_client_secret    = var.google_client_secret
    llm_api_key             = var.llm_api_key
  } : k => v == "" ? "unset" : v }
}

module "resource_group" {
  source   = "../../modules/resource-group"
  name     = "rg-${local.prefix}"
  location = var.location
  tags     = local.tags
}

module "monitoring" {
  source              = "../../modules/monitoring"
  log_analytics_name  = "log-${local.prefix}"
  app_insights_name   = "appi-${local.prefix}"
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  tags                = local.tags
}

module "identity_api" {
  source              = "../../modules/identity"
  name                = "id-${local.prefix}-api"
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  tags                = local.tags
}

module "identity_keycloak" {
  source              = "../../modules/identity"
  name                = "id-${local.prefix}-kc"
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  tags                = local.tags
}

module "registry" {
  source              = "../../modules/registry"
  name                = "acrtao${var.environment}${local.suffix}"
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  pull_principal_ids = {
    api      = module.identity_api.principal_id
    keycloak = module.identity_keycloak.principal_id
  }
  tags = local.tags
}

module "postgres" {
  source              = "../../modules/postgres"
  name                = "psql-${local.prefix}-${local.suffix}"
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  sku_name            = var.postgres_sku
  tags                = local.tags
}

# Credentials generated here, stored only in the state and in Key Vault.
resource "random_password" "app_db" {
  length  = 32
  special = false
}

resource "random_password" "retention_db" {
  length  = 32
  special = false
}

resource "random_password" "keycloak_db" {
  length  = 32
  special = false
}

resource "random_password" "keycloak_admin" {
  length  = 32
  special = false
}

# Envelope KEK. Losing it makes every sealed note and credential unreadable: it lives in Key Vault
# and in the Terraform state, and docs/security.md says to keep a copy off Azure.
resource "random_id" "key_ring" {
  byte_length = 32
}

locals {
  db_host = module.postgres.fqdn
  db_tail = "SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=10"
  secrets = {
    "db-admin-password"       = module.postgres.administrator_password
    "db-app-password"         = random_password.app_db.result
    "db-retention-password"   = random_password.retention_db.result
    "db-keycloak-password"    = random_password.keycloak_db.result
    "keycloak-admin-password" = random_password.keycloak_admin.result
    "db-connection-app"       = "Host=${local.db_host};Port=5432;Database=tao;Username=tao_app;Password=${random_password.app_db.result};${local.db_tail}"
    "db-connection-migration" = "Host=${local.db_host};Port=5432;Database=tao;Username=${module.postgres.administrator_login};Password=${module.postgres.administrator_password};${local.db_tail}"
    "db-connection-retention" = "Host=${local.db_host};Port=5432;Database=tao;Username=tao_retention;Password=${random_password.retention_db.result};${local.db_tail}"
    "key-ring-json"           = jsonencode({ activeKeyId = "k1", keys = { k1 = random_id.key_ring.b64_std } })
    "microsoft-client-secret" = local.or_unset.microsoft_client_secret
    "google-client-secret"    = local.or_unset.google_client_secret
    "llm-api-key"             = local.or_unset.llm_api_key
  }
}

module "key_vault" {
  source              = "../../modules/keyvault"
  name                = "kv-${local.prefix}-${local.suffix}"
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  tenant_id           = data.azurerm_client_config.current.tenant_id
  deployer_object_id  = data.azurerm_client_config.current.object_id
  reader_principal_ids = {
    api      = module.identity_api.principal_id
    keycloak = module.identity_keycloak.principal_id
  }
  secrets = local.secrets
  tags    = local.tags
}

module "service_plan" {
  source              = "../../modules/app-service-plan"
  name                = "asp-${local.prefix}"
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  sku_name            = var.app_service_sku
  tags                = local.tags
}

module "keycloak" {
  source                     = "../../modules/container-web-app"
  name                       = local.kc_app_name
  resource_group_name        = module.resource_group.name
  location                   = module.resource_group.location
  service_plan_id            = module.service_plan.id
  identity_id                = module.identity_keycloak.id
  identity_client_id         = module.identity_keycloak.client_id
  acr_login_server           = module.registry.login_server
  image                      = "tao-keycloak:latest"
  health_check_path          = "/auth/realms/tao"
  start_time_limit_seconds   = 900
  custom_hostname            = var.keycloak_hostname
  log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
  tags                       = local.tags

  app_settings = {
    KC_DB                       = "postgres"
    KC_DB_URL                   = "jdbc:postgresql://${local.db_host}:5432/keycloak?sslmode=require"
    KC_DB_USERNAME              = "keycloak"
    KC_DB_PASSWORD              = module.key_vault.secret_references["db-keycloak-password"]
    KC_DB_POOL_MAX_SIZE         = "10"
    KC_BOOTSTRAP_ADMIN_USERNAME = "tao-admin"
    KC_BOOTSTRAP_ADMIN_PASSWORD = module.key_vault.secret_references["keycloak-admin-password"]
    KC_HOSTNAME                 = "${local.kc_url}/auth"
    KC_HTTP_ENABLED             = "true"
    KC_HTTP_RELATIVE_PATH       = "/auth"
    KC_PROXY_HEADERS            = "xforwarded"
    KC_HEALTH_ENABLED           = "true"
    JAVA_OPTS_KC_HEAP           = "-Xms256m -Xmx768m"
    TAO_PUBLIC_URL              = local.app_url
    TAO_TENANT_ID               = var.environment
  }
}

module "api" {
  source                     = "../../modules/container-web-app"
  name                       = local.api_app_name
  resource_group_name        = module.resource_group.name
  location                   = module.resource_group.location
  service_plan_id            = module.service_plan.id
  identity_id                = module.identity_api.id
  identity_client_id         = module.identity_api.client_id
  acr_login_server           = module.registry.login_server
  image                      = "tao-api:latest"
  health_check_path          = "/health"
  log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
  custom_hostname            = var.api_hostname
  tags                       = local.tags

  app_settings = {
    ASPNETCORE_ENVIRONMENT                = "Production"
    ASPNETCORE_URLS                       = "http://+:8080"
    APPLICATIONINSIGHTS_CONNECTION_STRING = module.monitoring.app_insights_connection_string
    Database__ConnectionString            = module.key_vault.secret_references["db-connection-app"]
    Database__MigrationConnectionString   = module.key_vault.secret_references["db-connection-migration"]
    Database__RetentionConnectionString   = module.key_vault.secret_references["db-connection-retention"]
    Crypto__KeyRingJson                   = module.key_vault.secret_references["key-ring-json"]
    Auth__Issuer                          = local.issuer
    Auth__MetadataAddress                 = "${local.issuer}/.well-known/openid-configuration"
    Auth__Audience                        = "tao-api"
    Auth__SocialProviders                 = join(",", local.social)
    Tao__PublicBaseUrl                    = local.app_url
    Cors__Origins__0                      = var.pages_origin
    Mcp__PublicUrl                        = var.api_hostname == "" ? "" : "https://${var.api_hostname}/api/v1/mcp"
    Connectors__Microsoft__ClientId       = var.microsoft_client_id
    Connectors__Microsoft__ClientSecret   = module.key_vault.secret_references["microsoft-client-secret"]
    Connectors__Microsoft__Tenant         = var.microsoft_tenant
    Connectors__Google__ClientId          = var.google_client_id
    Connectors__Google__ClientSecret      = module.key_vault.secret_references["google-client-secret"]
    Llm__Endpoint                         = var.llm_endpoint
    Llm__Model                            = var.llm_model
    Llm__AuthHeader                       = var.llm_auth_header
    Llm__TokenParameter                   = var.llm_token_parameter
    Llm__OmitTemperature                  = tostring(var.llm_omit_temperature)
    Llm__ReasoningEffort                  = var.llm_reasoning_effort
    Llm__ApiKey                           = module.key_vault.secret_references["llm-api-key"]
    Embeddings__Provider                  = "none"
  }
}

module "alerts" {
  source              = "../../modules/alerts"
  resource_group_name = module.resource_group.name
  location            = module.resource_group.location
  name_prefix         = local.prefix
  alert_email         = var.alert_email
  web_app_ids = {
    api      = module.api.id
    keycloak = module.keycloak.id
  }
  service_plan_id = module.service_plan.id
  postgres_id     = module.postgres.id
  tags            = local.tags
}

module "budget" {
  source            = "../../modules/budget"
  name              = "budget-${local.prefix}"
  resource_group_id = module.resource_group.id
  amount_eur        = var.monthly_budget_eur
  alert_email       = var.alert_email
  start_date        = var.budget_start_date
}

# The apps' addresses are built from their names before the apps exist; this warns if Azure ever
# assigns a different default host name.
check "default_host_names" {
  assert {
    condition     = module.keycloak.default_url == local.kc_default_url
    error_message = "The Keycloak host name differs from the one baked into its settings."
  }
}
