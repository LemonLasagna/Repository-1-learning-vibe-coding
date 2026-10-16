# =====================================================================
# PharmaOS entity catalog — single source of truth for:
#   * database/schema/*.sql   (PostgreSQL 16 DDL)
#   * docs/04-erd.md          (Mermaid ERD per bounded context)
#   * database/data-dictionary.md
# Regenerate with:  python3 tools/schema_gen.py
#
# Grammar
#   @schema <name> | <description>
#   <table> [FLAGS] | col:type, col:type, ... [| U(colA,colB) ...]
#
# FLAGS
#   S  site-scoped        -> adds site_id NOT NULL FK org.site
#   W  workflow-enabled   -> adds workflow_instance_id, workflow_state
#   V  versioned          -> generates <table>_version snapshot table
#   N  non-GxP            -> no audit trigger, deletes allowed
#
# Types
#   c varchar(50)  s varchar(255)  l varchar(1000)  t text
#   i integer  b bigint  n numeric(18,6)  m numeric(19,4)
#   bool  d date  ts timestamptz  tm time  j jsonb  u uuid  st varchar(30)  dur interval  h char(64) sha-256 hex
#   >schema.table   uuid FK
# Modifiers (suffix):  !  NOT NULL      *  unique per tenant
# =====================================================================

@schema org | Organization & multi-site hierarchy
enterprise | code:c!*, name:s!, hq_country:c
region | enterprise_id:>org.enterprise!, code:c!*, name:s!
country | region_id:>org.region!, iso_code:c!*, name:s!, health_authority:s, default_language:c
legal_entity | country_id:>org.country!, code:c!*, name:s!, tax_id:c, base_currency:c
business_unit | enterprise_id:>org.enterprise!, code:c!*, name:s!, industry_segment:st!
site | legal_entity_id:>org.legal_entity!, business_unit_id:>org.business_unit, code:c!*, name:s!, timezone:c!, gmp_license_no:c, fei_number:c, duns:c, address:j, status:st!
plant | site_id:>org.site!, code:c!*, name:s!
building | plant_id:>org.plant!, code:c!*, name:s!
area | building_id:>org.building!, code:c!*, name:s!, cleanroom_grade:c, area_type:st
room | area_id:>org.area!, code:c!*, name:s!, cleanroom_grade:c, pressure_cascade:c
department | site_id:>org.site, code:c!*, name:s!, parent_id:>org.department, head_user_id:>iam.app_user
cost_center | legal_entity_id:>org.legal_entity!, code:c!*, name:s!
org_node | node_type:st!, ref_id:u!, parent_id:>org.org_node, path:l!, depth:i!
calendar | site_id:>org.site, code:c!*, name:s!
calendar_holiday | calendar_id:>org.calendar!, holiday_date:d!, name:s
shift | site_id:>org.site!, code:c!*, start_time:tm!, end_time:tm!

@schema iam | Identity, access, RBAC + ABAC
app_user | username:c!*, display_name:s!, email:s!, employee_no:c, idp_subject:s!, home_site_id:>org.site, department_id:>org.department, manager_id:>iam.app_user, job_title:s, status:st!, locked_at:ts, last_login_at:ts, esig_enabled:bool!
user_identity_link | user_id:>iam.app_user!, provider:c!, external_id:s! | U(provider,external_id)
role | code:c!*, name:s!, description:l, role_type:st!, is_gxp:bool!
permission | code:c!*, resource:c!, action:c!, description:l
role_permission | role_id:>iam.role!, permission_id:>iam.permission! | U(role_id,permission_id)
user_role_assignment | user_id:>iam.app_user!, role_id:>iam.role!, org_node_id:>org.org_node, valid_from:ts!, valid_to:ts, granted_by:>iam.app_user!, justification:l
user_group | code:c!*, name:s!
user_group_member | group_id:>iam.user_group!, user_id:>iam.app_user! | U(group_id,user_id)
group_role_assignment | group_id:>iam.user_group!, role_id:>iam.role!, org_node_id:>org.org_node
abac_policy V | code:c!*, name:s!, effect:st!, resource:c!, action:c!, condition_expr:t!, priority:i!
abac_attribute | code:c!*, source:st!, data_type:st!, resolver:s
segregation_of_duty_rule | code:c!*, name:s!, conflicting_permission_a:>iam.permission!, conflicting_permission_b:>iam.permission!, enforcement:st!
delegation | delegator_id:>iam.app_user!, delegate_id:>iam.app_user!, scope:j, valid_from:ts!, valid_to:ts!, reason:l!
access_review_campaign W | code:c!*, name:s!, due_date:d!, status:st!
access_review_item | campaign_id:>iam.access_review_campaign!, user_role_assignment_id:>iam.user_role_assignment!, reviewer_id:>iam.app_user!, decision:st, decided_at:ts, comment:l
session_log | user_id:>iam.app_user!, session_id:s!, ip_address:c, user_agent:l, login_at:ts!, logout_at:ts, logout_reason:st
login_attempt | username:c!, success:bool!, ip_address:c, attempted_at:ts!, failure_reason:st
api_client | client_id:c!*, name:s!, owner_user_id:>iam.app_user!, scopes:j, status:st!
service_account | code:c!*, name:s!, api_client_id:>iam.api_client, status:st!
training_gate | permission_id:>iam.permission!, curriculum_id:>trn.curriculum!, enforcement:st!

@schema meta | Metadata-driven configuration framework (builders)
module | code:c!*, name:s!, is_enabled:bool!
entity_definition V | module_id:>meta.module!, code:c!*, name:s!, physical_table:s, is_custom:bool!, is_gxp:bool!, numbering_scheme_id:>meta.numbering_scheme, default_workflow_id:>wf.process_definition
field_definition V | entity_definition_id:>meta.entity_definition!, code:c!, label:s!, data_type:st!, is_required:bool!, is_system:bool!, max_length:i, num_precision:i, num_scale:i, lookup_list_id:>meta.lookup_list, reference_entity_id:>meta.entity_definition, default_value:l, validation_expr:t, is_gxp_critical:bool!, is_audited:bool!, storage:st! | U(entity_definition_id,code)
lookup_list | code:c!*, name:s!, is_hierarchical:bool!
lookup_value | lookup_list_id:>meta.lookup_list!, code:c!, label:s!, parent_id:>meta.lookup_value, sort_order:i, is_active:bool! | U(lookup_list_id,code)
lookup_value_translation | lookup_value_id:>meta.lookup_value!, locale:c!, label:s! | U(lookup_value_id,locale)
form_definition V | entity_definition_id:>meta.entity_definition!, code:c!*, name:s!, form_type:st!, layout:j!, status:st!
form_section | form_definition_id:>meta.form_definition!, code:c!, title:s!, sort_order:i!, visibility_rule_id:>meta.business_rule
form_field_placement | form_section_id:>meta.form_section!, field_definition_id:>meta.field_definition!, sort_order:i!, col_span:i, is_readonly:bool!, visibility_rule_id:>meta.business_rule, required_rule_id:>meta.business_rule
form_state_override | form_definition_id:>meta.form_definition!, workflow_state_code:c!, field_definition_id:>meta.field_definition!, access:st!
business_rule V | code:c!*, name:s!, entity_definition_id:>meta.entity_definition, rule_type:st!, trigger_event:st!, expression:t!, expression_language:st!, severity:st, message:l, is_active:bool!
business_rule_action | business_rule_id:>meta.business_rule!, action_type:st!, parameters:j!, sort_order:i!
decision_table V | code:c!*, name:s!, hit_policy:st!, inputs:j!, outputs:j!
decision_table_row | decision_table_id:>meta.decision_table!, row_no:i!, conditions:j!, results:j!
numbering_scheme | code:c!*, pattern:s!, reset_policy:st!, site_scoped:bool!
numbering_sequence | numbering_scheme_id:>meta.numbering_scheme!, scope_key:s!, current_value:b! | U(numbering_scheme_id,scope_key)
menu | code:c!*, name:s!, app_area:st!
menu_item | menu_id:>meta.menu!, parent_id:>meta.menu_item, label:s!, route:l, icon:c, permission_id:>iam.permission, sort_order:i!
list_view_definition | entity_definition_id:>meta.entity_definition!, code:c!*, name:s!, columns:j!, filters:j, sort_spec:j, is_default:bool!
document_template V | code:c!*, name:s!, template_type:st!, entity_definition_id:>meta.entity_definition, stored_object_id:>file.stored_object, merge_fields:j
email_template V | code:c!*, name:s!, subject_template:l!, body_template:t!, locale:c!
audit_policy V | entity_definition_id:>meta.entity_definition!, field_definition_id:>meta.field_definition, capture_mode:st!, reason_required:bool!, review_frequency_days:i
config_package W | code:c!*, name:s!, version_label:c!, source_environment:c!, content_hash:h!, manifest:j!, status:st!
config_package_item | config_package_id:>meta.config_package!, artifact_type:st!, artifact_id:u!, artifact_version:c!, payload:j!
config_deployment | config_package_id:>meta.config_package!, target_environment:c!, deployed_at:ts!, deployed_by:>iam.app_user!, result:st!, log:t
localization_resource | locale:c!, resource_key:s!, value:t! | U(locale,resource_key)
feature_flag | code:c!*, description:l, is_enabled:bool!, scope:j
custom_entity_record W | entity_definition_id:>meta.entity_definition!, record_no:c!, data:j!, status:st!

