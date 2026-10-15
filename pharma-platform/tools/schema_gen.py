#!/usr/bin/env python3
"""
PharmaOS schema generator.

Reads database/model/catalog.dsl and emits:
  database/schema/000_foundation.sql   extensions, schemas, roles, audit infrastructure
  database/schema/010_tables.sql       business tables (standard columns + domain columns)
  database/schema/020_versions.sql     <table>_version snapshot tables for V-flagged entities
  database/schema/030_foreign_keys.sql FKs (added after all tables -> no ordering issues)
  database/schema/040_indexes.sql      FK, tenant, status, uniqueness, JSONB indexes
  database/schema/050_triggers_rls.sql audit / touch / no-delete / append-only triggers, RLS
  docs/04-erd.md                       Mermaid ERD per bounded context
  database/data-dictionary.md          column-level dictionary

Usage: python3 tools/schema_gen.py [--check]
"""
from __future__ import annotations

import hashlib
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CATALOG = ROOT / "database" / "model" / "catalog.dsl"
OUT_SQL = ROOT / "database" / "schema"
OUT_ERD = ROOT / "docs" / "04-erd.md"
OUT_DICT = ROOT / "database" / "data-dictionary.md"

TYPES = {
    "c": "varchar(50)", "s": "varchar(255)", "l": "varchar(1000)", "t": "text",
    "i": "integer", "b": "bigint", "n": "numeric(18,6)", "m": "numeric(19,4)",
    "bool": "boolean", "d": "date", "ts": "timestamptz", "tm": "time",
    "j": "jsonb", "u": "uuid", "st": "varchar(30)", "dur": "interval", "h": "char(64)",
}
MERMAID_TYPE = {
    "varchar(50)": "varchar", "varchar(255)": "varchar", "varchar(1000)": "varchar",
    "varchar(30)": "varchar", "text": "text", "integer": "int", "bigint": "bigint",
    "numeric(18,6)": "numeric", "numeric(19,4)": "money", "boolean": "bool",
    "date": "date", "timestamptz": "timestamptz", "time": "time", "jsonb": "jsonb",
    "uuid": "uuid", "interval": "interval", "char(64)": "hash",
}

# Tables whose rows must never be updated once written (correction = new row).
APPEND_ONLY = {
    "esig.signature", "esig.signature_failure", "esig.signature_manifest",
    "wf.process_history", "wms.inventory_transaction", "wms.lot_status_history",
    "eqp.equipment_status_history", "lims.sample_custody_event",
    "mes.batch_parameter_value", "iam.login_attempt", "rpt.report_execution",
    "val.automated_test_evidence", "dms.controlled_print",
}
# Ubiquitous targets omitted from ERD edges to keep diagrams legible.
ERD_SKIP_TARGETS = {"iam.app_user", "org.site"}


@dataclass
class Column:
    name: str
    sqltype: str
    not_null: bool = False
    unique: bool = False
    fk: str | None = None  # "schema.table"


@dataclass
class Table:
    schema: str
    name: str
    flags: set[str]
    columns: list[Column]
    uniques: list[list[str]] = field(default_factory=list)

    @property
    def fq(self) -> str:
        return f"{self.schema}.{self.name}"

    @property
    def gxp(self) -> bool:
        return "N" not in self.flags


@dataclass
class Schema:
    name: str
    description: str
    tables: list[Table] = field(default_factory=list)


