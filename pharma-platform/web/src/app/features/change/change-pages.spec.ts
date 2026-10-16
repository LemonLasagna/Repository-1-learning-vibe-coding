import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthService, NAVIGATE, SessionUser } from '../../core/api/api';
import { ChangeAvailable, ChangeControl } from '../../core/api/change-api';
import { ChangeCreatePage, ChangeDetailPage } from './change-pages';

const owner: SessionUser = {
  sub: 'u1', login: 'qa.analyst', name: 'Quinn Analyst', tenantId: 't1',
  permissions: ['qms.change:read', 'qms.change:write'], authTime: null, logoutUrl: null,
};
const qa: SessionUser = { ...owner, sub: 'u2', login: 'qa.approver', name: 'Avery Approver', permissions: ['qms.change:read', 'qms.change:approve', 'esig.sign'] };

const change: ChangeControl = {
  id: 'x1', ccNo: 'CC-2026-00004', siteId: 's1', siteCode: 'BER', title: 'New labeller', description: 'Swap the labeller', justification: 'End of life',
  changeType: 'PLANNED', changeCategory: 'MAJOR', isTemporary: false, temporaryEndDate: null, regulatoryImpact: 'NONE', implementationDue: '2027-01-13',
  effectiveDate: null, ownerId: 'u1', ownerName: 'Quinn Analyst', status: 'ASSESSMENT', rowVersion: 4, updatedAt: '2026-10-15T08:00:00Z',
  impacts: [], tasks: [],
};

const assessing: ChangeAvailable = {
  state: 'ASSESSMENT', canEditHeader: true, canAssess: true, canPlanTasks: true, canCompleteTasks: false, canSetDates: false,
  actions: [{ action: 'submit_for_approval', label: 'Submit for approval', to: 'APPROVAL', signatureMeaning: null, requiresReason: false,
    blockedBy: 'Record at least one impact assessment, and one for the regulatory area when the regulatory impact is major.' }],
};

const implementing: ChangeAvailable = {
  state: 'IMPLEMENTATION', canEditHeader: false, canAssess: false, canPlanTasks: false, canCompleteTasks: true, canSetDates: true, actions: [],
};

