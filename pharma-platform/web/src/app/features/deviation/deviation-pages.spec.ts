import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';
import bundle from '../../../../public/metadata/deviation.bundle.json';
import { AuthService, NAVIGATE, SessionUser } from '../../core/api/api';
import { Deviation, DeviationActions } from '../../core/api/deviation-api';
import { MetadataForm } from '../../core/metadata/metadata-form';
import { DeviationCreatePage, DeviationDetailPage, DeviationListPage } from './deviation-pages';

const owner: SessionUser = {
  sub: 's1', login: 'qa.analyst', name: 'Quinn Analyst', tenantId: 't1',
  permissions: ['qms.deviation:read', 'qms.deviation:write', 'esig.sign'], authTime: null, logoutUrl: null,
};
const reader: SessionUser = { ...owner, login: 'read.only', name: 'Riley Reader', permissions: ['qms.deviation:read'] };

const deviation: Deviation = {
  id: 'd1', deviationNo: 'DEV-2026-00007', siteId: 'site1', siteCode: 'BER', title: 'Label misprint', description: 'Missing digit',
  classification: 'MINOR', deviationType: 'PROCESS', isPlanned: false, occurredAt: '2026-10-15T06:00:00Z',
  discoveredAt: '2026-10-15T07:00:00Z', immediateAction: null, impactAssessment: null, rootCauseSummary: null, riskScore: null,
  plannedJustification: null, shift: 'EARLY', batchesAffected: null, ownerId: 's1', ownerName: 'Quinn Analyst', qaOwnerId: null,
  qaOwnerName: null, dueDate: '2026-11-14', closedAt: null, status: 'DRAFT', rowVersion: 5, updatedAt: '2026-10-15T07:00:00Z',
};

const options = {
  statement: { version: '1', text: 'My electronic signature is legally binding.', acknowledged: true },
  meanings: [
    { code: 'AUTHORED', label: 'Authored', statement: 'I am the author of this record.', requiresReason: false, requiresSecondPerson: false },
    { code: 'APPROVED', label: 'Approved', statement: 'I approve this record for use.', requiresReason: false, requiresSecondPerson: true },
    { code: 'CANCELLED', label: 'Cancelled', statement: 'I cancel this record.', requiresReason: true, requiresSecondPerson: false },
  ],
};

