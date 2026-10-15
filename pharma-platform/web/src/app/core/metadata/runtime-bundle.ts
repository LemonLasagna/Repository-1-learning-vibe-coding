import { evaluate, isTruthy, Json, validate } from '../rules/rule-evaluator';

/**
 * Compiled runtime bundle for one entity in one workflow state (DS §10.1). Produced by the
 * config-studio service from meta.* definitions; the client never builds forms by hand.
 */
export type FieldType = 'text' | 'longtext' | 'number' | 'date' | 'datetime' | 'lookup' | 'boolean';
export type Access = 'hidden' | 'read' | 'edit' | 'required';

export interface FieldDef {
  code: string;
  label: string;
  type: FieldType;
  required?: boolean;
  maxLength?: number;
  unit?: string;
  help?: string;
  options?: { code: string; label: string }[];
}

export interface SectionDef {
  code: string;
  title: string;
  fields: string[];
  visibleWhen?: Json;
}

export interface FieldRule {
  code: string;
  effect: 'require' | 'hide' | 'readonly';
  fields: string[];
  when: Json;
}

export interface TransitionDef {
  action: string;
  label: string;
  signatureMeaning?: string;
  signatureMeaningLabel?: string;
  /** Exact statement shown to the signer; must describe this meaning (FS-ESIG-001). */
  signatureStatement?: string;
}

export interface RuntimeBundle {
  entity: string;
  entityLabel: string;
  bundleVersion: string;
  workflowState: string;
  fields: FieldDef[];
  sections: SectionDef[];
  /** Per-state field access (meta.form_state_override). Missing means 'edit'. */
  stateAccess: Record<string, Access>;
  rules: FieldRule[];
  transitions: TransitionDef[];
}

export interface FieldState {
  hidden: boolean;
  readonly: boolean;
  required: boolean;
}

export interface FormStates {
  fields: Record<string, FieldState>;
  sectionsVisible: Record<string, boolean>;
  /** Rules that failed to evaluate. They are treated as "condition false"; the server re-validates. */
  ruleErrors: string[];
}

/** Rejects a bundle whose references or rule expressions are invalid, before anything is rendered. */
export function validateBundle(b: RuntimeBundle): void {
  const codes = new Set(b.fields.map((f) => f.code));
  const check = (code: string, where: string) => {
    if (!codes.has(code)) throw new Error(`${where} references unknown field '${code}'`);
  };
  b.sections.forEach((s) => {
    s.fields.forEach((f) => check(f, `Section '${s.code}'`));
    if (s.visibleWhen !== undefined) validate(s.visibleWhen);
  });
  b.transitions
    .filter((t) => t.signatureMeaning && !t.signatureStatement)
    .forEach((t) => {
      throw new Error(`Transition '${t.action}' requires a signature but has no signatureStatement`);
    });
  b.rules.forEach((r) => {
    r.fields.forEach((f) => check(f, `Rule '${r.code}'`));
    validate(r.when);
  });
  Object.keys(b.stateAccess).forEach((f) => check(f, 'stateAccess'));
}

/**
 * Field visibility, read-only and requiredness for the current values. Pure function: the same
 * inputs always give the same states, which is what the component tests assert.
 * Precedence: hidden beats read-only beats required (a hidden or read-only field is never required).
 */
export function computeFormStates(b: RuntimeBundle, value: Record<string, Json>): FormStates {
  const ruleErrors: string[] = [];
  const holds = (code: string, expr: Json): boolean => {
    try {
      return isTruthy(evaluate(expr, value));
    } catch (e) {
      ruleErrors.push(`${code}: ${(e as Error).message}`);
      return false;
    }
  };

  const sectionsVisible: Record<string, boolean> = {};
  const fieldSectionHidden = new Set<string>();
  for (const s of b.sections) {
    const visible = s.visibleWhen === undefined || holds(`section:${s.code}`, s.visibleWhen);
    sectionsVisible[s.code] = visible;
    if (!visible) s.fields.forEach((f) => fieldSectionHidden.add(f));
  }

  const active = { require: new Set<string>(), hide: new Set<string>(), readonly: new Set<string>() };
  for (const r of b.rules) {
    if (holds(r.code, r.when)) r.fields.forEach((f) => active[r.effect].add(f));
  }

  const fields: Record<string, FieldState> = {};
  for (const f of b.fields) {
    const access = b.stateAccess[f.code] ?? 'edit';
    const hidden = access === 'hidden' || fieldSectionHidden.has(f.code) || active.hide.has(f.code);
    const readonly = !hidden && (access === 'read' || active.readonly.has(f.code));
    const required =
      !hidden && !readonly && (!!f.required || access === 'required' || active.require.has(f.code));
    fields[f.code] = { hidden, readonly, required };
  }
  return { fields, sectionsVisible, ruleErrors };
}