@schema wf | BPMN workflow engine
process_definition V | code:c!*, name:s!, entity_definition_id:>meta.entity_definition, bpmn_xml:t!, model_json:j!, status:st!, is_gxp:bool!
process_node | process_definition_id:>wf.process_definition!, node_key:c!, node_type:st!, name:s!, config:j | U(process_definition_id,node_key)
process_transition | process_definition_id:>wf.process_definition!, from_node_id:>wf.process_node!, to_node_id:>wf.process_node!, condition_expr:t, requires_esig:bool!, esig_meaning_id:>esig.signature_meaning, sort_order:i
workflow_draft | code:c!, version_no:i!, initial_state:c!, transitions:j!, status:st!, validation_errors:j | U(code,version_no)
approval_route_template V | code:c!*, name:s!, mode:st!, quorum:i, config:j
approval_route_step | route_template_id:>wf.approval_route_template!, step_no:i!, mode:st!, assignee_rule:j!, min_approvals:i!, due_in:dur, esig_meaning_id:>esig.signature_meaning
process_instance | process_definition_id:>wf.process_definition!, definition_version:c!, entity_definition_id:>meta.entity_definition, business_key:c!, record_id:u!, status:st!, started_at:ts!, completed_at:ts, started_by:>iam.app_user!
token | process_instance_id:>wf.process_instance!, node_id:>wf.process_node!, parent_token_id:>wf.token, status:st!, arrived_at:ts!
task | process_instance_id:>wf.process_instance!, node_id:>wf.process_node!, token_id:>wf.token, name:s!, status:st!, priority:st, due_at:ts, claimed_by:>iam.app_user, completed_by:>iam.app_user, completed_at:ts, outcome:c
task_candidate | task_id:>wf.task!, candidate_type:st!, user_id:>iam.app_user, role_id:>iam.role, group_id:>iam.user_group
task_comment | task_id:>wf.task!, author_id:>iam.app_user!, body:t!
task_delegation | task_id:>wf.task!, from_user_id:>iam.app_user!, to_user_id:>iam.app_user!, reason:l!, delegation_id:>iam.delegation
escalation_policy | code:c!*, name:s!, levels:j!
escalation_event | task_id:>wf.task!, escalation_policy_id:>wf.escalation_policy!, level:i!, fired_at:ts!, notified:j
timer_job N | process_instance_id:>wf.process_instance, task_id:>wf.task, fire_at:ts!, job_type:st!, payload:j, status:st!, attempts:i!
process_variable | process_instance_id:>wf.process_instance!, name:c!, value:j | U(process_instance_id,name)
process_history | process_instance_id:>wf.process_instance!, from_node_key:c, to_node_key:c!, transition_id:>wf.process_transition, actor_id:>iam.app_user, occurred_at:ts!, comment:l, signature_id:>esig.signature
rejection_loop_counter | process_instance_id:>wf.process_instance!, node_key:c!, loop_count:i! | U(process_instance_id,node_key)
sla_definition | code:c!*, entity_definition_id:>meta.entity_definition, target_duration:dur!, warning_at_pct:i, business_calendar_id:>org.calendar
sla_tracking | sla_definition_id:>wf.sla_definition!, process_instance_id:>wf.process_instance!, due_at:ts!, breached:bool!, breached_at:ts
external_event_subscription N | process_instance_id:>wf.process_instance!, event_type:c!, correlation_key:s!, status:st!
user_out_of_office | user_id:>iam.app_user!, start_at:ts!, end_at:ts!, delegate_id:>iam.app_user
workflow_simulation_run N | process_definition_id:>wf.process_definition!, scenario:j!, result:j, executed_at:ts!

@schema esig | Electronic signatures (21 CFR Part 11 / Annex 11)
signature_meaning | code:c!*, label:s!, description:l
signature_policy V | code:c!*, name:s!, entity_definition_id:>meta.entity_definition, action_code:c!, meaning_id:>esig.signature_meaning!, auth_method:st!, require_reason:bool!, require_second_person:bool!, manifest_template:l
signature | signer_id:>iam.app_user!, signer_full_name:s!, meaning_id:>esig.signature_meaning!, meaning_label:s!, signed_at:ts!, entity_definition_id:>meta.entity_definition!, record_id:u!, record_version:c!, record_hash:h!, auth_method:st!, reason:l, policy_id:>esig.signature_policy, signature_hash:h!, witness_signature_id:>esig.signature
signature_challenge N | user_id:>iam.app_user!, nonce:c!, issued_at:ts!, expires_at:ts!, consumed_at:ts, method:st!
signature_failure | user_id:>iam.app_user!, attempted_at:ts!, reason:st!, record_id:u
signature_manifest | signature_id:>esig.signature!, rendered_text:t!, locale:c!
user_esig_acknowledgement | user_id:>iam.app_user!, acknowledged_at:ts!, statement_version:c!, signature_id:>esig.signature
org_certification | legal_entity_id:>org.legal_entity!, submitted_to:s!, submitted_on:d!, stored_object_id:>file.stored_object

@schema notif | Notification builder & delivery
channel | code:c!*, channel_type:st!, config:j
notification_rule V | code:c!*, name:s!, entity_definition_id:>meta.entity_definition, trigger_event:c!, condition_expr:t, recipient_rule:j!, email_template_id:>meta.email_template, channel_id:>notif.channel!, digest_mode:st
notification N | rule_id:>notif.notification_rule, recipient_id:>iam.app_user!, channel_id:>notif.channel!, subject:l, body:t, record_ref:j, status:st!, sent_at:ts, read_at:ts
notification_delivery_attempt N | notification_id:>notif.notification!, attempted_at:ts!, result:st!, provider_message_id:s, error:l
user_notification_preference N | user_id:>iam.app_user!, event_category:c!, channel_id:>notif.channel!, is_enabled:bool!
announcement | title:s!, body:t!, audience:j, publish_from:ts!, publish_to:ts
record_subscription N | user_id:>iam.app_user!, entity_definition_id:>meta.entity_definition!, record_id:u!
digest_batch N | recipient_id:>iam.app_user!, period_start:ts!, period_end:ts!, sent_at:ts

@schema file | Object storage index (S3-compatible, self-hosted) & retention
storage_bucket | code:c!*, minio_bucket:s!, retention_class:st!, worm_enabled:bool!
stored_object | bucket_id:>file.storage_bucket!, object_key:l!, file_name:s!, mime_type:s!, size_bytes:b!, sha256:h!, uploaded_by:>iam.app_user!, virus_scan_status:st!, retention_until:d, legal_hold:bool!
attachment | stored_object_id:>file.stored_object!, entity_definition_id:>meta.entity_definition!, record_id:u!, category:c, description:l
rendition | stored_object_id:>file.stored_object!, rendition_type:st!, rendition_object_id:>file.stored_object!, status:st!
retention_policy | code:c!*, name:s!, record_class:c!, retention_period:dur!, trigger_event:st!, disposition:st!

