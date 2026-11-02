import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthService, NAVIGATE, SessionUser, Signature } from '../../core/api/api';
import { WorkflowDraft, WorkflowTransition } from '../../core/api/workflow-api';
import { WorkflowDetailPage, WorkflowListPage } from './workflow-pages';

const transitions: WorkflowTransition[] = [
  { action: 'submit_plan', from: 'DRAFT', to: 'PLAN_REVIEW', label: 'Submit plan for review', permission: 'qms.capa:write',
    guard: null, guardMessage: null, signatureMeaning: null, requireReason: false },
  { action: 'approve_plan', from: 'PLAN_REVIEW', to: 'IMPLEMENTATION', label: 'Approve plan', permission: 'qms.capa:approve',
    guard: null, guardMessage: null, signatureMeaning: 'APPROVED', requireReason: false },
];

const draft = (over: Partial<WorkflowDraft> = {}): WorkflowDraft => ({
  id: 'd1', code: 'CAPA_STD', version: 2, initialState: 'DRAFT', transitions, status: 'VALID', errors: [], submittedBy: null,
  submittedAt: null, approvedBy: null, approvedAt: null, decisionComment: null, activatedAt: null, approvalSignatureId: null,
  rowVersion: 1, updatedAt: '2026-10-16T10:00:00Z', ...over,
});

const user = (...permissions: string[]): SessionUser => ({
  sub: 's1', login: 'o.owner', name: 'Olu Owner', tenantId: 't1', permissions, authTime: null, logoutUrl: null,
});

const options = {
  statement: { version: '1', text: 'My electronic signature is legally binding.', acknowledged: false },
  meanings: [{ code: 'APPROVED', label: 'Approved', statement: 'I approve this record for use.', requiresReason: false, requiresSecondPerson: true }],
};

const signature: Signature = {
  id: 'sig1', signerName: 'Olu Owner', signerLogin: 'o.owner', meaning: 'APPROVED', meaningLabel: 'Approved',
  signedAt: '2026-10-16T12:00:00Z', recordVersion: 2, recordHash: 'a'.repeat(64), reason: null,
  manifest: 'Electronically signed by Olu Owner (o.owner)', appliesToCurrentVersion: true,
};

const button = (el: HTMLElement, text: string) =>
  Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === text) as HTMLButtonElement | undefined;