describe('Deviation screens', () => {
  let http: HttpTestingController;
  const navigations: string[] = [];

  beforeEach(() => {
    navigations.length = 0;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'deviations/:id', component: DeviationDetailPage }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: NAVIGATE, useValue: (url: string) => navigations.push(url) },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  const button = (el: HTMLElement, text: string) =>
    Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.includes(text)) as HTMLButtonElement | undefined;

  it('lists deviations and offers "New deviation" only to those who may raise one', async () => {
    TestBed.inject(AuthService).user.set(reader);
    const fixture = TestBed.createComponent(DeviationListPage);
    fixture.detectChanges();
    http.expectOne('/api/qms/v1/deviations').flush({ items: [deviation] });
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('DEV-2026-00007');
    expect(el.textContent).toContain('Draft');
    expect(el.querySelector('a[href="/deviations/new"]')).toBeNull();
  });

  it('raises a draft with the site and the configured fields, in API names and with a UTC offset', async () => {
    const fixture = TestBed.createComponent(DeviationCreatePage);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture.detectChanges();
    http.expectOne('/api/org/v1/sites').flush([{ id: 'site1', code: 'BER', name: 'Berlin', timezone: 'Europe/Berlin' }]);
    http.expectOne('/metadata/deviation.bundle.json').flush(bundle);
    await fixture.whenStable();

    const form = fixture.debugElement.query(By.directive(MetadataForm)).componentInstance as MetadataForm;
    form.control('title').setValue('Label misprint');
    form.control('classification').setValue('MINOR');
    form.control('deviation_type').setValue('PROCESS');
    form.control('occurred_at').setValue('2026-10-15T08:30');
    form.control('description').setValue('Missing digit');
    await fixture.whenStable();
    button(fixture.nativeElement, 'Save draft')!.click();

    const req = http.expectOne('/api/qms/v1/deviations');
    expect(req.request.body).toMatchObject({
      siteId: 'site1', title: 'Label misprint', classification: 'MINOR', deviationType: 'PROCESS', description: 'Missing digit', isPlanned: false,
    });
    expect(req.request.body.occurredAt).toMatch(/Z$/);
    expect('dueDate' in req.request.body).toBe(false);
    req.flush({ ...deviation, id: 'd9' }, { headers: { ETag: '"1"' } });
    expect(navigate).toHaveBeenCalledWith(['/deviations', 'd9']);
  });

  async function renderDetail(user: SessionUser, d: Deviation, actions: DeviationActions, esig?: string) {
    TestBed.inject(AuthService).user.set(user);
    const fixture = TestBed.createComponent(DeviationDetailPage);
    fixture.componentRef.setInput('id', d.id);
    if (esig) fixture.componentRef.setInput('esig', esig);
    fixture.detectChanges();
    http.expectOne('/metadata/deviation.bundle.json').flush(bundle);
    const completion = esig ? http.expectOne(`/api/esig/v1/challenges/${esig}/complete`) : null;
    const loadAll = async () => {
      http.expectOne(`/api/qms/v1/deviations/${d.id}`).flush(d, { headers: { ETag: `"${d.rowVersion}"` } });
      http.expectOne(`/api/qms/v1/deviations/${d.id}/available-actions`).flush(actions);
      http.expectOne(`/api/qms/v1/deviations/${d.id}/history`).flush([]);
      await fixture.whenStable();
      // The workflow panel loads the signing options once the record is shown (only for those who may sign).
      if (user.permissions.includes('esig.sign')) {
        http.match('/api/esig/v1/meanings?entity=qms.deviation').forEach((r) => r.flush(options));
      }
      http.expectOne(`/api/esig/v1/records/qms.deviation/${d.id}/signatures`).flush({ currentVersion: d.rowVersion, items: [] });
      http.expectOne(`/api/audit/v1/records/qms/deviation/${d.id}`).flush([]);
      await fixture.whenStable();
    };
    if (!esig) await loadAll();
    const el = fixture.nativeElement as HTMLElement;
    const form = () => fixture.debugElement.query(By.directive(MetadataForm)).componentInstance as MetadataForm;
    return { fixture, el, form, completion, loadAll };
  }

  const draftActions: DeviationActions = {
    state: 'DRAFT', editableFields: ['title', 'description', 'classification'], reasonForChangeRequired: false,
    actions: [
      { action: 'submit', label: 'Submit', to: 'TRIAGE', signatureMeaning: 'AUTHORED', requiresReason: false, blockedBy: null },
      { action: 'cancel', label: 'Cancel', to: 'CANCELLED', signatureMeaning: 'CANCELLED', requiresReason: true, blockedBy: null },
    ],
  };

  it('makes only the fields the server allows editable', async () => {
    const { form, el } = await renderDetail(owner, deviation, draftActions);
    expect(form().control('title').enabled).toBe(true);
    expect(form().control('root_cause_summary').disabled).toBe(true);
    expect(button(el, 'Save')).toBeDefined();
  });

  it('shows a read-only record to readers, without actions', async () => {
    const { form, el } = await renderDetail(reader, { ...deviation, status: 'TRIAGE' },
      { state: 'TRIAGE', editableFields: [], reasonForChangeRequired: true, actions: [] });
    expect(form().control('title').disabled).toBe(true);
    expect(button(el, 'Save')).toBeUndefined();
    expect(el.querySelector('.progress .current')?.textContent).toContain('Triage');
  });

  it('signs a transition: the challenge carries the action, then Keycloak asks for the password', async () => {
    const { fixture, el } = await renderDetail(owner, deviation, draftActions);
    button(el, 'Submit')!.click();
    await fixture.whenStable();
    const dialog = el.querySelector('[role="dialog"]')!;
    expect(dialog.textContent).toContain('DEV-2026-00007 (Submit)');
    expect(dialog.textContent).toContain('Authored');
    button(el, 'Continue to sign')!.click();
    const req = http.expectOne('/api/esig/v1/challenges');
    expect(req.request.body).toEqual({
      entity: 'qms.deviation', recordId: 'd1', recordVersion: 5, meaning: 'AUTHORED', reason: null, acknowledge: false, action: 'submit',
    });
    req.flush({ challengeId: 'c1', expiresAt: '' });
    expect(navigations).toEqual(['/bff/reauth?returnUrl=%2Fdeviations%2Fd1%3Fesig%3Dc1']);
  });

  it('asks for a reason before an unsigned transition that needs one, and sends the version it read', async () => {
    const qa: SessionUser = { ...owner, permissions: ['qms.deviation:read', 'qms.deviation:approve'] };
    const { fixture, el } = await renderDetail(qa, { ...deviation, status: 'QA_APPROVAL' }, {
      state: 'QA_APPROVAL', editableFields: [], reasonForChangeRequired: true,
      actions: [{ action: 'reject', label: 'Return to investigation', to: 'INVESTIGATION', signatureMeaning: null, requiresReason: true, blockedBy: null }],
    });
    button(el, 'Return to investigation')!.click();
    await fixture.whenStable();
    const textarea = el.querySelector('.reason-panel textarea') as HTMLTextAreaElement;
    textarea.value = 'Add the maintenance record';
    textarea.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    button(el, 'Confirm')!.click();
    const req = http.expectOne('/api/qms/v1/deviations/d1/transitions');
    expect(req.request.headers.get('If-Match')).toBe('"5"');
    expect(req.request.body).toEqual({ action: 'reject', reason: 'Add the maintenance record' });
    req.flush({ ...deviation, status: 'INVESTIGATION', rowVersion: 6 }, { headers: { ETag: '"6"' } });
    http.expectOne('/api/qms/v1/deviations/d1').flush({ ...deviation, status: 'INVESTIGATION', rowVersion: 6 }, { headers: { ETag: '"6"' } });
    http.expectOne('/api/qms/v1/deviations/d1/available-actions').flush({ state: 'INVESTIGATION', editableFields: [], reasonForChangeRequired: true, actions: [] });
    http.expectOne('/api/qms/v1/deviations/d1/history').flush([]);
    await fixture.whenStable();
    http.expectOne('/api/esig/v1/records/qms.deviation/d1/signatures').flush({ currentVersion: 6, items: [] });
    http.expectOne('/api/audit/v1/records/qms/deviation/d1').flush([]);
    await fixture.whenStable();
    expect(el.textContent).toContain('Return to investigation: now Investigation.');
  });

  it('shows why an action is not possible yet', async () => {
    const { el } = await renderDetail(owner, { ...deviation, status: 'INVESTIGATION' }, {
      state: 'INVESTIGATION', editableFields: ['rootCauseSummary'], reasonForChangeRequired: true,
      actions: [{ action: 'submit_for_approval', label: 'Submit for QA approval', to: 'QA_APPROVAL', signatureMeaning: null,
        requiresReason: false, blockedBy: 'Enter the root cause summary.' }],
    });
    expect(button(el, 'Submit for QA approval')!.disabled).toBe(true);
    expect(el.textContent).toContain('Enter the root cause summary.');
  });

  it('completes the signature when Keycloak sends the user back', async () => {
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const { fixture, el, completion, loadAll } = await renderDetail(owner, { ...deviation, status: 'TRIAGE', rowVersion: 6 },
      { state: 'TRIAGE', editableFields: [], reasonForChangeRequired: true, actions: [] }, 'c1');
    completion!.flush({ id: 'sig1', signerName: 'Quinn Analyst', meaningLabel: 'Authored', meaning: 'AUTHORED' });
    await fixture.whenStable();
    await loadAll();
    expect(el.textContent).toContain('Signed: Authored by Quinn Analyst.');
  });
});