@schema dms | Document management
document_type | code:c!*, name:s!, category:st!, numbering_scheme_id:>meta.numbering_scheme, review_period_months:i, requires_training:bool!, lifecycle_definition_id:>wf.process_definition, template_id:>meta.document_template
document W | document_type_id:>dms.document_type!, doc_number:c!*, title:l!, owner_id:>iam.app_user!, department_id:>org.department, current_version_id:>dms.document_version, effective_version_id:>dms.document_version, status:st!, confidentiality:st!, next_review_date:d, language:c
document_version | document_id:>dms.document!, major:i!, minor:i!, status:st!, change_summary:l, stored_object_id:>file.stored_object, rendition_object_id:>file.stored_object, content_hash:h, effective_date:d, superseded_date:d, obsolete_date:d, change_control_id:>qms.change_control | U(document_id,major,minor)
document_version_signature | document_version_id:>dms.document_version!, signature_id:>esig.signature!, role_in_review:st!
document_review_cycle W | document_version_id:>dms.document_version!, cycle_type:st!, started_at:ts!, due_at:ts, outcome:st
document_review_comment | review_cycle_id:>dms.document_review_cycle!, reviewer_id:>iam.app_user!, location_ref:s, body:t!, resolution:st, resolved_by:>iam.app_user
document_relation | source_document_id:>dms.document!, target_document_id:>dms.document!, relation_type:st!
document_site_applicability | document_id:>dms.document!, site_id:>org.site! | U(document_id,site_id)
document_distribution | document_version_id:>dms.document_version!, recipient_id:>iam.app_user, location:s, copy_no:i, distribution_type:st!, issued_at:ts!, retrieved_at:ts
controlled_print | document_version_id:>dms.document_version!, printed_by:>iam.app_user!, printed_at:ts!, copy_no:i!, purpose:l!, watermark:s!, reconciled_at:ts, reconciled_by:>iam.app_user
periodic_review W | document_id:>dms.document!, due_date:d!, reviewer_id:>iam.app_user!, outcome:st, completed_at:ts, signature_id:>esig.signature
document_keyword | document_id:>dms.document!, keyword:s!
document_checkout | document_id:>dms.document!, checked_out_by:>iam.app_user!, checked_out_at:ts!, checked_in_at:ts
document_read_acknowledgement | document_version_id:>dms.document_version!, user_id:>iam.app_user!, acknowledged_at:ts!, signature_id:>esig.signature
executed_form W | document_version_id:>dms.document_version!, record_no:c!*, data:j!, status:st!, completed_by:>iam.app_user, completed_at:ts
logbook S | code:c!*, name:s!, logbook_type:st!, equipment_id:>eqp.equipment, room_id:>org.room, status:st!
logbook_entry | logbook_id:>dms.logbook!, entry_at:ts!, entry_by:>iam.app_user!, activity:c!, data:j!, verified_by:>iam.app_user, verified_at:ts, correction_of_id:>dms.logbook_entry
document_obsolescence | document_id:>dms.document!, reason:l!, effective_date:d!, change_control_id:>qms.change_control
document_translation | source_version_id:>dms.document_version!, translated_version_id:>dms.document_version!, locale:c!, certified:bool!
binder | code:c!*, name:s!, binder_type:st!
binder_item | binder_id:>dms.binder!, document_id:>dms.document!, sort_order:i!
watermark_rule | code:c!*, status_applies:st!, text_template:s!

@schema qms | Quality management processes
quality_event W S | event_no:c!*, event_type:st!, title:l!, description:t!, reported_by:>iam.app_user!, reported_at:ts!, occurred_at:ts, area_id:>org.area, triage_outcome:st, status:st!
deviation W S | deviation_no:c!*, quality_event_id:>qms.quality_event, title:l!, description:t!, classification:st!, deviation_type:st!, is_planned:bool!, occurred_at:ts!, discovered_at:ts!, area_id:>org.area, product_id:>mdm.product, immediate_action:t, impact_assessment:t, root_cause_summary:t, risk_score:i, owner_id:>iam.app_user!, qa_owner_id:>iam.app_user, due_date:d!, closed_at:ts, status:st!, recurrence_flag:bool!
deviation_batch | deviation_id:>qms.deviation!, batch_id:>mes.batch!, impact:st | U(deviation_id,batch_id)
deviation_material | deviation_id:>qms.deviation!, material_id:>mdm.material!, lot_id:>wms.lot
deviation_equipment | deviation_id:>qms.deviation!, equipment_id:>eqp.equipment!
due_date_extension | source_type:st!, source_id:u!, current_due_date:d!, requested_due_date:d!, justification:l!, approved_signature_id:>esig.signature, status:st!
investigation W | source_type:st!, source_id:u!, investigation_no:c!*, lead_id:>iam.app_user!, method:st!, scope:t, conclusion:t, status:st!
rca_five_why | investigation_id:>qms.investigation!, problem_statement:l!, why_level:i!, question:l!, answer:l!, parent_id:>qms.rca_five_why
rca_fishbone | investigation_id:>qms.investigation!, effect:l!, model:st!
rca_fishbone_cause | fishbone_id:>qms.rca_fishbone!, category:st!, cause:l!, parent_id:>qms.rca_fishbone_cause, is_root_cause:bool!, evidence:l
rca_is_is_not | investigation_id:>qms.investigation!, dimension:st!, is_text:l, is_not_text:l, distinction:l
root_cause | investigation_id:>qms.investigation!, category:st!, subcategory:c, description:l!, is_primary:bool!
capa W S | capa_no:c!*, source_type:st!, source_id:u, title:l!, problem_statement:t!, capa_type:st!, owner_id:>iam.app_user!, root_cause_id:>qms.root_cause, priority:st!, due_date:d!, status:st!, closed_at:ts
capa_action | capa_id:>qms.capa!, action_no:i!, action_type:st!, description:t!, assignee_id:>iam.app_user!, due_date:d!, completed_at:ts, evidence:t, status:st!, change_control_id:>qms.change_control
capa_effectiveness_check W | capa_id:>qms.capa!, criteria:t!, method:l, planned_date:d!, performed_at:ts, result:st, evidence:t, performed_by:>iam.app_user
capa_link | capa_id:>qms.capa!, linked_type:st!, linked_id:u!
change_control W S | cc_no:c!*, title:l!, description:t!, change_type:st!, change_category:st!, justification:t!, is_temporary:bool!, temporary_end_date:d, owner_id:>iam.app_user!, risk_assessment_id:>qrm.risk_assessment, regulatory_impact:st, implementation_due:d, effective_date:d, status:st!
change_impact_assessment | change_control_id:>qms.change_control!, impacted_area:st!, assessor_id:>iam.app_user!, is_impacted:bool!, assessment:t, action_required:t
change_affected_item | change_control_id:>qms.change_control!, item_type:st!, item_id:u!, item_ref:s
change_task | change_control_id:>qms.change_control!, task_no:i!, phase:st!, description:t!, assignee_id:>iam.app_user!, due_date:d!, status:st!, completed_at:ts
change_regulatory_assessment | change_control_id:>qms.change_control!, country_id:>org.country!, variation_category:c, filing_required:bool!, variation_id:>rim.variation
complaint W S | complaint_no:c!*, received_at:ts!, channel:st!, complainant_type:st!, complainant_name:s, country_id:>org.country, product_id:>mdm.product, lot_no:c, description:t!, category:st!, is_adverse_event:bool!, is_reportable:bool!, reportability_due:d, sample_requested:bool!, sample_received_at:ts, owner_id:>iam.app_user!, status:st!, response_sent_at:ts
complaint_sample | complaint_id:>qms.complaint!, received_at:ts!, sample_condition:l, retained_location:s
complaint_code | code:c!*, name:s!, parent_id:>qms.complaint_code
complaint_communication | complaint_id:>qms.complaint!, direction:st!, sent_at:ts!, channel:st!, summary:l!, stored_object_id:>file.stored_object
pv_case_link | complaint_id:>qms.complaint!, safety_case_ref:c!, safety_system:s!, transferred_at:ts!
oos_investigation W S | oos_no:c!*, kind:st!, test_result_id:>lims.test_result!, sample_id:>lims.sample!, specification_limit:l, reported_value:l, phase:st!, phase1_conclusion:st, assignable_cause:bool, retest_allowed:bool, phase2_conclusion:st, batch_impact:t, owner_id:>iam.app_user!, status:st!
oos_hypothesis_test | oos_id:>qms.oos_investigation!, hypothesis:l!, test_performed:l, result:l, supports_cause:bool
oos_retest | oos_id:>qms.oos_investigation!, sample_id:>lims.sample!, test_result_id:>lims.test_result, analyst_id:>iam.app_user!, justification:l!
oot_trend_rule | code:c!*, name:s!, method:st!, parameters:j!, specification_id:>lims.specification
recall W | recall_no:c!*, product_id:>mdm.product!, recall_class:st!, depth:st!, reason:t!, initiated_at:ts!, status:st!, authority_notified_at:ts
recall_lot | recall_id:>qms.recall!, lot_id:>wms.lot!, quantity_distributed:n, quantity_recovered:n
recall_customer_notice | recall_id:>qms.recall!, customer_id:>erp.customer!, notified_at:ts, acknowledged_at:ts
management_review W S | review_no:c!*, period_start:d!, period_end:d!, meeting_date:d!, chair_id:>iam.app_user!, summary:t, status:st!
management_review_input | management_review_id:>qms.management_review!, input_type:st!, metric_snapshot:j, commentary:t
management_review_action | management_review_id:>qms.management_review!, description:l!, owner_id:>iam.app_user!, due_date:d!, capa_id:>qms.capa, status:st!
quality_metric_definition V | code:c!*, name:s!, formula:t!, unit:c, direction:st!, target:n, threshold_amber:n, threshold_red:n, frequency:st!
quality_metric_value | metric_definition_id:>qms.quality_metric_definition!, site_id:>org.site, period_start:d!, period_end:d!, value:n!, numerator:n, denominator:n, computed_at:ts!
product_quality_review W | pqr_no:c!*, product_id:>mdm.product!, period_start:d!, period_end:d!, status:st!, conclusion:t
product_quality_review_section | pqr_id:>qms.product_quality_review!, section_code:c!, content:j, author_id:>iam.app_user
lessons_learned | source_type:st!, source_id:u!, summary:t!, applicability:j

