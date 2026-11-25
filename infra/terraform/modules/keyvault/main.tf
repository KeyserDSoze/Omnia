variable "name" { type = string }
variable "resource_group_name" { type = string }
variable "location" { type = string }
variable "tenant_id" { type = string }
variable "deployer_object_id" {
  description = "The identity running Terraform: it writes the secrets."
  type        = string
}
variable "reader_principal_ids" {
  description = "Managed identities that read secrets (Get/List), keyed by a static label."
  type        = map(string)
}
variable "secrets" {
  description = "Secret name => value. Names are static; values may be unknown until apply."
  type        = map(string)
  sensitive   = true
}
variable "tags" {
  type    = map(string)
  default = {}
}

resource "azurerm_key_vault" "this" {
  name                          = var.name
  resource_group_name           = var.resource_group_name
  location                      = var.location
  tenant_id                     = var.tenant_id
  sku_name                      = "standard"
  soft_delete_retention_days    = 7
  purge_protection_enabled      = false
  rbac_authorization_enabled    = false
  public_network_access_enabled = true
  tags                          = var.tags
}

resource "azurerm_key_vault_access_policy" "deployer" {
  key_vault_id       = azurerm_key_vault.this.id
  tenant_id          = var.tenant_id
  object_id          = var.deployer_object_id
  secret_permissions = ["Get", "List", "Set", "Delete", "Purge", "Recover"]
}

resource "azurerm_key_vault_access_policy" "reader" {
  for_each           = var.reader_principal_ids
  key_vault_id       = azurerm_key_vault.this.id
  tenant_id          = var.tenant_id
  object_id          = each.value
  secret_permissions = ["Get", "List"]
}

resource "azurerm_key_vault_secret" "this" {
  for_each     = nonsensitive(toset(keys(var.secrets)))
  name         = each.key
  value        = var.secrets[each.key]
  key_vault_id = azurerm_key_vault.this.id
  depends_on   = [azurerm_key_vault_access_policy.deployer]
}

output "id" { value = azurerm_key_vault.this.id }
output "name" { value = azurerm_key_vault.this.name }
output "secret_references" {
  description = "App Service reference strings, one per secret name."
  value = {
    for name, secret in azurerm_key_vault_secret.this :
    name => "@Microsoft.KeyVault(SecretUri=${secret.versionless_id})"
  }
}
