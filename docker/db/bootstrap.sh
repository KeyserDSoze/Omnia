#!/usr/bin/env bash
set -euo pipefail
: "${APP_DB_PASSWORD:?APP_DB_PASSWORD is required}"
: "${RETENTION_DB_PASSWORD:?RETENTION_DB_PASSWORD is required}"
: "${KEYCLOAK_DB_PASSWORD:?KEYCLOAK_DB_PASSWORD is required}"
: "${PGPASSWORD:?PGPASSWORD is required}"

# This command runs as the schema/migration administrator, never as the API role.
# SQL quote_literal via psql :'app_password' prevents interpolation of secrets.
psql -v ON_ERROR_STOP=1 -v app_password="$APP_DB_PASSWORD" -v retention_password="$RETENTION_DB_PASSWORD" \
  -v keycloak_password="$KEYCLOAK_DB_PASSWORD" <<'SQL'
SELECT format(
  'CREATE ROLE tao_app LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE',
  :'app_password'
)
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tao_app')
\gexec
-- Also rotate credentials on existing volumes to match the current .env.
SELECT format('ALTER ROLE tao_app PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE',
  :'app_password')
\gexec
-- A dedicated least-privileged worker login: NO direct table DML,
-- no schema ownership, and only EXECUTE on a single bounded purge function.
SELECT format(
  'CREATE ROLE tao_retention LOGIN PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT',
  :'retention_password'
)
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tao_retention')
\gexec
SELECT format(
  'ALTER ROLE tao_retention PASSWORD %L NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT',
  :'retention_password'
)
\gexec
-- Keycloak keeps its own database and owner role on the same server.
SELECT format('CREATE ROLE keycloak LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE',
  :'keycloak_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'keycloak')
\gexec
SELECT format('ALTER ROLE keycloak PASSWORD %L', :'keycloak_password')
\gexec
SELECT 'CREATE DATABASE keycloak OWNER keycloak'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'keycloak')
\gexec
SQL
