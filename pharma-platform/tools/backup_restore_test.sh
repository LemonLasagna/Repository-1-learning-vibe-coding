#!/usr/bin/env bash
# Backup-restore test: dumps a PharmaOS database, restores the dump into a new database and proves the copy matches.
#
#   PGPASSWORD=... tools/backup_restore_test.sh <source-db> [pg-host] [pg-port] [pg-superuser]
#
# Proves: the custom-format dump restores without error; the schema is identical (pg_dump --schema-only of both, compared);
# every table has the same row count; and the audit trail, the record of every GxP change, is intact row for row (a digest over
# its rows, ordered by primary key, compared). The restore target is dropped at the end, whatever the result.
set -euo pipefail

SRC="${1:?source database name}"
HOST="${2:-localhost}"
PORT="${3:-5432}"
SU="${4:-postgres}"
DST="${SRC}_restore_test"
WORK="$(mktemp -d)"
trap 'dropdb --if-exists -h "$HOST" -p "$PORT" -U "$SU" "$DST" >/dev/null 2>&1 || true; rm -rf "$WORK"' EXIT

psql_() { psql -X -q -v ON_ERROR_STOP=1 -h "$HOST" -p "$PORT" -U "$SU" "$@"; }

# Tables whose names come from the catalog, never from input.
TABLES="$(psql_ -d "$SRC" -At -c "SELECT quote_ident(table_schema)||'.'||quote_ident(table_name) FROM information_schema.tables WHERE table_type='BASE TABLE' AND table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1")"
SOURCE_ROWS=0
while IFS= read -r t; do
  [[ -z "$t" ]] && continue
  SOURCE_ROWS=$((SOURCE_ROWS + $(psql_ -d "$SRC" -At -c "SELECT count(*) FROM $t")))
done <<< "$TABLES"
if [[ "$SOURCE_ROWS" -lt 1 ]]; then
  echo "FAIL: $SRC has no rows, so a restore cannot be shown to match. Load demo data first (install.sh --demo-data)." >&2
  exit 1
fi

echo "1/4 dump $SRC"
pg_dump -h "$HOST" -p "$PORT" -U "$SU" -Fc -f "$WORK/backup.dump" "$SRC"
echo "    $(stat -c%s "$WORK/backup.dump") bytes"

echo "2/4 restore into $DST"
dropdb --if-exists -h "$HOST" -p "$PORT" -U "$SU" "$DST"
createdb -h "$HOST" -p "$PORT" -U "$SU" "$DST"
pg_restore -h "$HOST" -p "$PORT" -U "$SU" -d "$DST" --no-owner --exit-on-error "$WORK/backup.dump"

echo "3/4 compare schema"
pg_dump -s -h "$HOST" -p "$PORT" -U "$SU" "$SRC" | grep -v -e '^-- Dumped' -e '^\\restrict' -e '^\\unrestrict' > "$WORK/src.sql"
pg_dump -s -h "$HOST" -p "$PORT" -U "$SU" "$DST" | grep -v -e '^-- Dumped' -e '^\\restrict' -e '^\\unrestrict' > "$WORK/dst.sql"
if ! diff -q "$WORK/src.sql" "$WORK/dst.sql" >/dev/null; then
  diff "$WORK/src.sql" "$WORK/dst.sql" | head -20 >&2
  echo "FAIL: schema differs after restore" >&2
  exit 1
fi
echo "    schema identical"

echo "4/4 compare rows"
FAILS=0
ROWS=0
while IFS= read -r t; do
  [[ -z "$t" ]] && continue
  a="$(psql_ -d "$SRC" -At -c "SELECT count(*) FROM $t")"
  b="$(psql_ -d "$DST" -At -c "SELECT count(*) FROM $t")"
  ROWS=$((ROWS + a))
  if [[ "$a" != "$b" ]]; then
    echo "    MISMATCH $t: source $a rows, restored $b" >&2
    FAILS=$((FAILS + 1))
  fi
done <<< "$TABLES"

# The audit trail is the evidence that GxP changes happened: compare every row, in sequence order.
a="$(psql_ -d "$SRC" -At -c "SELECT md5(coalesce(string_agg(t::text, E'\n' ORDER BY t.seq), '')) FROM audit.audit_trail t")"
b="$(psql_ -d "$DST" -At -c "SELECT md5(coalesce(string_agg(t::text, E'\n' ORDER BY t.seq), '')) FROM audit.audit_trail t")"
if [[ "$a" != "$b" ]]; then
  echo "    MISMATCH audit.audit_trail: audit trail digest differs" >&2
  FAILS=$((FAILS + 1))
else
  echo "    audit trail digest identical ($a)"
fi

if [[ "$FAILS" -gt 0 ]]; then
  echo "FAIL: $FAILS table(s) differ after restore" >&2
  exit 1
fi
echo "PASS: $(echo "$TABLES" | grep -c .) tables, $ROWS rows restored identically"
