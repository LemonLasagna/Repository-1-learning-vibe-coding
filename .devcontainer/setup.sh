#!/usr/bin/env bash
# One-time setup of the development container (runs when the codespace is created):
# PostgreSQL 16, Java 21, Keycloak, then the PharmaOS installer with the demo organisation.
# DEVELOPMENT ONLY: the database superuser password below is known and the container is throwaway.
set -Eeuo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP="$(cd "$HERE/../pharma-platform" && pwd)"
KC_VERSION=26.8.1
KC_HOME="${KC_HOME:-$HOME/keycloak-$KC_VERSION}"
PG_DEV_PASSWORD=postgres

echo "== PostgreSQL 16 and Java 21 =="
sudo apt-get update -qq
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq postgresql-16 postgresql-client-16 openjdk-21-jre-headless python3 curl
sudo service postgresql start
sudo -u postgres psql -qc "ALTER USER postgres PASSWORD '$PG_DEV_PASSWORD'"

echo "== Keycloak $KC_VERSION =="
if [[ ! -x "$KC_HOME/bin/kc.sh" ]]; then
  tmp="$(mktemp -d)"
  base="https://github.com/keycloak/keycloak/releases/download/$KC_VERSION"
  curl -fsSL -o "$tmp/keycloak.tar.gz" "$base/keycloak-$KC_VERSION.tar.gz"
  expected="$(curl -fsSL "$base/keycloak-$KC_VERSION.tar.gz.sha1")"
  echo "${expected%% *}  $tmp/keycloak.tar.gz" | sha1sum -c --quiet - || { echo "Keycloak download is corrupt" >&2; exit 1; }
  mkdir -p "$(dirname "$KC_HOME")"
  tar -xzf "$tmp/keycloak.tar.gz" -C "$(dirname "$KC_HOME")"
  rm -rf "$tmp"
fi
echo "  Keycloak in $KC_HOME"

echo "== PharmaOS installer (builds, runs all tests, loads the demo organisation) =="
cd "$APP"
if PGPASSWORD=$PG_DEV_PASSWORD psql -h localhost -U postgres -qAtc "SELECT 1 FROM pg_database WHERE datname = 'pharmaos_dev'" | grep -q 1; then
  echo "  Database pharmaos_dev already exists; installer skipped. To start over: PGPASSWORD=$PG_DEV_PASSWORD ./install.sh --recreate --demo-data"
else
  PGPASSWORD=$PG_DEV_PASSWORD ./install.sh --non-interactive --demo-data --pg-host localhost
fi