@schema qrm | Quality risk management (ICH Q9)
risk_register S | code:c!*, name:s!, scope:st!, owner_id:>iam.app_user!
risk_matrix V | code:c!*, name:s!, severity_levels:j!, probability_levels:j!, detectability_levels:j, scoring_method:st!, acceptance_bands:j!
risk_assessment W S | ra_no:c!*, title:l!, method:st!, risk_matrix_id:>qrm.risk_matrix!, register_id:>qrm.risk_register, scope:t, status:st!, team:j, next_review_date:d
risk_item | risk_assessment_id:>qrm.risk_assessment!, hazard:l!, cause:l, effect:l, severity:i, probability:i, detectability:i, initial_score:i, initial_level:st, residual_severity:i, residual_probability:i, residual_detectability:i, residual_score:i, residual_level:st, acceptance:st
fmea_worksheet | risk_assessment_id:>qrm.risk_assessment!, fmea_type:st!, item_function:l
fmea_line | worksheet_id:>qrm.fmea_worksheet!, process_step:l!, failure_mode:l!, failure_effect:l!, failure_cause:l!, current_controls:l, sev:i!, occ:i!, det:i!, rpn:i!, recommended_action:l, revised_sev:i, revised_occ:i, revised_det:i, revised_rpn:i
risk_control | risk_item_id:>qrm.risk_item!, control_type:st!, description:l!, owner_id:>iam.app_user, capa_action_id:>qms.capa_action, verification:l
risk_review | risk_assessment_id:>qrm.risk_assessment!, reviewed_at:ts!, reviewer_id:>iam.app_user!, outcome:st!, comment:l
risk_link | risk_item_id:>qrm.risk_item!, linked_type:st!, linked_id:u!
hazard_library | code:c!*, hazard:l!, category:st!, default_severity:i
risk_communication | risk_assessment_id:>qrm.risk_assessment!, audience:l!, communicated_at:ts!, summary:l

@schema sqm | Supplier quality
supplier_site | supplier_id:>erp.supplier!, name:s!, address:j, country_id:>org.country, gmp_certificate_no:c, gmp_certificate_expiry:d
supplier_qualification W | supplier_id:>erp.supplier!, material_id:>mdm.material, site_id:>org.site, status:st!, qualification_type:st!, risk_level:st!, approved_at:ts, expires_at:d
approved_supplier_list | supplier_id:>erp.supplier!, manufacturer_site_id:>sqm.supplier_site, material_id:>mdm.material!, status:st!, valid_until:d, conditions:l
supplier_questionnaire V | code:c!*, name:s!, questions:j!
supplier_questionnaire_response | questionnaire_id:>sqm.supplier_questionnaire!, supplier_id:>erp.supplier!, responses:j!, submitted_at:ts, score:n
quality_agreement W | supplier_id:>erp.supplier!, agreement_no:c!*, document_id:>dms.document, effective_date:d, expiry_date:d, status:st!
supplier_scorecard | supplier_id:>erp.supplier!, period_start:d!, period_end:d!, quality_score:n, delivery_score:n, responsiveness_score:n, overall_score:n, tier:st
scar W | scar_no:c!*, supplier_id:>erp.supplier!, source_type:st!, source_id:u, description:t!, response_due:d!, supplier_response:t, status:st!
supplier_change_notification W | supplier_id:>erp.supplier!, received_at:ts!, description:t!, change_control_id:>qms.change_control, assessment:st, status:st!
supplier_certificate | supplier_id:>erp.supplier!, certificate_type:st!, number:c, issued_by:s, valid_from:d, valid_to:d, stored_object_id:>file.stored_object
supplier_material_risk | supplier_id:>erp.supplier!, material_id:>mdm.material!, risk_assessment_id:>qrm.risk_assessment, risk_level:st!, testing_regime:st!
supplier_audit_schedule | supplier_id:>erp.supplier!, planned_date:d!, audit_id:>aud.audit, frequency_months:i

@schema aud | Audit & regulatory inspection management
audit_program | code:c!*, name:s!, program_year:i!, scope:st!, status:st!
audit W S | audit_no:c!*, program_id:>aud.audit_program, audit_type:st!, auditee_type:st!, auditee_ref:u, lead_auditor_id:>iam.app_user!, planned_start:d!, planned_end:d!, actual_start:d, actual_end:d, scope:t, standard_refs:j, status:st!
audit_team_member | audit_id:>aud.audit!, user_id:>iam.app_user, external_name:s, team_role:st!
audit_checklist_template V | code:c!*, name:s!, items:j!
audit_checklist_response | audit_id:>aud.audit!, template_id:>aud.audit_checklist_template, item_code:c!, response:st, notes:l, evidence_object_id:>file.stored_object
audit_finding | audit_id:>aud.audit!, finding_no:c!, classification:st!, requirement_ref:l, description:t!, evidence:t, capa_id:>qms.capa, response_due:d, status:st!
audit_report | audit_id:>aud.audit!, stored_object_id:>file.stored_object, issued_at:ts, signature_id:>esig.signature
auditor_qualification | user_id:>iam.app_user!, auditor_level:st!, qualified_at:d!, expires_at:d, scope:l
inspection W S | inspection_no:c!*, authority:s!, inspection_type:st!, announced:bool!, notified_at:ts, start_date:d!, end_date:d, lead_inspector:s, outcome:st, status:st!
inspection_request | inspection_id:>aud.inspection!, requested_at:ts!, request_text:l!, requested_by:s, assigned_to:>iam.app_user, provided_at:ts, status:st!
inspection_document_provided | request_id:>aud.inspection_request!, document_version_id:>dms.document_version, stored_object_id:>file.stored_object, provided_at:ts!
inspection_observation | inspection_id:>aud.inspection!, observation_no:c!, classification:st!, observation_text:t!, response_due:d, response_text:t, capa_id:>qms.capa, status:st!
inspection_commitment | observation_id:>aud.inspection_observation!, commitment:l!, due_date:d!, regulatory_commitment_id:>rim.regulatory_commitment, status:st!
inspection_readiness_item | inspection_id:>aud.inspection, area:s!, item:l!, owner_id:>iam.app_user, status:st!

@schema trn | Training management
competency | code:c!*, name:s!, description:l
job_role | code:c!*, name:s!, department_id:>org.department
job_role_competency | job_role_id:>trn.job_role!, competency_id:>trn.competency!, required_level:i
user_job_role | user_id:>iam.app_user!, job_role_id:>trn.job_role!, assigned_from:d!, assigned_to:d
course V | code:c!*, title:l!, course_type:st!, document_id:>dms.document, duration_minutes:i, has_assessment:bool!, passing_score:n, retraining_months:i, status:st!
course_material | course_id:>trn.course!, stored_object_id:>file.stored_object!, material_type:st!
curriculum V | code:c!*, name:s!, status:st!
curriculum_item | curriculum_id:>trn.curriculum!, course_id:>trn.course!, is_mandatory:bool!, due_within_days:i!, sort_order:i
job_role_curriculum | job_role_id:>trn.job_role!, curriculum_id:>trn.curriculum! | U(job_role_id,curriculum_id)
training_assignment | user_id:>iam.app_user!, course_id:>trn.course!, course_version:c!, source_type:st!, source_id:u, assigned_at:ts!, due_date:d!, status:st!, completed_at:ts, signature_id:>esig.signature
training_session S | course_id:>trn.course!, trainer_id:>iam.app_user, start_at:ts!, end_at:ts!, location:s, capacity:i, status:st!
training_session_attendance | session_id:>trn.training_session!, user_id:>iam.app_user!, attended:bool!, trainer_signature_id:>esig.signature
assessment V | course_id:>trn.course!, code:c!*, name:s!, question_pool_size:i, questions_per_attempt:i
assessment_question | assessment_id:>trn.assessment!, question_type:st!, question_text:t!, options:j, correct_answer:j!, points:n!
assessment_attempt | assessment_id:>trn.assessment!, user_id:>iam.app_user!, started_at:ts!, submitted_at:ts, score:n, passed:bool, answers:j
ojt_record | user_id:>iam.app_user!, course_id:>trn.course!, trainer_id:>iam.app_user!, observations:t, performed_at:ts!, outcome:st!, trainer_signature_id:>esig.signature
trainer_qualification | user_id:>iam.app_user!, course_id:>trn.course!, qualified_at:d!, expires_at:d
external_training_record | user_id:>iam.app_user!, title:l!, provider:s, completed_on:d!, stored_object_id:>file.stored_object
training_matrix_snapshot N | job_role_id:>trn.job_role, snapshot_at:ts!, data:j!
training_waiver | assignment_id:>trn.training_assignment!, reason:l!, approved_signature_id:>esig.signature

