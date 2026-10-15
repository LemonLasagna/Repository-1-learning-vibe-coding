-- =====================================================================
-- Data-integrity control tests (traceable to OQ-DI-001..009 in docs/12-iqoqpq-templates.md)
-- Run:  psql -v ON_ERROR_STOP=1 -d <db> -f database/tests/integrity_tests.sql
-- Each block raises on failure; a clean run prints "ALL DATA-INTEGRITY TESTS PASSED".
-- =====================================================================
\set QUIET on
BEGIN;

-- Fixture: tenant A / tenant B, run as the application role (RLS enforced).
SELECT set_config('app.tenant_id', '00000000-0000-0000-0000-00000000000a', true);
SET LOCAL ROLE pharmaos_app;

-- OQ-DI-001: a GxP write without an authenticated actor is rejected.
DO $$ BEGIN
  BEGIN
    INSERT INTO mdm.uom (tenant_id, code, name, dimension, created_by, updated_by)
    VALUES ('00000000-0000-0000-0000-00000000000a', 'KG', 'Kilogram', 'MASS', gen_random_uuid(), gen_random_uuid());
    RAISE EXCEPTION 'OQ-DI-001 FAILED: anonymous write accepted';
  EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'OQ-DI-001 pass';
  END;
END $$;

SELECT set_config('app.user_id', '11111111-1111-1111-1111-111111111111', true);
SELECT set_config('app.user_display', 'Test Analyst (tanalyst)', true);
SELECT set_config('app.change_reason', 'Initial master data load', true);

-- OQ-DI-002: insert is captured with actor, reason, timestamp and hash.
INSERT INTO mdm.uom (id, tenant_id, code, name, dimension, created_by, updated_by)
VALUES ('22222222-2222-2222-2222-222222222222', '00000000-0000-0000-0000-00000000000a', 'KG', 'Kilogram', 'MASS',
        '11111111-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111');
RESET ROLE;
DO $$ DECLARE r audit.audit_trail; BEGIN
  SELECT * INTO r FROM audit.audit_trail WHERE record_id = '22222222-2222-2222-2222-222222222222' AND operation = 'INSERT';
  IF r.actor_user_id IS DISTINCT FROM '11111111-1111-1111-1111-111111111111' OR r.reason <> 'Initial master data load'
     OR r.row_hash IS NULL OR r.new_values ->> 'code' <> 'KG' THEN
    RAISE EXCEPTION 'OQ-DI-002 FAILED: %', row_to_json(r);
  END IF;
  RAISE NOTICE 'OQ-DI-002 pass';
END $$;
SET LOCAL ROLE pharmaos_app;

-- OQ-DI-003: update captures only changed fields with old and new values; row_version increments.
SELECT set_config('app.change_reason', 'Correct display name per CC-2026-0042', true);
UPDATE mdm.uom SET name = 'Kilogramme', row_version = 1 WHERE id = '22222222-2222-2222-2222-222222222222';
RESET ROLE;
DO $$ DECLARE r audit.audit_trail; v int; BEGIN
  SELECT * INTO r FROM audit.audit_trail WHERE record_id = '22222222-2222-2222-2222-222222222222' AND operation = 'UPDATE';
  SELECT row_version INTO v FROM mdm.uom WHERE id = '22222222-2222-2222-2222-222222222222';
  IF r.changed_fields <> ARRAY['name'] OR r.old_values ->> 'name' <> 'Kilogram'
     OR r.new_values ->> 'name' <> 'Kilogramme' OR v <> 2 THEN
    RAISE EXCEPTION 'OQ-DI-003 FAILED: % (row_version=%)', row_to_json(r), v;
  END IF;
  RAISE NOTICE 'OQ-DI-003 pass';
END $$;
SET LOCAL ROLE pharmaos_app;

-- OQ-DI-004: stale row_version (lost update) is rejected.
DO $$ BEGIN
  BEGIN
    UPDATE mdm.uom SET name = 'KG (stale)', row_version = 1 WHERE id = '22222222-2222-2222-2222-222222222222';
    RAISE EXCEPTION 'OQ-DI-004 FAILED: stale update accepted';
  EXCEPTION WHEN serialization_failure THEN RAISE NOTICE 'OQ-DI-004 pass';
  END;
END $$;

-- OQ-DI-005: physical delete of a GxP record is blocked.
DO $$ BEGIN
  BEGIN
    DELETE FROM mdm.uom WHERE id = '22222222-2222-2222-2222-222222222222';
    RAISE EXCEPTION 'OQ-DI-005 FAILED: delete accepted';
  EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'OQ-DI-005 pass';
  END;
END $$;