describe('Change control screens', () => {
  let http: HttpTestingController;
  const navigations: string[] = [];

  beforeEach(() => {
    navigations.length = 0;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'changes/:id', component: ChangeDetailPage }]),
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

  it('raises a change control and opens it', async () => {
    const fixture = TestBed.createComponent(ChangeCreatePage);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture.detectChanges();
    http.expectOne('/api/org/v1/sites').flush([{ id: 's1', code: 'BER', name: 'Berlin' }]);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const set = (selector: string, value: string) => {
      const input = el.querySelector(selector) as HTMLInputElement | HTMLTextAreaElement;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    };
    set('input[formcontrolname="title"]', 'New labeller');
    set('textarea[formcontrolname="description"]', 'Swap the labeller');
    set('textarea[formcontrolname="justification"]', 'End of life');
    await fixture.whenStable();
    button(el, 'Save draft')!.click();
    const req = http.expectOne('/api/qms/v1/changes');
    expect(req.request.body).toMatchObject({
      siteId: 's1', title: 'New labeller', description: 'Swap the labeller', justification: 'End of life', changeType: 'PLANNED',
      changeCategory: 'MINOR', regulatoryImpact: 'NONE', isTemporary: false,
    });
    expect(req.request.body).not.toHaveProperty('temporaryEndDate');
    req.flush(change, { headers: { ETag: '"1"' } });
    expect(navigate).toHaveBeenCalledWith(['/changes', 'x1']);
  });

  async function renderDetail(user: SessionUser, c: ChangeControl, available: ChangeAvailable) {
    TestBed.inject(AuthService).user.set(user);
    const fixture = TestBed.createComponent(ChangeDetailPage);
    fixture.componentRef.setInput('id', c.id);
    fixture.detectChanges();
    await reload(fixture, user, c, available);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  async function reload(fixture: { whenStable(): Promise<unknown> }, user: SessionUser, c: ChangeControl, available: ChangeAvailable) {
    http.expectOne(`/api/qms/v1/changes/${c.id}`).flush(c, { headers: { ETag: `"${c.rowVersion}"` } });
    http.expectOne(`/api/qms/v1/changes/${c.id}/available-actions`).flush(available);
    http.expectOne(`/api/qms/v1/changes/${c.id}/history`).flush([]);
    if (available.canPlanTasks) http.match('/api/iam/v1/users').forEach((r) => r.flush([{ id: 'u1', username: 'qa.analyst', displayName: 'Quinn Analyst' }]));
    await fixture.whenStable();
    if (user.permissions.includes('esig.sign')) http.match('/api/esig/v1/meanings?entity=qms.change').forEach((r) => r.flush({
      statement: { version: '1', text: 'Binding.', acknowledged: true },
      meanings: [{ code: 'APPROVED', label: 'Approved', statement: 'I approve this record for use.', requiresReason: false, requiresSecondPerson: true }],
    }));
    http.match(`/api/esig/v1/records/qms.change/${c.id}/signatures`).forEach((r) => r.flush({ currentVersion: c.rowVersion, items: [] }));
    http.match(`/api/audit/v1/records/qms/change_control/${c.id}`).forEach((r) => r.flush([]));
    await fixture.whenStable();
  }

  it('assesses an impact against the version it read, and shows why it cannot be submitted yet', async () => {
    const { fixture, el } = await renderDetail(owner, change, assessing);
    expect(button(el, 'Submit for approval')!.disabled).toBe(true);
    expect(el.textContent).toContain('Record at least one impact assessment');

    const assessment = el.querySelector('form[aria-label="Assess impact"] textarea[formcontrolname="assessment"]') as HTMLTextAreaElement;
    assessment.value = 'The new labeller needs qualification.';
    assessment.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    button(el, 'Save assessment')!.click();
    const req = http.expectOne('/api/qms/v1/changes/x1/impacts/QUALITY');
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('If-Match')).toBe('"4"');
    expect(req.request.body).toEqual({ isImpacted: true, assessment: 'The new labeller needs qualification.', actionRequired: null });
    req.flush(change, { headers: { ETag: '"5"' } });
    await reload(fixture, owner, { ...change, rowVersion: 5 }, assessing);
    expect(el.textContent).toContain('Quality assessment saved.');
  });

  it('completes a task and sets the effective date during implementation', async () => {
    const inImplementation: ChangeControl = {
      ...change, status: 'IMPLEMENTATION',
      tasks: [{ no: 1, phase: 'IMPLEMENTATION', description: 'Qualify it', assigneeId: 'u1', assigneeName: 'Quinn Analyst', dueDate: '2026-11-01', status: 'PLANNED', completedAt: null }],
    };
    const { fixture, el } = await renderDetail(owner, inImplementation, implementing);
    button(el, 'Mark done')!.click();
    const done = http.expectOne('/api/qms/v1/changes/x1/tasks/1/complete');
    expect(done.request.headers.get('If-Match')).toBe('"4"');
    done.flush(inImplementation, { headers: { ETag: '"5"' } });
    await reload(fixture, owner, { ...inImplementation, rowVersion: 5 }, implementing);

    const date = el.querySelector('form[aria-label="Set dates"] input[formcontrolname="effectiveDate"]') as HTMLInputElement;
    date.value = '2026-12-01';
    date.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    button(el, 'Save dates')!.click();
    const dates = http.expectOne('/api/qms/v1/changes/x1/implementation');
    expect(dates.request.method).toBe('PATCH');
    expect(dates.request.headers.get('If-Match')).toBe('"5"');
    expect(dates.request.body).toEqual({ effectiveDate: '2026-12-01', implementationDue: '2027-01-13' });
    dates.flush(inImplementation, { headers: { ETag: '"6"' } });
    await reload(fixture, owner, { ...inImplementation, rowVersion: 6 }, implementing);
  });

  it('signs the approval with the action attached, then sends QA to Keycloak', async () => {
    const { fixture, el } = await renderDetail(qa, { ...change, status: 'APPROVAL' }, {
      state: 'APPROVAL', canEditHeader: false, canAssess: false, canPlanTasks: false, canCompleteTasks: false, canSetDates: false,
      actions: [{ action: 'approve', label: 'Approve change', to: 'IMPLEMENTATION', signatureMeaning: 'APPROVED', requiresReason: false, blockedBy: null }],
    });
    button(el, 'Approve change')!.click();
    await fixture.whenStable();
    button(el, 'Continue to sign')!.click();
    const req = http.expectOne('/api/esig/v1/challenges');
    expect(req.request.body).toMatchObject({ entity: 'qms.change', recordId: 'x1', recordVersion: 4, meaning: 'APPROVED', action: 'approve' });
    req.flush({ challengeId: 'y1', expiresAt: '' });
    expect(navigations).toEqual(['/bff/reauth?returnUrl=%2Fchanges%2Fx1%3Fesig%3Dy1']);
  });
});