@schema val | Validation lifecycle (CSV/CSA, equipment, process)
system_inventory S | code:c!*, name:s!, vendor:s, version:c, gamp_category:st!, gxp_relevant:bool!, part11_relevant:bool!, data_criticality:st, system_owner_id:>iam.app_user!, process_owner_id:>iam.app_user, validation_status:st!, hosting:st, next_periodic_review:d
validation_project W S | project_no:c!*, title:l!, scope_type:st!, system_id:>val.system_inventory, equipment_id:>eqp.equipment, change_control_id:>qms.change_control, approach:st, status:st!
validation_master_plan W S | vmp_no:c!*, document_id:>dms.document, period_start:d, period_end:d, status:st!
validation_deliverable | project_id:>val.validation_project!, deliverable_type:st!, document_id:>dms.document, is_required:bool!, status:st!
requirement V | project_id:>val.validation_project!, req_no:c!, req_type:st!, req_text:t!, priority:st, gxp_impact:st, risk_level:st, parent_requirement_id:>val.requirement, source:l, status:st! | U(project_id,req_no)
requirement_risk | requirement_id:>val.requirement!, risk_item_id:>qrm.risk_item!
test_protocol V W | project_id:>val.validation_project!, protocol_no:c!*, protocol_type:st!, title:l!, objective:t, prerequisites:t, acceptance_criteria:t, document_id:>dms.document, status:st!
test_script | protocol_id:>val.test_protocol!, script_no:c!, title:l!, objective:l, setup:t, sort_order:i!
test_step | script_id:>val.test_script!, step_no:i!, instruction:t!, expected_result:t!, requires_evidence:bool!, requirement_refs:j
test_execution W | protocol_id:>val.test_protocol!, run_no:i!, executor_id:>iam.app_user!, environment:c!, started_at:ts!, completed_at:ts, result:st, status:st!
test_step_execution | execution_id:>val.test_execution!, step_id:>val.test_step!, actual_result:t, result:st!, executed_by:>iam.app_user!, executed_at:ts!, evidence_object_id:>file.stored_object, witness_signature_id:>esig.signature
test_incident W | execution_id:>val.test_execution!, step_execution_id:>val.test_step_execution, incident_no:c!*, category:st!, description:t!, resolution:t, retest_required:bool, status:st!
trace_link | requirement_id:>val.requirement!, target_type:st!, target_id:u!, link_type:st!
validation_summary_report W | project_id:>val.validation_project!, conclusion:t, deviations_summary:t, document_id:>dms.document, status:st!
system_periodic_review W | system_id:>val.system_inventory!, due_date:d!, performed_at:ts, outcome:st, findings:t, next_due_date:d, status:st!
equipment_qualification_status | equipment_id:>eqp.equipment!, stage:st!, status:st!, valid_until:d, project_id:>val.validation_project
csv_supplier_assessment | system_id:>val.system_inventory!, supplier_id:>erp.supplier, assessment_type:st!, outcome:st, performed_on:d
platform_release_package | platform_release:c!*, release_notes_object_id:>file.stored_object, regression_result:j, vendor_signoff_at:ts, customer_acceptance_status:st
automated_test_evidence | execution_id:>val.test_execution, test_case_key:s!, framework:c!, result:st!, log_object_id:>file.stored_object, executed_at:ts!, commit_sha:c
data_integrity_assessment | system_id:>val.system_inventory!, assessed_on:d!, alcoa_scores:j!, gaps:t, remediation_capa_id:>qms.capa

@schema mdm | Master data
uom | code:c!*, name:s!, dimension:st!
uom_conversion | from_uom_id:>mdm.uom!, to_uom_id:>mdm.uom!, factor:n!, material_id:>mdm.material
material_type | code:c!*, name:s!
storage_condition | code:c!*, name:s!, temp_min:n, temp_max:n, humidity_max:n, light_protect:bool!
material V | material_no:c!*, name:l!, material_type_id:>mdm.material_type!, base_uom_id:>mdm.uom!, is_controlled_substance:bool!, controlled_schedule:c, storage_condition_id:>mdm.storage_condition, shelf_life_days:i, retest_interval_days:i, requires_coa:bool!, sampling_plan_id:>lims.sampling_plan, cas_number:c, status:st!
material_site | material_id:>mdm.material!, site_id:>org.site!, status:st!, inspection_type:st, procurement_type:st | U(material_id,site_id)
material_specification_link | material_id:>mdm.material!, specification_id:>lims.specification!, usage:st!, valid_from:d!
dosage_form | code:c!*, name:s!, route:c
product | product_code:c!*, name:l!, product_type:st!, industry_segment:st!, dosage_form_id:>mdm.dosage_form, therapeutic_area:c, is_sterile:bool!, is_biologic:bool!, status:st!
product_strength | product_id:>mdm.product!, strength:c!, uom_id:>mdm.uom
sku | sku_code:c!*, product_id:>mdm.product!, material_id:>mdm.material!, pack_size:c, market_country_id:>org.country, gtin:c, status:st!
bill_of_material V | material_id:>mdm.material!, bom_no:c!*, base_quantity:n!, base_uom_id:>mdm.uom!, usage:st!, status:st!
bom_item | bom_id:>mdm.bill_of_material!, line_no:i!, component_material_id:>mdm.material!, quantity:n!, uom_id:>mdm.uom!, is_active_ingredient:bool!, overage_pct:n, scrap_pct:n, phase:c
reason_code | code:c!*, category:st!, description:s!
hazard_classification | material_id:>mdm.material!, ghs_class:c!, sds_document_id:>dms.document
packaging_component | material_id:>mdm.material!, component_type:st!, artwork_id:>rim.artwork, dimensions:j
controlled_substance_license | material_id:>mdm.material!, authority:s!, license_no:c!, quota:n, valid_to:d
data_steward_request W | entity_type:st!, entity_id:u, request_type:st!, payload:j!, status:st!
market | code:c!*, name:s!, country_id:>org.country!
currency | code:c!*, name:s!, decimals:i!
exchange_rate | from_currency_id:>mdm.currency!, to_currency_id:>mdm.currency!, rate:n!, valid_on:d!

@schema erp | Pharmaceutical ERP (procure-to-pay, plan, order-to-cash handoff)
supplier | supplier_no:c!*, name:l!, country_id:>org.country, tax_id:c, status:st!, payment_terms:c, currency_id:>mdm.currency, is_manufacturer:bool!, is_distributor:bool!
supplier_contact | supplier_id:>erp.supplier!, name:s!, email:s, phone:c, contact_role:c
customer | customer_no:c!*, name:l!, country_id:>org.country, customer_type:st!, license_no:c, license_expiry:d, status:st!
customer_site | customer_id:>erp.customer!, name:s!, address:j, ship_to:bool!, gdp_qualified:bool!
purchase_requisition W S | pr_no:c!*, requested_by:>iam.app_user!, status:st!, need_by:d
purchase_requisition_line | pr_id:>erp.purchase_requisition!, line_no:i!, material_id:>mdm.material!, quantity:n!, uom_id:>mdm.uom!
purchase_order W S | po_no:c!*, supplier_id:>erp.supplier!, order_date:d!, currency_id:>mdm.currency, status:st!, total_amount:m
purchase_order_line | po_id:>erp.purchase_order!, line_no:i!, material_id:>mdm.material!, quantity:n!, uom_id:>mdm.uom!, unit_price:m, delivery_date:d, manufacturer_site_id:>sqm.supplier_site, asl_check_status:st
sales_order S | so_no:c!*, customer_id:>erp.customer!, order_date:d!, status:st!
sales_order_line | so_id:>erp.sales_order!, line_no:i!, sku_id:>mdm.sku!, quantity:n!, uom_id:>mdm.uom!, requested_date:d
demand_forecast N | sku_id:>mdm.sku!, site_id:>org.site, period_start:d!, quantity:n!, forecast_version:c!
master_production_schedule S | mps_no:c!*, period_start:d!, period_end:d!, status:st!
mps_line | mps_id:>erp.master_production_schedule!, material_id:>mdm.material!, planned_quantity:n!, week_start:d!
mrp_run N S | run_at:ts!, horizon_days:i!, parameters:j, status:st!
planned_order N | mrp_run_id:>erp.mrp_run, material_id:>mdm.material!, quantity:n!, due_date:d!, order_type:st!, converted_ref:u
production_order W S | order_no:c!*, material_id:>mdm.material!, master_recipe_id:>mes.master_recipe!, master_recipe_version:c!, planned_quantity:n!, uom_id:>mdm.uom!, planned_start:ts, planned_end:ts, status:st!
production_order_component | production_order_id:>erp.production_order!, material_id:>mdm.material!, required_quantity:n!, uom_id:>mdm.uom!, reserved_quantity:n
cost_record | production_order_id:>erp.production_order, cost_center_id:>org.cost_center, cost_element:c!, amount:m!, currency_id:>mdm.currency!, posted_on:d!
finance_posting_outbox N | source_type:st!, source_id:u!, payload:j!, status:st!, posted_at:ts
price_list | code:c!*, currency_id:>mdm.currency!, valid_from:d!
price_list_item | price_list_id:>erp.price_list!, sku_id:>mdm.sku!, price:m!
delivery | delivery_no:c!*, sales_order_id:>erp.sales_order!, ship_date:d, carrier:s, status:st!
delivery_line | delivery_id:>erp.delivery!, so_line_id:>erp.sales_order_line!, lot_id:>wms.lot!, quantity:n!
return_authorization W | rma_no:c!*, customer_id:>erp.customer!, reason:l!, complaint_id:>qms.complaint, status:st!
return_line | rma_id:>erp.return_authorization!, sku_id:>mdm.sku!, lot_id:>wms.lot, quantity:n!, disposition:st

