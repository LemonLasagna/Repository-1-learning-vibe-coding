import bundleJson from '../../../../public/metadata/deviation.bundle.json';
import { computeFormStates, RuntimeBundle, validateBundle } from './runtime-bundle';

const bundle = bundleJson as unknown as RuntimeBundle;

describe('computeFormStates (Deviation bundle, state INVESTIGATION)', () => {
  it('the shipped bundle is valid', () => {
    expect(() => validateBundle(bundle)).not.toThrow();
  });

  it('hides the assessment section until a classification is chosen', () => {
    const s = computeFormStates(bundle, { classification: null });
    expect(s.sectionsVisible['assessment']).toBe(false);
    expect(s.fields['root_cause_summary'].hidden).toBe(true);
    expect(s.fields['root_cause_summary'].required).toBe(false);
  });

  it('requires QA owner and impact assessment for MAJOR and CRITICAL', () => {
    for (const classification of ['MAJOR', 'CRITICAL']) {
      const s = computeFormStates(bundle, { classification });
      expect(s.fields['qa_owner'].required).toBe(true);
      expect(s.fields['impact_assessment'].required).toBe(true);
    }
    const minor = computeFormStates(bundle, { classification: 'MINOR' });
    expect(minor.fields['qa_owner'].required).toBe(false);
  });

  it('hides batches affected for MINOR only', () => {
    expect(computeFormStates(bundle, { classification: 'MINOR' }).fields['batches_affected'].hidden).toBe(true);
    expect(computeFormStates(bundle, { classification: 'MAJOR' }).fields['batches_affected'].hidden).toBe(false);
  });

  it('shows and requires the justification only for planned deviations', () => {
    const unplanned = computeFormStates(bundle, { is_planned: false });
    expect(unplanned.fields['planned_justification']).toEqual({ hidden: true, readonly: false, required: false });
    const planned = computeFormStates(bundle, { is_planned: true });
    expect(planned.fields['planned_justification']).toEqual({ hidden: false, readonly: false, required: true });
  });

  it('applies per-state access: read-only fields are never required', () => {
    const s = computeFormStates(bundle, { classification: 'MAJOR' });
    expect(s.fields['title']).toEqual({ hidden: false, readonly: true, required: false });
    expect(s.fields['root_cause_summary'].required).toBe(true);
  });

  it('treats a failing rule as "condition false" and reports it', () => {
    const broken: RuntimeBundle = {
      ...bundle,
      rules: [{ code: 'R-BAD', effect: 'hide', fields: ['title'], when: { '+': ['text', 1] } }],
    };
    const s = computeFormStates(broken, {});
    expect(s.fields['title'].hidden).toBe(false);
    expect(s.ruleErrors[0]).toContain('R-BAD');
  });

  it('rejects a bundle that references an unknown field', () => {
    const bad: RuntimeBundle = { ...bundle, rules: [{ code: 'X', effect: 'hide', fields: ['nope'], when: true }] };
    expect(() => validateBundle(bad)).toThrow(/unknown field 'nope'/);
  });

  it('rejects a signed transition without the statement the signer must see', () => {
    const bad: RuntimeBundle = {
      ...bundle,
      transitions: [{ action: 'approve', label: 'Approve', signatureMeaning: 'APPROVED' }],
    };
    expect(() => validateBundle(bad)).toThrow(/signatureStatement/);
  });

  it('rejects a bundle with an unsafe rule expression', () => {
    const bad: RuntimeBundle = { ...bundle, rules: [{ code: 'X', effect: 'hide', fields: ['title'], when: { eval: 'x' } }] };
    expect(() => validateBundle(bad)).toThrow(/Unknown operator/);
  });
});
