import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthService, NAVIGATE, SessionUser } from '../../core/api/api';
import { Capa, CapaAvailable } from '../../core/api/capa-api';
import { CapaCreatePage, CapaDetailPage } from './capa-pages';

const owner: SessionUser = {
  sub: 'u1', login: 'qa.analyst', name: 'Quinn Analyst', tenantId: 't1',
  permissions: ['qms.capa:read', 'qms.capa:write'], authTime: null, logoutUrl: null,
};
const qa: SessionUser = { ...owner, sub: 'u2', login: 'qa.approver', name: 'Avery Approver', permissions: ['qms.capa:read', 'qms.capa:approve', 'esig.sign'] };

const capa: Capa = {
  id: 'c1', capaNo: 'CAPA-2026-00003', siteId: 's1', siteCode: 'BER', sourceType: 'DEVIATION', sourceId: 'd1', sourceNo: 'DEV-2026-00007',
  title: 'Fix labeller', problemStatement: 'Missing digits', capaType: 'CORRECTIVE', priority: 'HIGH', ownerId: 'u1', ownerName: 'Quinn Analyst',
  dueDate: '2027-01-13', status: 'DRAFT', closedAt: null, rowVersion: 4, updatedAt: '2026-10-15T08:00:00Z',
  actions: [{ no: 1, actionType: 'CORRECTIVE', description: 'Replace print head', assigneeId: 'u1', assigneeName: 'Quinn Analyst',
    dueDate: '2026-11-01', status: 'PLANNED', completedAt: null, evidence: null }],
  effectiveness: null,
};

const draft: CapaAvailable = {
  state: 'DRAFT', canEditPlan: true, canCompleteActions: false, canRecordEffectiveness: false,
  actions: [{ action: 'submit_plan', label: 'Submit plan for review', to: 'PLAN_REVIEW', signatureMeaning: null, requiresReason: false,
    blockedBy: 'Plan at least one action and the effectiveness check.' }],
};