@schema mes | Manufacturing execution (ISA-88 recipes, EBR)
master_recipe V W | recipe_no:c!*, material_id:>mdm.material!, name:l!, recipe_type:st!, batch_size:n!, uom_id:>mdm.uom!, bom_id:>mdm.bill_of_material, theoretical_yield:n, yield_lower_limit:n, yield_upper_limit:n, status:st!, effective_from:d
master_formula | recipe_id:>mes.master_recipe!, document_id:>dms.document, formula_json:j!
recipe_procedure | recipe_id:>mes.master_recipe!, code:c!, name:s!, sort_order:i!
recipe_unit_procedure | procedure_id:>mes.recipe_procedure!, code:c!, name:s!, equipment_class_id:>eqp.equipment_class, sort_order:i!
recipe_operation | unit_procedure_id:>mes.recipe_unit_procedure!, code:c!, name:s!, room_class:c, sort_order:i!
recipe_phase | operation_id:>mes.recipe_operation!, code:c!, name:s!, phase_type:st!, instruction:t, config:j!, is_critical:bool!, requires_verification:bool!, sort_order:i!
recipe_parameter | phase_id:>mes.recipe_phase!, code:c!, name:s!, data_type:st!, target:n, lower_limit:n, upper_limit:n, uom_id:>mdm.uom, is_cpp:bool!, alarm_on_violation:bool!
recipe_material_requirement | phase_id:>mes.recipe_phase!, material_id:>mdm.material!, quantity:n!, uom_id:>mdm.uom!, tolerance_pct:n, is_scaled:bool!, potency_adjusted:bool!
recipe_ipc_definition | phase_id:>mes.recipe_phase!, test_code:c!, frequency:c!, sample_size:i, lower_limit:n, upper_limit:n, uom_id:>mdm.uom
batch W S | batch_no:c!*, production_order_id:>erp.production_order!, master_recipe_id:>mes.master_recipe!, master_recipe_version:c!, material_id:>mdm.material!, planned_quantity:n!, actual_quantity:n, uom_id:>mdm.uom!, manufacture_date:d, expiry_date:d, status:st!, release_status:st!, started_at:ts, completed_at:ts
batch_record | batch_id:>mes.batch!, record_type:st!, status:st!, review_status:st!, generated_object_id:>file.stored_object, content_hash:h
batch_step_execution | batch_record_id:>mes.batch_record!, phase_id:>mes.recipe_phase!, sequence_no:i!, status:st!, started_at:ts, completed_at:ts, performed_by:>iam.app_user, verified_by:>iam.app_user, room_id:>org.room, equipment_id:>eqp.equipment, data:j, has_exception:bool!
batch_parameter_value | step_execution_id:>mes.batch_step_execution!, parameter_id:>mes.recipe_parameter!, value_numeric:n, value_text:l, recorded_at:ts!, recorded_by:>iam.app_user!, source:st!, in_limits:bool!
batch_exception | batch_record_id:>mes.batch_record!, step_execution_id:>mes.batch_step_execution, exception_type:st!, description:t!, severity:st!, deviation_id:>qms.deviation, reviewed_by:>iam.app_user, reviewed_at:ts, status:st!
batch_comment | batch_record_id:>mes.batch_record!, step_execution_id:>mes.batch_step_execution, author_id:>iam.app_user!, body:t!
dispensing_order S | dispense_no:c!*, batch_id:>mes.batch!, booth_room_id:>org.room, status:st!
dispensing_line | dispensing_order_id:>mes.dispensing_order!, material_id:>mdm.material!, required_quantity:n!, uom_id:>mdm.uom!, potency_factor:n, status:st!
weighing_record | dispensing_line_id:>mes.dispensing_line!, source_lot_id:>wms.lot!, container_id:>wms.container, balance_id:>eqp.equipment!, tare:n!, gross:n!, net:n!, uom_id:>mdm.uom!, in_tolerance:bool!, weighed_by:>iam.app_user!, verified_by:>iam.app_user, weighed_at:ts!, label_printed:bool!, balance_check_id:>eqp.balance_daily_check
material_charge | step_execution_id:>mes.batch_step_execution!, weighing_record_id:>mes.weighing_record, lot_id:>wms.lot!, quantity:n!, uom_id:>mdm.uom!, charged_by:>iam.app_user!, verified_by:>iam.app_user, charged_at:ts!
checklist_template V | code:c!*, name:s!, items:j!
line_clearance W | batch_id:>mes.batch, room_id:>org.room!, equipment_id:>eqp.equipment, clearance_type:st!, checklist_template_id:>mes.checklist_template, previous_batch_no:c, performed_by:>iam.app_user!, verified_by:>iam.app_user, performed_at:ts!, result:st!
checklist_response | line_clearance_id:>mes.line_clearance!, item_code:c!, response:st!, comment:l
ipc_result | batch_id:>mes.batch!, ipc_definition_id:>mes.recipe_ipc_definition!, step_execution_id:>mes.batch_step_execution, sample_time:ts!, value:n, result_text:l, in_spec:bool!, performed_by:>iam.app_user!, instrument_id:>eqp.equipment
equipment_usage | equipment_id:>eqp.equipment!, batch_id:>mes.batch, activity:st!, start_at:ts!, end_at:ts, performed_by:>iam.app_user!, status_before:st, status_after:st
yield_reconciliation | batch_id:>mes.batch!, stage:c!, theoretical_quantity:n!, actual_quantity:n!, samples_quantity:n, waste_quantity:n, yield_pct:n!, in_limits:bool!, justification:l, verified_by:>iam.app_user
label_reconciliation | batch_id:>mes.batch!, label_material_id:>mdm.material!, issued:i!, used:i!, destroyed:i!, returned:i!, variance:i!, in_limits:bool!
batch_review W | batch_id:>mes.batch!, review_type:st!, reviewer_id:>iam.app_user!, review_by_exception:bool!, outcome:st, completed_at:ts, signature_id:>esig.signature
batch_release W | batch_id:>mes.batch!, release_type:st!, market_id:>mdm.market, decision:st!, decided_by:>iam.app_user, decided_at:ts, signature_id:>esig.signature, conditions:l, checklist:j!
batch_release_check | batch_release_id:>mes.batch_release!, check_code:c!, check_name:s!, result:st!, evidence_ref:j, auto_evaluated:bool!
work_center S | code:c!*, name:s!, room_id:>org.room
production_schedule_slot | work_center_id:>mes.work_center!, batch_id:>mes.batch, start_at:ts!, end_at:ts!, status:st!
process_data_tag | equipment_id:>eqp.equipment!, tag_name:s!, opcua_node_id:l, data_type:st!, uom_id:>mdm.uom, recipe_parameter_code:c
process_data_point N | tag_id:>mes.process_data_tag!, sample_ts:ts!, value:n, quality_code:c
batch_sample | batch_id:>mes.batch!, lims_sample_id:>lims.sample, step_execution_id:>mes.batch_step_execution, sample_point:c
batch_document_link | batch_id:>mes.batch!, document_version_id:>dms.document_version!