def parse(path: Path) -> list[Schema]:
    schemas: list[Schema] = []
    cur: Schema | None = None
    for lineno, raw in enumerate(path.read_text().splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("@schema"):
            name, desc = line[len("@schema"):].split("|", 1)
            cur = Schema(name.strip(), desc.strip())
            schemas.append(cur)
            continue
        if cur is None:
            raise SystemExit(f"{lineno}: table outside @schema")
        parts = [p.strip() for p in line.split("|")]
        head = parts[0].split()
        tname, flags = head[0], set("".join(head[1:]))
        cols: list[Column] = []
        for spec in [c.strip() for c in parts[1].split(",") if c.strip()]:
            cname, ctype = spec.split(":", 1)
            not_null = "!" in ctype
            unique = "*" in ctype
            ctype = ctype.replace("!", "").replace("*", "")
            if ctype.startswith(">"):
                cols.append(Column(cname, "uuid", not_null, unique, ctype[1:]))
            else:
                if ctype not in TYPES:
                    raise SystemExit(f"{lineno}: unknown type {ctype!r} in {tname}.{cname}")
                cols.append(Column(cname, TYPES[ctype], not_null, unique))
        uniques = []
        for extra in parts[2:]:
            m = re.fullmatch(r"U\(([^)]*)\)", extra)
            if not m:
                raise SystemExit(f"{lineno}: bad table option {extra!r}")
            uniques.append([c.strip() for c in m.group(1).split(",")])
        cur.tables.append(Table(cur.name, tname, flags, cols, uniques))
    return schemas


def ident(*parts: str) -> str:
    """Build a <=63 char identifier, hashing the tail if needed."""
    name = "_".join(parts)
    if len(name) <= 63:
        return name
    h = hashlib.sha1(name.encode()).hexdigest()[:8]
    return name[:54] + "_" + h


def standard_columns(t: Table) -> list[str]:
    cols = [
        "id uuid PRIMARY KEY DEFAULT gen_random_uuid()",
        "tenant_id uuid NOT NULL",
    ]
    if "S" in t.flags:
        cols.append("site_id uuid NOT NULL")
    return cols


def trailing_columns(t: Table) -> list[str]:
    cols = []
    if "W" in t.flags:
        cols += ["workflow_instance_id uuid", "workflow_state varchar(50)"]
    if "V" in t.flags:
        cols.append("current_version_no integer NOT NULL DEFAULT 1")
    cols += [
        "ext jsonb NOT NULL DEFAULT '{}'::jsonb",
        "row_version integer NOT NULL DEFAULT 1",
        "created_at timestamptz NOT NULL DEFAULT now()",
        "created_by uuid NOT NULL",
        "updated_at timestamptz NOT NULL DEFAULT now()",
        "updated_by uuid NOT NULL",
    ]
    return cols


def all_fks(t: Table) -> list[tuple[str, str]]:
    fks = [(c.name, c.fk) for c in t.columns if c.fk]
    if "S" in t.flags:
        fks.append(("site_id", "org.site"))
    if "W" in t.flags:
        fks.append(("workflow_instance_id", "wf.process_instance"))
    return fks


FOUNDATION = r"""-- =====================================================================
-- 000_foundation.sql  —  GENERATED by tools/schema_gen.py — do not edit
-- PostgreSQL 16+. Extensions, schemas, roles, audit-trail infrastructure.
-- =====================================================================
CREATE EXTENSION IF NOT EXISTS pgcrypto;   -- gen_random_uuid(), digest()
CREATE EXTENSION IF NOT EXISTS pg_trgm;    -- fuzzy search on numbers/titles
CREATE EXTENSION IF NOT EXISTS btree_gist; -- exclusion constraints (scheduling)

-- Roles: the application never connects as owner, so RLS is always enforced.
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='pharmaos_owner')    THEN CREATE ROLE pharmaos_owner NOLOGIN; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='pharmaos_app')      THEN CREATE ROLE pharmaos_app NOLOGIN; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='pharmaos_readonly') THEN CREATE ROLE pharmaos_readonly NOLOGIN; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='pharmaos_auditor')  THEN CREATE ROLE pharmaos_auditor NOLOGIN; END IF;
END $$;

{schemas}
CREATE SCHEMA IF NOT EXISTS audit;

-- ---------------------------------------------------------------------
-- Session context contract (set by the .NET DbConnectionInterceptor per tx):
--   SET LOCAL app.tenant_id      = '<uuid>';
--   SET LOCAL app.user_id        = '<uuid>';
--   SET LOCAL app.user_display   = 'Jane Doe (jdoe)';
--   SET LOCAL app.on_behalf_of   = '<uuid>'   -- delegation
--   SET LOCAL app.change_reason  = '<text>';
--   SET LOCAL app.correlation_id = '<uuid>';
--   SET LOCAL app.client         = 'qms-service/1.4.2';
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION audit.ctx(p_key text) RETURNS text
LANGUAGE sql STABLE AS $$ SELECT nullif(current_setting('app.' || p_key, true), '') $$;

-- Immutable, partitioned audit trail (ALCOA+: attributable, contemporaneous, original).
CREATE TABLE IF NOT EXISTS audit.audit_trail (
  seq            bigint GENERATED ALWAYS AS IDENTITY,
  occurred_at    timestamptz NOT NULL DEFAULT clock_timestamp(),
  tenant_id      uuid,
  schema_name    name NOT NULL,
  table_name     name NOT NULL,
  record_id      uuid,
  operation      varchar(10) NOT NULL,
  changed_fields text[],
  old_values     jsonb,
  new_values     jsonb,
  actor_user_id  uuid NOT NULL,
  actor_display  varchar(255),
  on_behalf_of   uuid,
  reason         varchar(2000),
  correlation_id uuid,
  client_app     varchar(100),
  tx_id          bigint NOT NULL,
  row_hash       char(64) NOT NULL,
  PRIMARY KEY (occurred_at, seq)
) PARTITION BY RANGE (occurred_at);

CREATE TABLE IF NOT EXISTS audit.audit_trail_default PARTITION OF audit.audit_trail DEFAULT;

CREATE INDEX IF NOT EXISTS ix_audit_trail_record ON audit.audit_trail (tenant_id, schema_name, table_name, record_id, occurred_at);
CREATE INDEX IF NOT EXISTS ix_audit_trail_actor  ON audit.audit_trail (tenant_id, actor_user_id, occurred_at);
CREATE INDEX IF NOT EXISTS ix_audit_trail_brin   ON audit.audit_trail USING brin (occurred_at);

-- Monthly partition provisioning (called by the platform scheduler; pg_partman is an alternative).
CREATE OR REPLACE FUNCTION audit.ensure_partition(p_month date) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
  v_from date := date_trunc('month', p_month)::date;
  v_to   date := (date_trunc('month', p_month) + interval '1 month')::date;
  v_name text := format('audit_trail_%s', to_char(v_from, 'YYYYMM'));
BEGIN
  EXECUTE format('CREATE TABLE IF NOT EXISTS audit.%I PARTITION OF audit.audit_trail FOR VALUES FROM (%L) TO (%L)', v_name, v_from, v_to);
END $$;

-- Tamper evidence: rows are hashed, ranges are chained into seals by an async sealer
-- (hash chaining inside the write trigger would serialise every GxP write).
CREATE TABLE IF NOT EXISTS audit.audit_seal (
  id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  from_seq       bigint NOT NULL,
  to_seq         bigint NOT NULL,
  record_count   integer NOT NULL,
  range_hash     char(64) NOT NULL,
  prev_seal_hash char(64),
  seal_hash      char(64) NOT NULL,
  kms_key_id     varchar(200),
  kms_signature  text,
  sealed_at      timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE OR REPLACE FUNCTION audit.seal_range(p_from bigint, p_to bigint) RETURNS audit.audit_seal
LANGUAGE plpgsql AS $$
DECLARE
  v_prev  char(64);
  v_range char(64);
  v_count integer;
  v_seal  audit.audit_seal;
BEGIN
  SELECT seal_hash INTO v_prev FROM audit.audit_seal ORDER BY id DESC LIMIT 1;
  SELECT count(*), encode(digest(coalesce(string_agg(row_hash, '' ORDER BY seq), ''), 'sha256'), 'hex')
    INTO v_count, v_range
    FROM audit.audit_trail WHERE seq BETWEEN p_from AND p_to;
  INSERT INTO audit.audit_seal (from_seq, to_seq, record_count, range_hash, prev_seal_hash, seal_hash)
  VALUES (p_from, p_to, v_count, v_range, v_prev,
          encode(digest(coalesce(v_prev, '') || v_range || p_from::text || p_to::text, 'sha256'), 'hex'))
  RETURNING * INTO v_seal;
  RETURN v_seal;
END $$;

-- Verification used by the "Audit Trail Integrity" OQ test and the periodic DI check.
CREATE OR REPLACE FUNCTION audit.verify_seals() RETURNS TABLE(seal_id bigint, ok boolean)
LANGUAGE sql STABLE AS $$
  SELECT s.id,
         s.seal_hash = encode(digest(coalesce(s.prev_seal_hash, '') ||
             encode(digest(coalesce((SELECT string_agg(a.row_hash, '' ORDER BY a.seq)
                                       FROM audit.audit_trail a
                                      WHERE a.seq BETWEEN s.from_seq AND s.to_seq), ''), 'sha256'), 'hex')
             || s.from_seq::text || s.to_seq::text, 'sha256'), 'hex')
         AND s.prev_seal_hash IS NOT DISTINCT FROM lag(s.seal_hash) OVER (ORDER BY s.id)
  FROM audit.audit_seal s ORDER BY s.id
$$;

-- Periodic audit-trail review evidence (data-integrity expectation for GxP systems).
CREATE TABLE IF NOT EXISTS audit.audit_trail_review (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     uuid NOT NULL,
  scope         jsonb NOT NULL,
  period_start  timestamptz NOT NULL,
  period_end    timestamptz NOT NULL,
  reviewer_id   uuid NOT NULL,
  findings      text,
  outcome       varchar(30) NOT NULL,
  signature_id  uuid,
  reviewed_at   timestamptz NOT NULL DEFAULT now()
);

-- Generic row-audit trigger. Row hash is computed at insert; seals chain ranges later.
CREATE OR REPLACE FUNCTION audit.fn_row_audit() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, audit, public AS $$
DECLARE
  v_old jsonb; v_new jsonb; v_changed text[]; v_actor uuid;
  v_at timestamptz := clock_timestamp();
  v_tenant uuid; v_id uuid; v_reason text;
BEGIN
  v_actor := audit.ctx('user_id')::uuid;
  IF v_actor IS NULL THEN
    RAISE EXCEPTION 'GxP write to %.% without authenticated actor (app.user_id not set)', TG_TABLE_SCHEMA, TG_TABLE_NAME
      USING ERRCODE = 'insufficient_privilege';
  END IF;
  IF TG_OP <> 'INSERT' THEN v_old := to_jsonb(OLD); END IF;
  IF TG_OP <> 'DELETE' THEN v_new := to_jsonb(NEW); END IF;
  v_tenant := coalesce(v_new ->> 'tenant_id', v_old ->> 'tenant_id')::uuid;
  v_id     := coalesce(v_new ->> 'id', v_old ->> 'id')::uuid;
  IF TG_OP = 'UPDATE' THEN
    SELECT array_agg(k ORDER BY k) INTO v_changed
      FROM jsonb_object_keys(v_new) k
     WHERE v_new -> k IS DISTINCT FROM v_old -> k
       AND k NOT IN ('updated_at', 'updated_by', 'row_version');
    IF v_changed IS NULL THEN RETURN NULL; END IF;
    SELECT jsonb_object_agg(k, v_old -> k), jsonb_object_agg(k, v_new -> k)
      INTO v_old, v_new FROM unnest(v_changed) k;
  END IF;
  v_reason := audit.ctx('change_reason');
  INSERT INTO audit.audit_trail (occurred_at, tenant_id, schema_name, table_name, record_id, operation,
         changed_fields, old_values, new_values, actor_user_id, actor_display, on_behalf_of, reason,
         correlation_id, client_app, tx_id, row_hash)
  VALUES (v_at, v_tenant, TG_TABLE_SCHEMA, TG_TABLE_NAME, v_id, TG_OP, v_changed, v_old, v_new, v_actor,
          audit.ctx('user_display'), audit.ctx('on_behalf_of')::uuid, v_reason,
          audit.ctx('correlation_id')::uuid, audit.ctx('client'), txid_current(),
          encode(digest(concat_ws('|', v_at::text, v_tenant::text, TG_TABLE_SCHEMA, TG_TABLE_NAME,
                 v_id::text, TG_OP, v_old::text, v_new::text, v_actor::text, v_reason), 'sha256'), 'hex'));
  RETURN NULL;
END $$;

-- Audit trail is strictly append-only (also enforced by REVOKE; trigger guards superuser mistakes).
CREATE OR REPLACE FUNCTION audit.fn_protect_audit() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'audit.audit_trail is append-only (% rejected)', TG_OP USING ERRCODE = 'insufficient_privilege';
END $$;

DROP TRIGGER IF EXISTS trg_protect_audit ON audit.audit_trail;
CREATE TRIGGER trg_protect_audit BEFORE UPDATE OR DELETE ON audit.audit_trail
  FOR EACH ROW EXECUTE FUNCTION audit.fn_protect_audit();

-- Optimistic concurrency + immutable creation stamps.
CREATE OR REPLACE FUNCTION audit.fn_touch() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.tenant_id <> OLD.tenant_id OR NEW.created_at <> OLD.created_at OR NEW.created_by <> OLD.created_by THEN
    RAISE EXCEPTION 'tenant_id / created_* are immutable on %.%', TG_TABLE_SCHEMA, TG_TABLE_NAME;
  END IF;
  IF NEW.row_version <> OLD.row_version THEN
    RAISE EXCEPTION 'concurrency conflict on %.% id=%', TG_TABLE_SCHEMA, TG_TABLE_NAME, OLD.id
      USING ERRCODE = 'serialization_failure';
  END IF;
  NEW.row_version := OLD.row_version + 1;
  NEW.updated_at  := now();
  NEW.updated_by  := coalesce(audit.ctx('user_id')::uuid, NEW.updated_by);
  RETURN NEW;
END $$;

-- GxP records are never physically deleted (retire/void via status + workflow).
CREATE OR REPLACE FUNCTION audit.fn_block_delete() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'Physical delete of GxP record %.% is not permitted; use status transition', TG_TABLE_SCHEMA, TG_TABLE_NAME
    USING ERRCODE = 'insufficient_privilege';
END $$;

CREATE OR REPLACE FUNCTION audit.fn_block_update() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION '%.% is append-only; record a correcting entry instead', TG_TABLE_SCHEMA, TG_TABLE_NAME
    USING ERRCODE = 'insufficient_privilege';
END $$;

REVOKE ALL ON SCHEMA audit FROM PUBLIC;
GRANT USAGE ON SCHEMA audit TO pharmaos_app, pharmaos_auditor;
GRANT SELECT ON ALL TABLES IN SCHEMA audit TO pharmaos_auditor;
GRANT INSERT ON audit.audit_trail_review TO pharmaos_app;
"""


def gen_foundation(schemas: list[Schema]) -> str:
    lines = "\n".join(
        f"CREATE SCHEMA IF NOT EXISTS {s.name};\nCOMMENT ON SCHEMA {s.name} IS '{s.description}';"
        for s in schemas
    )
    return FOUNDATION.replace("{schemas}", lines)


def gen_tables(schemas: list[Schema]) -> str:
    out = ["-- 010_tables.sql — GENERATED by tools/schema_gen.py — do not edit\n"]
    for s in schemas:
        out.append(f"\n-- {'=' * 69}\n-- {s.name}: {s.description}\n-- {'=' * 69}")
        for t in s.tables:
            cols = standard_columns(t)
            for c in t.columns:
                cols.append(f"{c.name} {c.sqltype}{' NOT NULL' if c.not_null else ''}")
            cols += trailing_columns(t)
            body = ",\n  ".join(cols)
            out.append(f"CREATE TABLE {t.fq} (\n  {body}\n);")
            flags = "".join(sorted(t.flags)) or "-"
            out.append(f"COMMENT ON TABLE {t.fq} IS 'flags={flags}; gxp={str(t.gxp).lower()}';")
    return "\n".join(out) + "\n"


def gen_versions(schemas: list[Schema]) -> tuple[str, list[str]]:
    out = ["-- 020_versions.sql — GENERATED — immutable version snapshots for V-flagged entities\n"]
    names = []
    existing = {t.fq for s in schemas for t in s.tables}
    for s in schemas:
        for t in s.tables:
            if "V" not in t.flags:
                continue
            vname = f"{t.fq}_version"
            if vname in existing:
                raise SystemExit(f"version table collides with existing table {vname}")
            names.append(vname)
            out.append(f"""CREATE TABLE {vname} (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES {t.fq}(id),
  version_no integer NOT NULL,
  version_label varchar(20) NOT NULL,
  lifecycle_status varchar(30) NOT NULL,
  snapshot jsonb NOT NULL,
  snapshot_hash char(64) NOT NULL,
  effective_from timestamptz,
  effective_to timestamptz,
  change_control_id uuid REFERENCES qms.change_control(id),
  approval_signature_id uuid REFERENCES esig.signature(id),
  created_at timestamptz NOT NULL DEFAULT now(),
  created_by uuid NOT NULL,
  UNIQUE (entity_id, version_no)
);
CREATE INDEX {ident('ix', t.schema, t.name, 'ver_eff')} ON {vname} (tenant_id, entity_id, effective_from);""")
    return "\n".join(out) + "\n", names


def gen_fks(schemas: list[Schema], known: set[str]) -> str:
    out = ["-- 030_foreign_keys.sql — GENERATED\n"]
    for s in schemas:
        for t in s.tables:
            for col, target in all_fks(t):
                if target not in known:
                    raise SystemExit(f"{t.fq}.{col} references unknown table {target}")
                out.append(
                    f"ALTER TABLE {t.fq} ADD CONSTRAINT {ident('fk', t.name, col)} "
                    f"FOREIGN KEY ({col}) REFERENCES {target}(id);"
                )
    return "\n".join(out) + "\n"


def gen_indexes(schemas: list[Schema]) -> str:
    out = ["-- 040_indexes.sql — GENERATED\n"]
    for s in schemas:
        for t in s.tables:
            colnames = {c.name for c in t.columns}
            out.append(f"CREATE INDEX {ident('ix', t.schema, t.name, 'tenant')} ON {t.fq} (tenant_id);")
            if "S" in t.flags:
                out.append(f"CREATE INDEX {ident('ix', t.schema, t.name, 'site')} ON {t.fq} (tenant_id, site_id);")
            for col, _ in all_fks(t):
                if col == "site_id" and "S" in t.flags:
                    continue
                out.append(f"CREATE INDEX {ident('ix', t.schema, t.name, col)} ON {t.fq} ({col});")
            for c in t.columns:
                if c.unique:
                    out.append(f"CREATE UNIQUE INDEX {ident('ux', t.schema, t.name, c.name)} ON {t.fq} (tenant_id, {c.name});")
                    if c.sqltype.startswith("varchar"):
                        out.append(f"CREATE INDEX {ident('tg', t.schema, t.name, c.name)} ON {t.fq} USING gin ({c.name} gin_trgm_ops);")
            for u in t.uniques:
                out.append(f"CREATE UNIQUE INDEX {ident('ux', t.schema, t.name, *u)} ON {t.fq} (tenant_id, {', '.join(u)});")
            if "status" in colnames:
                out.append(f"CREATE INDEX {ident('ix', t.schema, t.name, 'status')} ON {t.fq} (tenant_id, status);")
            for dc in ("due_date", "next_review_date", "expiry_date"):
                if dc in colnames:
                    out.append(f"CREATE INDEX {ident('ix', t.schema, t.name, dc)} ON {t.fq} (tenant_id, {dc});")
            if "W" in t.flags:
                out.append(f"CREATE INDEX {ident('ix', t.schema, t.name, 'wfstate')} ON {t.fq} (tenant_id, workflow_state);")
                out.append(f"CREATE INDEX {ident('gx', t.schema, t.name, 'ext')} ON {t.fq} USING gin (ext jsonb_path_ops);")
    return "\n".join(out) + "\n"


def gen_triggers(schemas: list[Schema], version_tables: list[str]) -> str:
    out = ["-- 050_triggers_rls.sql — GENERATED\n"]
    for s in schemas:
        for t in s.tables:
            out.append(f"CREATE TRIGGER trg_touch BEFORE UPDATE ON {t.fq} FOR EACH ROW EXECUTE FUNCTION audit.fn_touch();")
            if t.gxp:
                out.append(f"CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON {t.fq} FOR EACH ROW EXECUTE FUNCTION audit.fn_row_audit();")
                out.append(f"CREATE TRIGGER trg_no_delete BEFORE DELETE ON {t.fq} FOR EACH ROW EXECUTE FUNCTION audit.fn_block_delete();")
            if t.fq in APPEND_ONLY:
                out.append(f"CREATE TRIGGER trg_append_only BEFORE UPDATE ON {t.fq} FOR EACH ROW EXECUTE FUNCTION audit.fn_block_update();")
            out.append(f"ALTER TABLE {t.fq} ENABLE ROW LEVEL SECURITY;")
            out.append(
                f"CREATE POLICY tenant_isolation ON {t.fq} USING (tenant_id = audit.ctx('tenant_id')::uuid) "
                f"WITH CHECK (tenant_id = audit.ctx('tenant_id')::uuid);"
            )
    for v in version_tables:
        out.append(f"CREATE TRIGGER trg_append_only BEFORE UPDATE OR DELETE ON {v} FOR EACH ROW EXECUTE FUNCTION audit.fn_block_update();")
        out.append(f"CREATE TRIGGER trg_audit AFTER INSERT ON {v} FOR EACH ROW EXECUTE FUNCTION audit.fn_row_audit();")
        out.append(f"ALTER TABLE {v} ENABLE ROW LEVEL SECURITY;")
        out.append(
            f"CREATE POLICY tenant_isolation ON {v} USING (tenant_id = audit.ctx('tenant_id')::uuid) "
            f"WITH CHECK (tenant_id = audit.ctx('tenant_id')::uuid);"
        )
    for s in schemas:
        out.append(f"GRANT USAGE ON SCHEMA {s.name} TO pharmaos_app, pharmaos_readonly;")
        out.append(f"GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA {s.name} TO pharmaos_app;")
        out.append(f"GRANT SELECT ON ALL TABLES IN SCHEMA {s.name} TO pharmaos_readonly;")
    # Non-GxP operational tables may be purged by the app.
    for s in schemas:
        for t in s.tables:
            if not t.gxp:
                out.append(f"GRANT DELETE ON {t.fq} TO pharmaos_app;")
    return "\n".join(out) + "\n"


def gen_erd(schemas: list[Schema], stats: dict) -> str:
    out = [
        "# 4. Detailed ERD",
        "",
        "> GENERATED by `tools/schema_gen.py` from `database/model/catalog.dsl` — do not edit by hand.",
        "",
        f"**{stats['tables']} business entities + {stats['versions']} version tables + "
        f"{stats['audit']} audit tables = {stats['total']} physical tables** across {len(schemas)} bounded-context schemas, "
        f"{stats['fks']} foreign keys.",
        "",
        "## Conventions (apply to every entity, omitted from diagrams for legibility)",
        "",
        "| Column | Purpose |",
        "|---|---|",
        "| `id uuid PK` | Surrogate key (app generates UUIDv7 for index locality; DB default is v4 fallback) |",
        "| `tenant_id uuid` | Row-level security boundary |",
        "| `site_id uuid` | Present on site-scoped (`S`) entities; drives ABAC |",
        "| `workflow_instance_id`, `workflow_state` | Present on workflow-enabled (`W`) entities |",
        "| `current_version_no` + `<table>_version` | Present on versioned (`V`) entities; snapshot is immutable |",
        "| `ext jsonb` | Admin-configured fields from the Field Builder (validated against `meta.field_definition`) |",
        "| `row_version` | Optimistic concurrency token |",
        "| `created_at/by`, `updated_at/by` | Stamps; full history lives in `audit.audit_trail` |",
        "",
        "Edges to `iam.app_user` and `org.site` are omitted from diagrams (nearly every entity has them).",
        "",
        "## Context map (schemas and cross-schema FK counts)",
        "",
        "```mermaid",
        "flowchart LR",
    ]
    edges: dict[tuple[str, str], int] = {}
    for s in schemas:
        for t in s.tables:
            for _, target in all_fks(t):
                ts = target.split(".")[0]
                if ts != s.name and target not in ERD_SKIP_TARGETS:
                    edges[(s.name, ts)] = edges.get((s.name, ts), 0) + 1
    for s in schemas:
        out.append(f'  {s.name}["{s.name}<br/>{len(s.tables)} entities"]')
    for (a, b), n in sorted(edges.items()):
        if n >= 3:
            out.append(f"  {a} -->|{n}| {b}")
    out += ["```", "", "_Only schema pairs with ≥3 FKs are drawn; full list in the per-schema tables below._", ""]

    for s in schemas:
        out += [f"## `{s.name}` — {s.description}", "", "```mermaid", "erDiagram"]
        names = {t.name for t in s.tables}
        for t in s.tables:
            out.append(f"  {t.name} {{")
            for c in t.columns:
                key = " FK" if c.fk else (" UK" if c.unique else "")
                out.append(f"    {MERMAID_TYPE[c.sqltype]} {c.name}{key}")
            out.append("  }")
        for t in s.tables:
            for col, target in all_fks(t):
                ts, tn = target.split(".")
                if ts == s.name and tn in names:
                    nn = next((c.not_null for c in t.columns if c.name == col), True)
                    left = "||" if nn else "|o"
                    out.append(f'  {tn} {left}--o{{ {t.name} : "{col}"')
        out += ["```", ""]
        cross = [
            (t.name, col, target)
            for t in s.tables for col, target in all_fks(t)
            if target.split(".")[0] != s.name and target not in ERD_SKIP_TARGETS
        ]
        if cross:
            out += ["Cross-domain references:", "", "| Entity | Column | References |", "|---|---|---|"]
            out += [f"| {a} | {b} | `{c}` |" for a, b, c in cross]
            out.append("")
    return "\n".join(out)


def gen_dictionary(schemas: list[Schema]) -> str:
    out = ["# Data Dictionary", "", "> GENERATED by `tools/schema_gen.py`. Standard columns (see ERD conventions) omitted.", ""]
    for s in schemas:
        out += [f"## {s.name} — {s.description}", ""]
        for t in s.tables:
            flags = "".join(sorted(t.flags)) or "—"
            extras = []
            if t.fq in APPEND_ONLY:
                extras.append("append-only")
            extras.append("audited" if t.gxp else "not audited")
            out += [f"### `{t.fq}`  (flags: {flags}; {', '.join(extras)})", "",
                    "| Column | Type | Null | Key |", "|---|---|---|---|"]
            for c in t.columns:
                key = f"FK → `{c.fk}`" if c.fk else ("unique/tenant" if c.unique else "")
                out.append(f"| {c.name} | {c.sqltype} | {'N' if c.not_null else 'Y'} | {key} |")
            for u in t.uniques:
                out.append(f"| _unique_ | ({', '.join(u)}) | | |")
            out.append("")
    return "\n".join(out)


def main() -> None:
    schemas = parse(CATALOG)
    known = {t.fq for s in schemas for t in s.tables}
    seen: set[str] = set()
    for s in schemas:
        for t in s.tables:
            if t.fq in seen:
                raise SystemExit(f"duplicate table {t.fq}")
            seen.add(t.fq)
            cols = [c.name for c in t.columns]
            if len(cols) != len(set(cols)):
                raise SystemExit(f"duplicate column in {t.fq}")
            for u in t.uniques:
                for c in u:
                    if c not in cols:
                        raise SystemExit(f"{t.fq}: unique on unknown column {c}")

    versions_sql, version_tables = gen_versions(schemas)
    fks = sum(len(all_fks(t)) for s in schemas for t in s.tables)
    stats = {
        "tables": len(known),
        "versions": len(version_tables),
        "audit": 4,  # audit_trail, audit_trail_default, audit_seal, audit_trail_review
        "fks": fks,
    }
    stats["total"] = stats["tables"] + stats["versions"] + stats["audit"]

    files = {
        OUT_SQL / "000_foundation.sql": gen_foundation(schemas),
        OUT_SQL / "010_tables.sql": gen_tables(schemas),
        OUT_SQL / "020_versions.sql": versions_sql,
        OUT_SQL / "030_foreign_keys.sql": gen_fks(schemas, known),
        OUT_SQL / "040_indexes.sql": gen_indexes(schemas),
        OUT_SQL / "050_triggers_rls.sql": gen_triggers(schemas, version_tables),
        OUT_ERD: gen_erd(schemas, stats),
        OUT_DICT: gen_dictionary(schemas),
    }
    check = "--check" in sys.argv
    stale = []
    for path, content in files.items():
        if check:
            if not path.exists() or path.read_text() != content:
                stale.append(str(path.relative_to(ROOT)))
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content)
    if stale:
        raise SystemExit("generated files are stale: " + ", ".join(stale))
    per_schema = ", ".join(f"{s.name}={len(s.tables)}" for s in schemas)
    print(f"schemas={len(schemas)} tables={stats['tables']} version_tables={stats['versions']} "
          f"total_physical={stats['total']} fks={fks}\n{per_schema}")


if __name__ == "__main__":
    main()