describe('Workflow pages', () => {
  let http: HttpTestingController;
  const navigations: string[] = [];

  beforeEach(() => {
    navigations.length = 0;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: NAVIGATE, useValue: (url: string) => navigations.push(url) },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  describe('WorkflowListPage', () => {
    it('starts a new draft as a copy of the current workflow at the next version', async () => {
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
      const fixture = TestBed.createComponent(WorkflowListPage);
      fixture.detectChanges();
      http.expectOne('/api/workflow/v1/workflows').flush({ items: [
        { code: 'CAPA_STD', recordType: 'CAPA', name: 'CAPA (standard)', version: 1, initialState: 'DRAFT', transitions },
      ] });
      http.expectOne('/api/workflow/v1/definitions').flush({ items: [draft({ id: 'old', status: 'ACTIVE' })] });
      await fixture.whenStable();
      const el = fixture.nativeElement as HTMLElement;
      expect(el.textContent).toContain('CAPA (standard)');
      expect(el.textContent).toContain('Active');

      button(el, 'New draft')!.click();
      const create = http.expectOne('/api/workflow/v1/definitions');
      expect(create.request.method).toBe('POST');
      expect(create.request.body).toEqual({ code: 'CAPA_STD', version: 2, initialState: 'DRAFT', transitions });
      create.flush(draft(), { headers: { ETag: '"1"' } });
      await fixture.whenStable();
      expect(navigate).toHaveBeenCalledWith(['/workflows', 'd1']);
    });
  });

  describe('WorkflowDetailPage', () => {
    const draftUrl = '/api/workflow/v1/definitions/d1';

    /** Renders the draft page; flushes the requests the page makes on opening. */
    async function render(d: WorkflowDraft, permissions: string[], esig?: string) {
      vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
      TestBed.inject(AuthService).user.set(user('wf.definition:author', ...permissions));
      const fixture = TestBed.createComponent(WorkflowDetailPage);
      fixture.componentRef.setInput('id', d.id);
      if (esig) fixture.componentRef.setInput('esig', esig);
      fixture.detectChanges();
      http.expectOne(draftUrl).flush(d, { headers: { ETag: `"${d.rowVersion}"` } });
      if (permissions.includes('wf.definition:approve') && permissions.includes('esig.sign'))
        http.expectOne('/api/esig/v1/meanings?entity=wf.definition').flush(options);
      const completion = esig ? http.expectOne(`/api/esig/v1/challenges/${esig}/complete`) : null;
      await fixture.whenStable();
      if (d.approvalSignatureId)
        http.expectOne('/api/esig/v1/records/wf.definition/d1/signatures').flush({ currentVersion: d.rowVersion, items: [signature] });
      await fixture.whenStable();
      return { fixture, cmp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, completion };
    }

    it('submits a valid draft at the version it read', async () => {
      const { fixture, el } = await render(draft(), []);
      button(el, 'Submit for approval')!.click();
      const req = http.expectOne('/api/workflow/v1/definitions/d1/submit');
      expect(req.request.headers.get('If-Match')).toBe('"1"');
      req.flush(draft({ status: 'SUBMITTED', rowVersion: 2 }), { headers: { ETag: '"2"' } });
      await fixture.whenStable();
      expect(el.textContent).toContain('Submitted for approval.');
      expect(button(el, 'Submit for approval')).toBeUndefined();
    });

    it('lists the problems of a draft and does not offer to submit it', async () => {
      const { el } = await render(draft({ status: 'DRAFT', errors: ["State 'X' is not a CAPA state."] }), []);
      expect(el.textContent).toContain("State 'X' is not a CAPA state.");
      expect(button(el, 'Submit for approval')!.disabled).toBe(true);
    });

    it('does not send transitions that are not valid JSON', async () => {
      const { fixture, cmp, el } = await render(draft(), []);
      cmp.transitions.setValue('[{ broken');
      cmp.transitions.markAsDirty();
      fixture.detectChanges();
      button(el, 'Save draft')!.click();
      fixture.detectChanges();
      expect(el.textContent).toContain('Transitions must be a JSON list');
    });

    it('saves edited transitions with the draft code and initial state', async () => {
      const { fixture, cmp, el } = await render(draft(), []);
      const edited = [{ ...transitions[0], label: 'Send plan to QA' }, transitions[1]];
      cmp.transitions.setValue(JSON.stringify(edited));
      cmp.transitions.markAsDirty();
      fixture.detectChanges();
      button(el, 'Save draft')!.click();
      const req = http.expectOne(draftUrl);
      expect(req.request.method).toBe('PUT');
      expect(req.request.body).toEqual({ code: 'CAPA_STD', version: 2, initialState: 'DRAFT', transitions: edited });
      req.flush(draft({ transitions: edited, rowVersion: 2 }), { headers: { ETag: '"2"' } });
      await fixture.whenStable();
      expect(el.textContent).toContain('Send plan to QA');
    });

    it('needs a comment to reject, and sends it', async () => {
      const { fixture, el } = await render(draft({ status: 'SUBMITTED' }), ['wf.definition:approve']);
      expect(button(el, 'Reject')!.disabled).toBe(true);
      const comment = el.querySelector('input[maxlength="1000"]') as HTMLInputElement;
      comment.value = 'Labels need work.';
      comment.dispatchEvent(new Event('input'));
      await fixture.whenStable();
      expect(button(el, 'Reject')!.disabled).toBe(false);
      button(el, 'Reject')!.click();
      const req = http.expectOne('/api/workflow/v1/definitions/d1/reject');
      expect(req.request.body).toEqual({ comment: 'Labels need work.' });
      req.flush(draft({ status: 'VALID', decisionComment: 'Labels need work.', rowVersion: 2 }), { headers: { ETag: '"2"' } });
      await fixture.whenStable();
      expect(el.textContent).toContain('Rejected.');
    });

    it('offers the approval only with the approval permission', async () => {
      const { el } = await render(draft({ status: 'SUBMITTED' }), []);
      expect(button(el, 'Approve and sign…')).toBeUndefined();
      expect(button(el, 'Reject')).toBeUndefined();
    });

    it('says why the approval cannot be signed without the e-signature permission', async () => {
      const { el } = await render(draft({ status: 'SUBMITTED' }), ['wf.definition:approve']);
      expect(button(el, 'Approve and sign…')).toBeUndefined();
      expect(el.textContent).toContain('needs the electronic signature permission');
    });

    it('asks for an electronic signature to approve: shows what is signed, then leaves for the password', async () => {
      const { fixture, el } = await render(draft({ status: 'SUBMITTED' }), ['wf.definition:approve', 'esig.sign']);
      button(el, 'Approve and sign…')!.click();
      await fixture.whenStable();
      expect(el.querySelector('[role="dialog"]')?.textContent).toContain('CAPA_STD v2');
      (el.querySelector('.ack input') as HTMLInputElement).click();
      await fixture.whenStable();
      button(el, 'Continue to sign')!.click();
      const req = http.expectOne('/api/esig/v1/challenges');
      expect(req.request.body).toEqual({
        entity: 'wf.definition', recordId: 'd1', recordVersion: 1, meaning: 'APPROVED', reason: null, acknowledge: true, action: 'approve',
      });
      req.flush({ challengeId: 'c1', expiresAt: '2026-10-16T12:02:00Z' });
      expect(navigations).toEqual(['/bff/reauth?returnUrl=%2Fworkflows%2Fd1%3Fesig%3Dc1']);
    });

    it('shows why a signature cannot start, e.g. the second-person rule', async () => {
      const { fixture, el } = await render(draft({ status: 'SUBMITTED' }), ['wf.definition:approve', 'esig.sign']);
      button(el, 'Approve and sign…')!.click();
      await fixture.whenStable();
      (el.querySelector('.ack input') as HTMLInputElement).click();
      await fixture.whenStable();
      button(el, 'Continue to sign')!.click();
      http.expectOne('/api/esig/v1/challenges').flush(
        { title: 'Second person required', detail: 'The person who submitted a workflow cannot approve it.' },
        { status: 403, statusText: 'Forbidden' });
      await fixture.whenStable();
      expect(el.querySelector('[role="dialog"]')).toBeNull();
      expect(el.textContent).toContain('The person who submitted a workflow cannot approve it.');
      expect(navigations).toEqual([]);
    });

    it('completes the signature when Keycloak sends the user back, then reloads the draft and its signature', async () => {
      const { fixture, el, completion } = await render(draft({ status: 'SUBMITTED' }), ['wf.definition:approve', 'esig.sign'], 'c1');
      completion!.flush(signature);
      http.expectOne(draftUrl).flush(draft({ status: 'APPROVED', approvalSignatureId: 'sig1', rowVersion: 3 }), { headers: { ETag: '"3"' } });
      await fixture.whenStable();
      http.expectOne('/api/esig/v1/records/wf.definition/d1/signatures').flush({ currentVersion: 3, items: [signature] });
      await fixture.whenStable();
      expect(el.textContent).toContain('Signed: Approved by Olu Owner, version 2.');
      expect(el.textContent).toContain('Approved');
      expect(button(el, 'Activate')).toBeDefined();
      expect(el.querySelector('pos-signatures')).not.toBeNull();
    });

    it('activates an approved draft that carries its signature', async () => {
      const { fixture, el } = await render(draft({ status: 'APPROVED', approvalSignatureId: 'sig1', rowVersion: 3 }), ['wf.definition:approve']);
      expect(el.querySelector('pos-signatures')).not.toBeNull();
      button(el, 'Activate')!.click();
      const req = http.expectOne('/api/workflow/v1/definitions/d1/activate');
      expect(req.request.headers.get('If-Match')).toBe('"3"');
      req.flush(draft({ status: 'ACTIVE', approvalSignatureId: 'sig1', rowVersion: 4 }), { headers: { ETag: '"4"' } });
      await fixture.whenStable();
      expect(el.textContent).toContain('New records start on this version');
    });
  });
});
