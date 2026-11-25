#!/usr/bin/env bash
# Creates the database roles on the Azure PostgreSQL server (idempotent: passwords are re-applied).
# Needs `az login` (with access to the Key Vault) and Docker (the psql client runs in a container).
#   infra/scripts/bootstrap-db.sh <resource-group> <postgres-server-name> <key-vault-name>
set -euo pipefail
group=${1:?resource group}
server=${2:?postgres server name}
vault=${3:?key vault name}

secret() { az keyvault secret show --vault-name "$vault" -n "$1" --query value -o tsv; }
admin_password=$(secret db-admin-password)
app_password=$(secret db-app-password)
retention_password=$(secret db-retention-password)
keycloak_password=$(secret db-keycloak-password)
fqdn=$(az postgres flexible-server show -g "$group" -n "$server" --query fullyQualifiedDomainName -o tsv)
admin=$(az postgres flexible-server show -g "$group" -n "$server" --query administratorLogin -o tsv)

# The machine running this script needs the firewall open for the duration of the call only.
my_ip=$(curl -fsS https://api.ipify.org)
rule=tao-bootstrap
az postgres flexible-server firewall-rule create -g "$group" -n "$server" --rule-name "$rule" \
  --start-ip-address "$my_ip" --end-ip-address "$my_ip" -o none
cleanup() { az postgres flexible-server firewall-rule delete -g "$group" -n "$server" --rule-name "$rule" --yes -o none || true; }
trap cleanup EXIT

run_sql() { # <database> reads SQL from stdin
  docker run --rm -i -e PGPASSWORD="$admin_password" -e PGSSLMODE=require postgres:17 \
    psql -v ON_ERROR_STOP=1 -h "$fqdn" -U "$admin" -d "$1" \
      -v app_password="$app_password" -v retention_password="$retention_password" \
      -v keycloak_password="$keycloak_password" -v admin="$admin"
}

# Runtime roles never own tables, bypass RLS or create roles; the migration identity is the admin.
run_sql postgres <<'SQL'
SELECT format('CREATE ROLE tao_app LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tao_app') \gexec
SELECT format('ALTER ROLE tao_app PASSWORD %L', :'app_password') \gexec
SELECT format('CREATE ROLE tao_retention LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT', :'retention_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tao_retention') \gexec
SELECT format('ALTER ROLE tao_retention PASSWORD %L', :'retention_password') \gexec
SELECT format('CREATE ROLE keycloak LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE', :'keycloak_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'keycloak') \gexec
SELECT format('ALTER ROLE keycloak PASSWORD %L', :'keycloak_password') \gexec
-- Keycloak owns its database; the admin must be a member of the role to hand the database over.
SELECT format('GRANT keycloak TO %I', :'admin') \gexec
ALTER DATABASE keycloak OWNER TO keycloak;
SQL
# PostgreSQL 15+ no longer lets ordinary roles create objects in the public schema.
run_sql keycloak <<'SQL'
ALTER SCHEMA public OWNER TO keycloak;
SQL
echo "Database roles are ready on $fqdn."