@schema wms | Warehouse management & genealogy
warehouse S | code:c!*, name:s!, warehouse_type:st!, temperature_zone:c
storage_zone | warehouse_id:>wms.warehouse!, code:c!, zone_type:st!, storage_condition_id:>mdm.storage_condition
storage_location | zone_id:>wms.storage_zone!, code:c!, barcode:c!*, capacity:n, is_blocked:bool!
lot | lot_no:c!*, material_id:>mdm.material!, supplier_lot_no:c, manufacturer_lot_no:c, supplier_id:>erp.supplier, manufacturer_site_id:>sqm.supplier_site, batch_id:>mes.batch, manufacture_date:d, expiry_date:d, retest_date:d, quality_status:st!, potency:n, status:st!
lot_status_history | lot_id:>wms.lot!, from_status:st, to_status:st!, changed_at:ts!, changed_by:>iam.app_user!, reason:l!, signature_id:>esig.signature
container | container_no:c!*, lot_id:>wms.lot!, container_type:st, quantity:n!, uom_id:>mdm.uom!, location_id:>wms.storage_location, status:st!, is_sampled:bool!, tare:n, label_barcode:c
handling_unit | hu_no:c!*, hu_type:st!, parent_hu_id:>wms.handling_unit, location_id:>wms.storage_location, sscc:c
handling_unit_content | hu_id:>wms.handling_unit!, container_id:>wms.container, lot_id:>wms.lot, quantity:n!
inventory_balance | material_id:>mdm.material!, lot_id:>wms.lot!, location_id:>wms.storage_location!, quantity:n!, uom_id:>mdm.uom!, quality_status:st!, reserved_quantity:n! | U(lot_id,location_id)
inventory_transaction | txn_no:c!*, txn_type:st!, material_id:>mdm.material!, lot_id:>wms.lot, container_id:>wms.container, from_location_id:>wms.storage_location, to_location_id:>wms.storage_location, quantity:n!, uom_id:>mdm.uom!, reference_type:st, reference_id:u, reason_code_id:>mdm.reason_code, performed_by:>iam.app_user!, performed_at:ts!
goods_receipt W S | gr_no:c!*, po_id:>erp.purchase_order, supplier_id:>erp.supplier!, received_at:ts!, delivery_note:c, temperature_ok:bool, seal_intact:bool, status:st!
goods_receipt_line | gr_id:>wms.goods_receipt!, po_line_id:>erp.purchase_order_line, material_id:>mdm.material!, lot_id:>wms.lot!, quantity:n!, uom_id:>mdm.uom!, containers:i!, asl_verified:bool!, coa_received:bool!
reservation | material_id:>mdm.material!, lot_id:>wms.lot, quantity:n!, reference_type:st!, reference_id:u!, status:st!
picking_rule | code:c!*, strategy:st!, material_type_id:>mdm.material_type, min_remaining_shelf_life_days:i
pick_list S | pick_no:c!*, reference_type:st!, reference_id:u!, strategy:st!, status:st!
pick_list_line | pick_list_id:>wms.pick_list!, material_id:>mdm.material!, lot_id:>wms.lot, container_id:>wms.container, location_id:>wms.storage_location, quantity:n!, picked_quantity:n, picked_by:>iam.app_user, picked_at:ts, override_reason:l
transfer_order S | to_no:c!*, from_warehouse_id:>wms.warehouse!, to_warehouse_id:>wms.warehouse!, status:st!
transfer_order_line | transfer_order_id:>wms.transfer_order!, lot_id:>wms.lot!, quantity:n!, uom_id:>mdm.uom!
cycle_count_plan S | code:c!*, name:s!, method:st!, frequency:c!
cycle_count W | count_no:c!*, plan_id:>wms.cycle_count_plan, warehouse_id:>wms.warehouse!, scheduled_date:d!, blind_count:bool!, status:st!
cycle_count_line | cycle_count_id:>wms.cycle_count!, location_id:>wms.storage_location!, lot_id:>wms.lot, system_quantity:n!, counted_quantity:n, variance:n, recount_required:bool, counted_by:>iam.app_user, adjustment_txn_id:>wms.inventory_transaction
stock_reconciliation W S | recon_no:c!*, period_end:d!, scope:st!, status:st!, variance_value:m
stock_reconciliation_line | recon_id:>wms.stock_reconciliation!, material_id:>mdm.material!, lot_id:>wms.lot, book_quantity:n!, physical_quantity:n!, variance:n!, explanation:l
genealogy_link | parent_lot_id:>wms.lot!, child_lot_id:>wms.lot!, batch_id:>mes.batch, quantity:n, relation:st!
label_template V | code:c!*, name:s!, label_type:st!, format:st!, template:t!, barcode_symbology:st
label_print_job | template_id:>wms.label_template!, printer_code:c!, reference_type:st!, reference_id:u!, copies:i!, printed_by:>iam.app_user!, printed_at:ts!, reprint_reason:l
serial_number | sku_id:>mdm.sku!, lot_id:>wms.lot!, serial_no:c!, status:st!, aggregated_hu_id:>wms.handling_unit | U(sku_id,serial_no)
temperature_excursion W | location_id:>wms.storage_location, lot_id:>wms.lot, shipment_ref:c, min_recorded:n, max_recorded:n, duration:dur, detected_at:ts!, assessment:st, deviation_id:>qms.deviation
material_return_to_stock | batch_id:>mes.batch!, lot_id:>wms.lot!, quantity:n!, verified_by:>iam.app_user, status:st!
destruction_record W | lot_id:>wms.lot!, quantity:n!, method:s!, witnessed_by:>iam.app_user, destroyed_at:ts, certificate_object_id:>file.stored_object

@schema lims | Laboratory information management
test_method V W | method_no:c!*, name:l!, technique:st!, document_id:>dms.document, validation_status:st!, status:st!
test_definition | method_id:>lims.test_method!, code:c!*, name:s!, result_type:st!, uom_id:>mdm.uom, decimals:i, replicates:i!
specification V W | spec_no:c!*, name:l!, spec_type:st!, material_id:>mdm.material, product_id:>mdm.product, market_id:>mdm.market, pharmacopoeia:c, status:st!
specification_test | specification_id:>lims.specification!, test_definition_id:>lims.test_definition!, sequence:i!, lower_limit:n, upper_limit:n, target:n, text_criterion:l, limit_type:st!, alert_limit_low:n, alert_limit_high:n, is_reported_on_coa:bool!, reporting_rule:c
sampling_plan V | code:c!*, name:s!, method:st!, parameters:j!
sample_type | code:c!*, name:s!
sample W S | sample_no:c!*, sample_type_id:>lims.sample_type!, source_type:st!, material_id:>mdm.material, lot_id:>wms.lot, batch_id:>mes.batch, container_id:>wms.container, stability_pull_id:>lims.stability_pull, em_sample_point_id:>lims.em_sample_point, specification_id:>lims.specification, collected_at:ts, collected_by:>iam.app_user, received_at:ts, quantity:n, uom_id:>mdm.uom, storage_location:s, status:st!, priority:st
sample_aliquot | sample_id:>lims.sample!, aliquot_no:c!, quantity:n, location:s
sample_custody_event | sample_id:>lims.sample!, event_type:st!, from_party:s, to_party:s, occurred_at:ts!, by_user_id:>iam.app_user!, sample_condition:l
test_assignment | sample_id:>lims.sample!, specification_test_id:>lims.specification_test, test_definition_id:>lims.test_definition!, analyst_id:>iam.app_user, instrument_id:>eqp.equipment, due_at:ts, status:st!
worksheet | worksheet_no:c!*, analyst_id:>iam.app_user!, method_id:>lims.test_method, created_on:d!, status:st!
worksheet_item | worksheet_id:>lims.worksheet!, test_assignment_id:>lims.test_assignment!, slot_no:i!
instrument_run | instrument_id:>eqp.equipment!, run_id_external:c!, cds_system:s, acquired_at:ts!, raw_data_object_id:>file.stored_object, audit_trail_reviewed:bool!, reviewed_by:>iam.app_user
test_result | test_assignment_id:>lims.test_assignment!, replicate_no:i!, raw_value:l, value_numeric:n, value_text:l, reported_value:l, uom_id:>mdm.uom, in_spec:bool, oot_flag:bool!, entered_by:>iam.app_user!, entered_at:ts!, source:st!, instrument_run_id:>lims.instrument_run, reviewed_by:>iam.app_user, reviewed_at:ts, status:st!
result_calculation | test_result_id:>lims.test_result!, formula:t!, inputs:j!, output:n!
reagent | reagent_no:c!*, name:s!, lot_no:c, prepared_by:>iam.app_user, prepared_at:ts, expiry_at:ts, status:st!
reference_standard | std_no:c!*, name:s!, source:st!, lot_no:c, potency:n, expiry_date:d, parent_standard_id:>lims.reference_standard, status:st!
reagent_usage | test_assignment_id:>lims.test_assignment!, reagent_id:>lims.reagent, reference_standard_id:>lims.reference_standard
stability_study W S | study_no:c!*, product_id:>mdm.product!, batch_id:>mes.batch, lot_id:>wms.lot, protocol_document_id:>dms.document, study_type:st!, start_date:d!, status:st!
stability_condition | study_id:>lims.stability_study!, condition_code:c!, temperature:n!, humidity:n, chamber_id:>eqp.equipment
stability_timepoint | condition_id:>lims.stability_condition!, interval_months:n!, scheduled_date:d!, window_days:i!
stability_pull | timepoint_id:>lims.stability_timepoint!, pulled_at:ts, pulled_by:>iam.app_user, quantity:n, status:st!
stability_trend | study_id:>lims.stability_study!, test_definition_id:>lims.test_definition!, model:st!, slope:n, intercept:n, predicted_shelf_life_months:n, computed_at:ts!
em_sample_point S | code:c!*, room_id:>org.room!, sample_type:st!, grade:c, alert_limit:n, action_limit:n, frequency:c!, status:st!
em_schedule | sample_point_id:>lims.em_sample_point!, scheduled_at:ts!, sample_id:>lims.sample, status:st!
em_excursion W | sample_id:>lims.sample!, excursion_level:st!, value:n!, deviation_id:>qms.deviation, status:st!
micro_identification | sample_id:>lims.sample!, colony_count:i, organism:s, identification_method:c, is_objectionable:bool
coa W | coa_no:c!*, lot_id:>wms.lot!, specification_id:>lims.specification!, customer_id:>erp.customer, issued_at:ts, signature_id:>esig.signature, stored_object_id:>file.stored_object, status:st!
coa_line | coa_id:>lims.coa!, specification_test_id:>lims.specification_test!, reported_result:l!, conforms:bool!
supplier_coa_verification | lot_id:>wms.lot!, stored_object_id:>file.stored_object, verified_by:>iam.app_user!, discrepancies:l, verified_at:ts!
retain_sample | lot_id:>wms.lot!, quantity:n!, location:s!, retain_until:d!, status:st!

