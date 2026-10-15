-- 020_versions.sql — GENERATED — immutable version snapshots for V-flagged entities

CREATE TABLE iam.abac_policy_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES iam.abac_policy(id),
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
CREATE INDEX ix_iam_abac_policy_ver_eff ON iam.abac_policy_version (tenant_id, entity_id, effective_from);
CREATE TABLE meta.entity_definition_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES meta.entity_definition(id),
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
CREATE INDEX ix_meta_entity_definition_ver_eff ON meta.entity_definition_version (tenant_id, entity_id, effective_from);
CREATE TABLE meta.field_definition_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES meta.field_definition(id),
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
CREATE INDEX ix_meta_field_definition_ver_eff ON meta.field_definition_version (tenant_id, entity_id, effective_from);
CREATE TABLE meta.form_definition_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES meta.form_definition(id),
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
CREATE INDEX ix_meta_form_definition_ver_eff ON meta.form_definition_version (tenant_id, entity_id, effective_from);
CREATE TABLE meta.business_rule_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES meta.business_rule(id),
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
CREATE INDEX ix_meta_business_rule_ver_eff ON meta.business_rule_version (tenant_id, entity_id, effective_from);
CREATE TABLE meta.decision_table_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES meta.decision_table(id),
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
CREATE INDEX ix_meta_decision_table_ver_eff ON meta.decision_table_version (tenant_id, entity_id, effective_from);
CREATE TABLE meta.document_template_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES meta.document_template(id),
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
CREATE INDEX ix_meta_document_template_ver_eff ON meta.document_template_version (tenant_id, entity_id, effective_from);
CREATE TABLE meta.email_template_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES meta.email_template(id),
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
CREATE INDEX ix_meta_email_template_ver_eff ON meta.email_template_version (tenant_id, entity_id, effective_from);
CREATE TABLE meta.audit_policy_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES meta.audit_policy(id),
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
CREATE INDEX ix_meta_audit_policy_ver_eff ON meta.audit_policy_version (tenant_id, entity_id, effective_from);
CREATE TABLE wf.process_definition_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES wf.process_definition(id),
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
CREATE INDEX ix_wf_process_definition_ver_eff ON wf.process_definition_version (tenant_id, entity_id, effective_from);
CREATE TABLE wf.approval_route_template_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES wf.approval_route_template(id),
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
CREATE INDEX ix_wf_approval_route_template_ver_eff ON wf.approval_route_template_version (tenant_id, entity_id, effective_from);
CREATE TABLE esig.signature_policy_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES esig.signature_policy(id),
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
CREATE INDEX ix_esig_signature_policy_ver_eff ON esig.signature_policy_version (tenant_id, entity_id, effective_from);
CREATE TABLE notif.notification_rule_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES notif.notification_rule(id),
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
CREATE INDEX ix_notif_notification_rule_ver_eff ON notif.notification_rule_version (tenant_id, entity_id, effective_from);
CREATE TABLE qms.quality_metric_definition_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES qms.quality_metric_definition(id),
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
CREATE INDEX ix_qms_quality_metric_definition_ver_eff ON qms.quality_metric_definition_version (tenant_id, entity_id, effective_from);
CREATE TABLE qrm.risk_matrix_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES qrm.risk_matrix(id),
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
CREATE INDEX ix_qrm_risk_matrix_ver_eff ON qrm.risk_matrix_version (tenant_id, entity_id, effective_from);
CREATE TABLE sqm.supplier_questionnaire_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES sqm.supplier_questionnaire(id),
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
CREATE INDEX ix_sqm_supplier_questionnaire_ver_eff ON sqm.supplier_questionnaire_version (tenant_id, entity_id, effective_from);
CREATE TABLE aud.audit_checklist_template_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES aud.audit_checklist_template(id),
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
CREATE INDEX ix_aud_audit_checklist_template_ver_eff ON aud.audit_checklist_template_version (tenant_id, entity_id, effective_from);
CREATE TABLE trn.course_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES trn.course(id),
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
CREATE INDEX ix_trn_course_ver_eff ON trn.course_version (tenant_id, entity_id, effective_from);
CREATE TABLE trn.curriculum_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES trn.curriculum(id),
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
CREATE INDEX ix_trn_curriculum_ver_eff ON trn.curriculum_version (tenant_id, entity_id, effective_from);
CREATE TABLE trn.assessment_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES trn.assessment(id),
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
CREATE INDEX ix_trn_assessment_ver_eff ON trn.assessment_version (tenant_id, entity_id, effective_from);
CREATE TABLE val.requirement_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES val.requirement(id),
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
CREATE INDEX ix_val_requirement_ver_eff ON val.requirement_version (tenant_id, entity_id, effective_from);
CREATE TABLE val.test_protocol_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES val.test_protocol(id),
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
CREATE INDEX ix_val_test_protocol_ver_eff ON val.test_protocol_version (tenant_id, entity_id, effective_from);
CREATE TABLE mdm.material_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES mdm.material(id),
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
CREATE INDEX ix_mdm_material_ver_eff ON mdm.material_version (tenant_id, entity_id, effective_from);
CREATE TABLE mdm.bill_of_material_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES mdm.bill_of_material(id),
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
CREATE INDEX ix_mdm_bill_of_material_ver_eff ON mdm.bill_of_material_version (tenant_id, entity_id, effective_from);
CREATE TABLE mes.master_recipe_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES mes.master_recipe(id),
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
CREATE INDEX ix_mes_master_recipe_ver_eff ON mes.master_recipe_version (tenant_id, entity_id, effective_from);
CREATE TABLE mes.checklist_template_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES mes.checklist_template(id),
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
CREATE INDEX ix_mes_checklist_template_ver_eff ON mes.checklist_template_version (tenant_id, entity_id, effective_from);
CREATE TABLE wms.label_template_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES wms.label_template(id),
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
CREATE INDEX ix_wms_label_template_ver_eff ON wms.label_template_version (tenant_id, entity_id, effective_from);
CREATE TABLE lims.test_method_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES lims.test_method(id),
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
CREATE INDEX ix_lims_test_method_ver_eff ON lims.test_method_version (tenant_id, entity_id, effective_from);
CREATE TABLE lims.specification_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES lims.specification(id),
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
CREATE INDEX ix_lims_specification_ver_eff ON lims.specification_version (tenant_id, entity_id, effective_from);
CREATE TABLE lims.sampling_plan_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES lims.sampling_plan(id),
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
CREATE INDEX ix_lims_sampling_plan_ver_eff ON lims.sampling_plan_version (tenant_id, entity_id, effective_from);
CREATE TABLE eqp.calibration_plan_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES eqp.calibration_plan(id),
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
CREATE INDEX ix_eqp_calibration_plan_ver_eff ON eqp.calibration_plan_version (tenant_id, entity_id, effective_from);
CREATE TABLE eqp.cleaning_procedure_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES eqp.cleaning_procedure(id),
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
CREATE INDEX ix_eqp_cleaning_procedure_ver_eff ON eqp.cleaning_procedure_version (tenant_id, entity_id, effective_from);
CREATE TABLE rim.artwork_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES rim.artwork(id),
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
CREATE INDEX ix_rim_artwork_ver_eff ON rim.artwork_version (tenant_id, entity_id, effective_from);
CREATE TABLE rim.label_content_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES rim.label_content(id),
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
CREATE INDEX ix_rim_label_content_ver_eff ON rim.label_content_version (tenant_id, entity_id, effective_from);
CREATE TABLE rpt.dashboard_definition_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES rpt.dashboard_definition(id),
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
CREATE INDEX ix_rpt_dashboard_definition_ver_eff ON rpt.dashboard_definition_version (tenant_id, entity_id, effective_from);
CREATE TABLE rpt.report_definition_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES rpt.report_definition(id),
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
CREATE INDEX ix_rpt_report_definition_ver_eff ON rpt.report_definition_version (tenant_id, entity_id, effective_from);
CREATE TABLE intg.data_mapping_version (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL,
  entity_id uuid NOT NULL REFERENCES intg.data_mapping(id),
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
CREATE INDEX ix_intg_data_mapping_ver_eff ON intg.data_mapping_version (tenant_id, entity_id, effective_from);