describe('CAPA screens', () => {
  let http: HttpTestingController;
  const navigations: string[] = [];

  beforeEach(() => {
    navigations.length = 0;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'capas/:id', component: CapaDetailPage }]),
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

  it('raises a CAPA from a deviation, prefilled from it, without asking for the site', async () => {
    const fixture = TestBed.createComponent(CapaCreatePage);
    fixture.componentRef.setInput('sourceId', 'd1');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture.detectChanges();
    http.expectOne('/api/qms/v1/deviations/d1').flush(
      { id: 'd1', deviationNo: 'DEV-2026-00007', title: 'Label misprint', description: 'Missing digit', rootCauseSummary: 'Worn head', classification: 'MAJOR' },
      { headers: { ETag: '"5"' } },
    );
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Source: deviation DEV-2026-00007');
    expect(el.querySelector('select[formcontrolname="siteId"]')).toBeNull();
    button(el, 'Save draft')!.click();
    const req = http.expectOne('/api/qms/v1/capas');
    expect(req.request.body).toMatchObject({
      sourceType: 'DEVIATION', sourceId: 'd1', title: 'CAPA for DEV-2026-00007: Label misprint', priority: 'HIGH',
      problemStatement: 'Missing digit\n\nRoot cause: Worn head',
    });
    req.flush(capa, { headers: { ETag: '"1"' } });
    expect(navigate).toHaveBeenCalledWith(['/capas', 'c1']);
  });

  async function renderDetail(user: SessionUser, c: Capa, available: CapaAvailable) {
    TestBed.inject(AuthService).user.set(user);
    const fixture = TestBed.createComponent(CapaDetailPage);
    fixture.componentRef.setInput('id', c.id);
    fixture.detectChanges();
    await reload(fixture, user, c, available);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  async function reload(fixture: { whenStable(): Promise<unknown> }, user: SessionUser, c: Capa, available: CapaAvailable) {
    http.expectOne(`/api/qms/v1/capas/${c.id}`).flush(c, { headers: { ETag: `"${c.rowVersion}"` } });
    http.expectOne(`/api/qms/v1/capas/${c.id}/available-actions`).flush(available);
    http.expectOne(`/api/qms/v1/capas/${c.id}/history`).flush([]);
    if (available.canEditPlan) http.match('/api/iam/v1/users').forEach((r) => r.flush([{ id: 'u1', username: 'qa.analyst', displayName: 'Quinn Analyst' }]));
    await fixture.whenStable();
    if (user.permissions.includes('esig.sign')) http.match('/api/esig/v1/meanings?entity=qms.capa').forEach((r) => r.flush({
      statement: { version: '1', text: 'Binding.', acknowledged: true },
      meanings: [{ code: 'APPROVED', label: 'Approved', statement: 'I approve this record for use.', requiresReason: false, requiresSecondPerson: true }],
    }));
    http.match(`/api/esig/v1/records/qms.capa/${c.id}/signatures`).forEach((r) => r.flush({ currentVersion: c.rowVersion, items: [] }));
    http.match(`/api/audit/v1/records/qms/capa/${c.id}`).forEach((r) => r.flush([]));
    await fixture.whenStable();
  }

  it('plans an action against the CAPA version it read, and shows why the plan cannot go yet', async () => {
    const { fixture, el } = await renderDetail(owner, capa, draft);
    expect(button(el, 'Submit plan for review')!.disabled).toBe(true);
    expect(el.textContent).toContain('Plan at least one action and the effectiveness check.');

    const description = el.querySelector('form[aria-label="Add action"] textarea') as HTMLTextAreaElement;
    description.value = 'Add print head to the PM plan';
    description.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    button(el, 'Add action')!.click();
    const req = http.expectOne('/api/qms/v1/capas/c1/actions');
    expect(req.request.method).toBe('POST');
    expect(req.request.headers.get('If-Match')).toBe('"4"');
    expect(req.request.body).toMatchObject({ actionType: 'CORRECTIVE', description: 'Add print head to the PM plan', assigneeId: 'u1' });
    req.flush(capa, { headers: { ETag: '"5"' } });
    await reload(fixture, owner, { ...capa, rowVersion: 5 }, draft);
    expect(el.textContent).toContain('Action added.');
  });

  it('completes an action with evidence during implementation', async () => {
    const { fixture, el } = await renderDetail(owner, { ...capa, status: 'IMPLEMENTATION' },
      { state: 'IMPLEMENTATION', canEditPlan: false, canCompleteActions: true, canRecordEffectiveness: false, actions: [] });
    button(el, 'Complete…')!.click();
    await fixture.whenStable();
    const evidence = el.querySelector('[aria-label="Complete action"] textarea') as HTMLTextAreaElement;
    evidence.value = 'Work order WO-4711';
    evidence.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    button(el, 'Mark done')!.click();
    const req = http.expectOne('/api/qms/v1/capas/c1/actions/1/complete');
    expect(req.request.body).toEqual({ evidence: 'Work order WO-4711' });
    req.flush(capa, { headers: { ETag: '"5"' } });
    await reload(fixture, owner, { ...capa, status: 'IMPLEMENTATION', rowVersion: 5 },
      { state: 'IMPLEMENTATION', canEditPlan: false, canCompleteActions: true, canRecordEffectiveness: false, actions: [] });
  });

  it('signs the plan approval with the action attached, then sends QA to Keycloak', async () => {
    const { fixture, el } = await renderDetail(qa, { ...capa, status: 'PLAN_REVIEW' }, {
      state: 'PLAN_REVIEW', canEditPlan: false, canCompleteActions: false, canRecordEffectiveness: false,
      actions: [{ action: 'approve_plan', label: 'Approve plan', to: 'IMPLEMENTATION', signatureMeaning: 'APPROVED', requiresReason: false, blockedBy: null }],
    });
    button(el, 'Approve plan')!.click();
    await fixture.whenStable();
    button(el, 'Continue to sign')!.click();
    const req = http.expectOne('/api/esig/v1/challenges');
    expect(req.request.body).toMatchObject({ entity: 'qms.capa', recordId: 'c1', recordVersion: 4, meaning: 'APPROVED', action: 'approve_plan' });
    req.flush({ challengeId: 'x1', expiresAt: '' });
    expect(navigations).toEqual(['/bff/reauth?returnUrl=%2Fcapas%2Fc1%3Fesig%3Dx1']);
  });
});