-- OQ-DI-006: tenant isolation — tenant B cannot see or write tenant A rows.
SELECT set_config('app.tenant_id', '00000000-0000-0000-0000-00000000000b', true);
DO $$ DECLARE n int; BEGIN
  SELECT count(*) INTO n FROM mdm.uom;
  IF n <> 0 THEN RAISE EXCEPTION 'OQ-DI-006 FAILED: tenant B sees % rows', n; END IF;
  BEGIN
    INSERT INTO mdm.uom (tenant_id, code, name, dimension, created_by, updated_by)
    VALUES ('00000000-0000-0000-0000-00000000000a', 'G', 'Gram', 'MASS', gen_random_uuid(), gen_random_uuid());
    RAISE EXCEPTION 'OQ-DI-006 FAILED: cross-tenant insert accepted';
  EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'OQ-DI-006 pass';
  END;
END $$;
SELECT set_config('app.tenant_id', '00000000-0000-0000-0000-00000000000a', true);

-- OQ-DI-007: the application role cannot modify or delete audit trail rows.
DO $$ BEGIN
  BEGIN
    UPDATE audit.audit_trail SET reason = 'tampered';
    RAISE EXCEPTION 'OQ-DI-007 FAILED: audit update accepted';
  EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'OQ-DI-007 pass (privilege)';
  END;
END $$;
RESET ROLE;
DO $$ BEGIN  -- even the owner/superuser path is blocked by trigger
  BEGIN
    DELETE FROM audit.audit_trail;
    RAISE EXCEPTION 'OQ-DI-007 FAILED: superuser audit delete accepted';
  EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'OQ-DI-007 pass (trigger)';
  END;
END $$;

-- OQ-DI-008: executed electronic signatures are immutable.
SELECT set_config('app.change_reason', '', true);
INSERT INTO esig.signature_meaning (id, tenant_id, code, label, created_by, updated_by)
VALUES ('33333333-3333-3333-3333-333333333333', '00000000-0000-0000-0000-00000000000a', 'APPROVED', 'Approved',
        '11111111-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111');
INSERT INTO meta.module (id, tenant_id, code, name, is_enabled, created_by, updated_by)
VALUES ('44444444-4444-4444-4444-444444444444', '00000000-0000-0000-0000-00000000000a', 'MDM', 'Master Data', true,
        '11111111-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111');
INSERT INTO meta.entity_definition (id, tenant_id, module_id, code, name, is_custom, is_gxp, created_by, updated_by)
VALUES ('55555555-5555-5555-5555-555555555555', '00000000-0000-0000-0000-00000000000a', '44444444-4444-4444-4444-444444444444',
        'UOM', 'Unit of Measure', false, true, '11111111-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111');
INSERT INTO iam.app_user (id, tenant_id, username, display_name, email, idp_subject, status, esig_enabled, created_by, updated_by)
VALUES ('11111111-1111-1111-1111-111111111111', '00000000-0000-0000-0000-00000000000a', 'tanalyst', 'Test Analyst',
        'ta@example.test', 'kc|tanalyst', 'ACTIVE', true, '11111111-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111');
INSERT INTO esig.signature (id, tenant_id, signer_id, signer_full_name, meaning_id, meaning_label, signed_at,
       entity_definition_id, record_id, record_version, record_hash, auth_method, signature_hash, created_by, updated_by)
VALUES ('66666666-6666-6666-6666-666666666666', '00000000-0000-0000-0000-00000000000a', '11111111-1111-1111-1111-111111111111',
        'Test Analyst', '33333333-3333-3333-3333-333333333333', 'Approved', now(), '55555555-5555-5555-5555-555555555555',
        '22222222-2222-2222-2222-222222222222', '2', repeat('a', 64), 'PASSWORD', repeat('b', 64),
        '11111111-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111');
DO $$ BEGIN
  BEGIN
    UPDATE esig.signature SET meaning_label = 'Reviewed' WHERE id = '66666666-6666-6666-6666-666666666666';
    RAISE EXCEPTION 'OQ-DI-008 FAILED: signature altered';
  EXCEPTION WHEN insufficient_privilege THEN RAISE NOTICE 'OQ-DI-008 pass';
  END;
END $$;

-- OQ-DI-009: sealing detects tampering performed below the application (e.g. by a DBA with triggers disabled).
DO $$ DECLARE s audit.audit_seal; bad int; BEGIN
  s := audit.seal_range((SELECT min(seq) FROM audit.audit_trail), (SELECT max(seq) FROM audit.audit_trail));
  SELECT count(*) INTO bad FROM audit.verify_seals() WHERE NOT ok;
  IF bad <> 0 THEN RAISE EXCEPTION 'OQ-DI-009 FAILED: fresh seal does not verify'; END IF;
  ALTER TABLE audit.audit_trail DISABLE TRIGGER trg_protect_audit;
  UPDATE audit.audit_trail SET row_hash = repeat('0', 64) WHERE seq = s.from_seq;
  ALTER TABLE audit.audit_trail ENABLE TRIGGER trg_protect_audit;
  SELECT count(*) INTO bad FROM audit.verify_seals() WHERE NOT ok;
  IF bad = 0 THEN RAISE EXCEPTION 'OQ-DI-009 FAILED: tampering not detected'; END IF;
  RAISE NOTICE 'OQ-DI-009 pass';
END $$;

ROLLBACK;
\echo ALL DATA-INTEGRITY TESTS PASSED
