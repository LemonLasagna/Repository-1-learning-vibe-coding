#!/bin/sh
# Runs once, on first start of an empty data directory (official postgres image behaviour).
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres -c "CREATE DATABASE keycloak;" -c "CREATE DATABASE superset;"
for f in /schema/0*.sql; do
  echo "loading $f"
  psql -q -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -f "$f" > /dev/null
done
# Application login: member of the NOLOGIN pharmaos_app role, so RLS and grants apply (never the owner).
# (variables are only interpolated from script input, not from -c, hence the heredoc)
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v pw="${PHARMAOS_APP_PASSWORD:-$POSTGRES_PASSWORD}" <<'SQL'
CREATE ROLE pharmaos_app_login LOGIN IN ROLE pharmaos_app PASSWORD :'pw';
SQL
if [ "${PHARMAOS_RUN_DI_TESTS:-true}" = "true" ]; then
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -f /tests/integrity_tests.sql
fi