@schema eqp | Equipment, calibration, maintenance, utilities
equipment_class | code:c!*, name:s!, isa88_level:st
equipment S | equipment_no:c!*, name:l!, equipment_class_id:>eqp.equipment_class!, room_id:>org.room, manufacturer:s, model:s, serial_no:c, gxp_critical:bool!, status:st!, qualification_status:st!, calibration_due:d, pm_due:d, cleaning_hold_until:ts
equipment_component | equipment_id:>eqp.equipment!, name:s!, part_no:c, is_product_contact:bool!
equipment_status_history | equipment_id:>eqp.equipment!, from_status:st, to_status:st!, changed_at:ts!, changed_by:>iam.app_user!, reason:l
calibration_plan V | equipment_id:>eqp.equipment!, frequency_days:i!, procedure_document_id:>dms.document, tolerance:j
calibration_record W | equipment_id:>eqp.equipment!, plan_id:>eqp.calibration_plan, performed_at:ts!, performed_by:>iam.app_user, vendor:s, as_found:j, as_left:j, result:st!, certificate_object_id:>file.stored_object, next_due:d!
balance_daily_check | equipment_id:>eqp.equipment!, checked_at:ts!, weights_used:j!, results:j!, passed:bool!, checked_by:>iam.app_user!
maintenance_plan | equipment_id:>eqp.equipment!, plan_type:st!, frequency_days:i!, tasks:j!
work_order W S | wo_no:c!*, equipment_id:>eqp.equipment!, wo_type:st!, description:t!, scheduled_date:d, completed_at:ts, impact_on_qualification:st, change_control_id:>qms.change_control, status:st!
work_order_task | work_order_id:>eqp.work_order!, task_no:i!, description:l!, done:bool!, done_by:>iam.app_user
spare_part | part_no:c!*, name:s!, material_id:>mdm.material
cleaning_procedure V | code:c!*, equipment_class_id:>eqp.equipment_class, document_id:>dms.document, dirty_hold_hours:i, clean_hold_hours:i
cleaning_record | equipment_id:>eqp.equipment!, procedure_id:>eqp.cleaning_procedure!, cleaning_type:st!, started_at:ts!, completed_at:ts, performed_by:>iam.app_user!, verified_by:>iam.app_user, previous_product_id:>mdm.product, swab_sample_id:>lims.sample, result:st
instrument_interface | equipment_id:>eqp.equipment!, interface_type:st!, connection:j, firmware_version:c
utility_system S | code:c!*, name:s!, utility_type:st!
alarm_event | equipment_id:>eqp.equipment, utility_system_id:>eqp.utility_system, alarm_code:c!, severity:st!, raised_at:ts!, acknowledged_by:>iam.app_user, acknowledged_at:ts, cleared_at:ts

@schema rim | Regulatory information management
health_authority | code:c!*, name:s!, country_id:>org.country, region_scope:c
product_family | code:c!*, name:s!, product_id:>mdm.product!
marketing_authorization W | ma_no:c!*, product_id:>mdm.product!, country_id:>org.country!, health_authority_id:>rim.health_authority!, holder_legal_entity_id:>org.legal_entity!, procedure_type:st!, approval_date:d, expiry_date:d, renewal_due:d, status:st!
registered_detail | ma_id:>rim.marketing_authorization!, detail_type:st!, value:t!, effective_from:d!
registered_site | ma_id:>rim.marketing_authorization!, site_id:>org.site, supplier_site_id:>sqm.supplier_site, site_role:st!
submission W | submission_no:c!*, ma_id:>rim.marketing_authorization, health_authority_id:>rim.health_authority!, submission_type:st!, sequence_no:c, format:st, planned_date:d, submitted_date:d, ack_date:d, approval_date:d, status:st!
submission_document | submission_id:>rim.submission!, ctd_section:c!, document_version_id:>dms.document_version, stored_object_id:>file.stored_object
authority_question | submission_id:>rim.submission!, received_at:ts!, question:t!, response_due:d!, response:t, responded_at:ts, status:st!
variation W | variation_no:c!*, ma_id:>rim.marketing_authorization!, change_control_id:>qms.change_control, category:c!, description:t!, submission_id:>rim.submission, implementation_deadline:d, status:st!
regulatory_commitment W | commitment_no:c!*, source_type:st!, source_id:u, ma_id:>rim.marketing_authorization, description:t!, due_date:d!, owner_id:>iam.app_user!, status:st!
artwork V W | artwork_no:c!*, sku_id:>mdm.sku, component_type:st!, country_id:>org.country, stored_object_id:>file.stored_object, status:st!
artwork_text_element | artwork_id:>rim.artwork!, element_code:c!, element_text:t!, locale:c!, source_ref:l
artwork_proof_review | artwork_id:>rim.artwork!, reviewer_id:>iam.app_user!, review_type:st!, outcome:st, comments:t, signature_id:>esig.signature
label_content V W | label_no:c!*, product_id:>mdm.product!, country_id:>org.country!, label_type:st!, document_id:>dms.document, status:st!
label_content_change | label_content_id:>rim.label_content!, trigger_type:st!, description:t!, change_control_id:>qms.change_control, implementation_due:d
market_launch | sku_id:>mdm.sku!, country_id:>org.country!, ma_id:>rim.marketing_authorization, launch_date:d, status:st!
renewal | ma_id:>rim.marketing_authorization!, due_date:d!, submission_id:>rim.submission, status:st!
regulatory_intelligence | country_id:>org.country, title:l!, summary:t, effective_date:d, impact_assessment:t, status:st!
idmp_substance | code:c!*, name:s!, unii:c, material_id:>mdm.material
idmp_medicinal_product | product_id:>mdm.product!, mpid:c, pharmaceutical_product:j

@schema rpt | Dashboards, reports, analytics
data_source | code:c!*, name:s!, source_type:st!, definition:j!
dashboard_definition V | code:c!*, name:s!, audience:st, layout:j!, is_gxp:bool!
dashboard_widget | dashboard_id:>rpt.dashboard_definition!, widget_type:st!, data_source_id:>rpt.data_source!, config:j!, grid_position:j!
report_definition V W | code:c!*, name:s!, report_type:st!, engine:st!, template_id:>meta.document_template, data_source_id:>rpt.data_source, parameters:j, is_validated:bool!
report_schedule | report_definition_id:>rpt.report_definition!, cron:c!, recipients:j!, format:st!, is_active:bool!
report_execution | report_definition_id:>rpt.report_definition!, report_version:c!, parameters:j, executed_by:>iam.app_user, executed_at:ts!, output_object_id:>file.stored_object, output_hash:h, row_count:i
kpi_snapshot N | metric_code:c!, org_node_id:>org.org_node, period:d!, value:n!
bi_dashboard_binding | engine:st!, external_dashboard_id:c!, report_definition_id:>rpt.report_definition, dataset_ref:s, row_filter_role:c
user_dashboard_preference N | user_id:>iam.app_user!, dashboard_id:>rpt.dashboard_definition!, overrides:j
saved_search N | user_id:>iam.app_user!, name:s!, entity_definition_id:>meta.entity_definition, query:j!, is_shared:bool!

@schema intg | Integration, messaging, migration
outbox_message N | aggregate_type:c!, aggregate_id:u!, event_type:c!, payload:j!, occurred_at:ts!, published_at:ts, attempts:i!
inbox_message N | message_id:c!*, source:c!, event_type:c!, payload:j!, received_at:ts!, processed_at:ts, status:st!
external_system | code:c!*, name:s!, system_type:st!, base_url:l, auth_config_ref:s, status:st!
data_mapping V | code:c!*, source_schema:j!, target_schema:j!, transform:t!
integration_endpoint | external_system_id:>intg.external_system!, code:c!, direction:st!, protocol:st!, mapping_id:>intg.data_mapping, status:st!
integration_run N | endpoint_id:>intg.integration_endpoint!, started_at:ts!, ended_at:ts, records_in:i, records_out:i, errors:i, status:st!
integration_error | run_id:>intg.integration_run!, record_ref:s, error:t!, payload:j, resolved:bool!
webhook_subscription | url:l!, event_types:j!, secret_ref:s!, is_active:bool!
idempotency_key N | idem_key:c!*, request_hash:h!, response:j, expires_at:ts!
migration_batch | source_system:c!, entity_type:c!, batch_no:i!, records:i!, checksum_source:h, checksum_target:h, status:st!, verified_by:>iam.app_user
migration_record_map | migration_batch_id:>intg.migration_batch!, legacy_id:s!, new_id:u!, status:st!
