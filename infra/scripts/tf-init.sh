#!/usr/bin/env bash
# Creates (idempotently) the storage account that holds the Terraform state and initializes Terraform
# against it. Needs `az login` and ARM_CLIENT_ID / ARM_CLIENT_SECRET / ARM_TENANT_ID / ARM_SUBSCRIPTION_ID.
#   infra/scripts/tf-init.sh
set -euo pipefail
: "${ARM_SUBSCRIPTION_ID:?ARM_SUBSCRIPTION_ID is required}"
cd "$(dirname "$0")/.."

location=${TAO_LOCATION:-westeurope}
suffix=$(printf '%s' "$ARM_SUBSCRIPTION_ID" | sha1sum | cut -c1-5)
group=rg-tao-tfstate
account=sttaotf$suffix

az group create --subscription "$ARM_SUBSCRIPTION_ID" -n "$group" -l "$location" -o none
if ! az storage account show --subscription "$ARM_SUBSCRIPTION_ID" -g "$group" -n "$account" -o none 2>/dev/null; then
  az storage account create --subscription "$ARM_SUBSCRIPTION_ID" -g "$group" -n "$account" -l "$location" \
    --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 --allow-blob-public-access false -o none
fi
key=$(az storage account keys list --subscription "$ARM_SUBSCRIPTION_ID" -g "$group" -n "$account" --query '[0].value' -o tsv)
az storage container create --account-name "$account" --account-key "$key" -n tfstate -o none

terraform -chdir=terraform/environments/demo init -input=false -reconfigure \
  -backend-config="resource_group_name=$group" \
  -backend-config="storage_account_name=$account" \
  -backend-config="container_name=tfstate" \
  -backend-config="key=tao-demo.tfstate"
