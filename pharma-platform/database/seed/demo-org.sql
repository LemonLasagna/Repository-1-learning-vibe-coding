-- DEMO DATA FOR DEVELOPMENT AND TESTS ONLY. Never load into TEST, VAL or PROD.
-- A minimal organisation for the development tenant (the tenant_id of the users in
-- deploy/keycloak/dev-import/pharmaos-realm.json): one enterprise, one legal entity, two sites.
-- Real organisations are created through master-data management, under change control.
-- Idempotent: rows have fixed ids and existing rows are left alone.
BEGIN;
-- The audit triggers require an actor and the RLS policies a tenant.
DO $$ BEGIN
  PERFORM set_config('app.tenant_id', '0000000a-0000-0000-0000-000000000000', true);
  PERFORM set_config('app.user_id', '00000000-0000-4000-8000-00000000d0e0', true);
  PERFORM set_config('app.user_display', 'PharmaOS installer (demo data)', true);
  PERFORM set_config('app.change_reason', 'Demo organisation for development', true);
  PERFORM set_config('app.client', 'database/seed/demo-org.sql', true);
END $$;

INSERT INTO org.enterprise (id, tenant_id, code, name, hq_country, created_by, updated_by)
VALUES ('d0000000-0000-4000-8000-000000000001', '0000000a-0000-0000-0000-000000000000', 'DEMO', 'Demo Pharma Group', 'DE',
        '00000000-0000-4000-8000-00000000d0e0', '00000000-0000-4000-8000-00000000d0e0')
ON CONFLICT DO NOTHING;

INSERT INTO org.region (id, tenant_id, enterprise_id, code, name, created_by, updated_by)
VALUES ('d0000000-0000-4000-8000-000000000002', '0000000a-0000-0000-0000-000000000000', 'd0000000-0000-4000-8000-000000000001',
        'EU', 'Europe', '00000000-0000-4000-8000-00000000d0e0', '00000000-0000-4000-8000-00000000d0e0')
ON CONFLICT DO NOTHING;

INSERT INTO org.country (id, tenant_id, region_id, iso_code, name, default_language, created_by, updated_by)
VALUES ('d0000000-0000-4000-8000-000000000003', '0000000a-0000-0000-0000-000000000000', 'd0000000-0000-4000-8000-000000000002',
        'DE', 'Germany', 'de', '00000000-0000-4000-8000-00000000d0e0', '00000000-0000-4000-8000-00000000d0e0')
ON CONFLICT DO NOTHING;

INSERT INTO org.legal_entity (id, tenant_id, country_id, code, name, base_currency, created_by, updated_by)
VALUES ('d0000000-0000-4000-8000-000000000004', '0000000a-0000-0000-0000-000000000000', 'd0000000-0000-4000-8000-000000000003',
        'DEMO-DE', 'Demo Pharma GmbH', 'EUR', '00000000-0000-4000-8000-00000000d0e0', '00000000-0000-4000-8000-00000000d0e0')
ON CONFLICT DO NOTHING;

INSERT INTO org.site (id, tenant_id, legal_entity_id, code, name, timezone, status, created_by, updated_by)
VALUES ('d0000000-0000-4000-8000-000000000010', '0000000a-0000-0000-0000-000000000000', 'd0000000-0000-4000-8000-000000000004',
        'BER', 'Berlin (solid dose)', 'Europe/Berlin', 'ACTIVE', '00000000-0000-4000-8000-00000000d0e0', '00000000-0000-4000-8000-00000000d0e0'),
       ('d0000000-0000-4000-8000-000000000011', '0000000a-0000-0000-0000-000000000000', 'd0000000-0000-4000-8000-000000000004',
        'HAM', 'Hamburg (sterile)', 'Europe/Berlin', 'ACTIVE', '00000000-0000-4000-8000-00000000d0e0', '00000000-0000-4000-8000-00000000d0e0')
ON CONFLICT DO NOTHING;
COMMIT;
