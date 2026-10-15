import { TestBed } from '@angular/core/testing';
import bundleJson from '../../../../public/metadata/deviation.bundle.json';
import { MetadataForm } from './metadata-form';
import { RuntimeBundle } from './runtime-bundle';

const bundle = bundleJson as unknown as RuntimeBundle;

async function render(initialValue: Record<string, unknown> = {}) {
  const fixture = TestBed.createComponent(MetadataForm);
  fixture.componentRef.setInput('bundle', bundle);
  fixture.componentRef.setInput('initialValue', initialValue);
  await fixture.whenStable();
  const el = fixture.nativeElement as HTMLElement;
  return { fixture, el, cmp: fixture.componentInstance };
}

describe('MetadataForm', () => {
  it('renders every visible field with its label', async () => {
    const { el } = await render({ classification: 'MAJOR' });
    const labels = Array.from(el.querySelectorAll('[data-field] label')).map((l) => l.textContent?.trim());
    expect(labels.some((l) => l?.startsWith('Title'))).toBe(true);
    expect(el.querySelector('[data-section="assessment"]')).not.toBeNull();
    expect(el.querySelector('[data-field="planned_justification"]')).toBeNull();
  });

  it('reveals and requires the justification when "planned" is ticked', async () => {
    const { fixture, el, cmp } = await render({ classification: 'MINOR' });
    cmp.control('is_planned').setValue(true);
    await fixture.whenStable();

    const field = el.querySelector('[data-field="planned_justification"]');
    expect(field).not.toBeNull();
    expect(field!.querySelector('.req')).not.toBeNull();
    expect(cmp.control('planned_justification').hasError('required')).toBe(true);
  });

  it('disables read-only fields so they are not edited in this state', async () => {
    const { el, cmp } = await render({ title: 'Existing title', classification: 'MAJOR' });
    expect(cmp.control('title').disabled).toBe(true);
    expect((el.querySelector('#f-title') as HTMLInputElement).disabled).toBe(true);
    expect(cmp.rawValue()['title']).toBe('Existing title');
  });

  it('is invalid until rule-required fields are filled', async () => {
    const { fixture, cmp } = await render({
      title: 'T', classification: 'MAJOR', occurred_at: '2026-10-02T03:14', description: 'D',
    });
    expect(cmp.checkValid()).toBe(false);
    cmp.control('qa_owner').setValue('skeller');
    cmp.control('impact_assessment').setValue('No impact: batches remained within limits.');
    cmp.control('root_cause_summary').setValue('Chiller setpoint drift.');
    await fixture.whenStable();
    expect(cmp.checkValid()).toBe(true);
  });

  it('switching to MINOR drops the MAJOR-only requirements', async () => {
    const { fixture, cmp } = await render({ classification: 'MAJOR' });
    expect(cmp.control('qa_owner').hasError('required')).toBe(true);
    cmp.control('classification').setValue('MINOR');
    await fixture.whenStable();
    expect(cmp.control('qa_owner').hasError('required')).toBe(false);
  });
});
