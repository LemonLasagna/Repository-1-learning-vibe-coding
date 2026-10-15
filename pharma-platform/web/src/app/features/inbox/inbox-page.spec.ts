import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService, SessionUser } from '../../core/api/api';
import { Inbox, InboxApi } from '../../core/api/inbox-api';
import { InboxPage } from './inbox-page';

const qa: SessionUser = {
  sub: 'u2', login: 'qa.approver', name: 'Avery Approver', tenantId: 't1', permissions: ['qms.capa:approve'], authTime: null, logoutUrl: null,
};

const inbox: Inbox = {
  items: [
    { entity: 'qms.deviation', recordId: 'd1', number: 'DEV-2026-00001', title: 'Misprint', state: 'TRIAGE', task: 'Triage', kind: 'DO',
      detail: null, dueDate: '2026-09-01', overdue: true },
    { entity: 'qms.capa', recordId: 'c1', number: 'CAPA-2026-00002', title: 'Fix labeller', state: 'PLAN_REVIEW', task: 'Review and approve the plan',
      kind: 'SIGN', detail: null, dueDate: '2027-01-01', overdue: false },
    { entity: 'qms.deviation', recordId: 'd2', number: 'DEV-2026-00003', title: 'Glove tear', state: 'QA_APPROVAL', task: 'Approve and close',
      kind: 'WAITING', detail: 'Waiting for 1 linked CAPA plan(s) to be approved', dueDate: '2026-11-01', overdue: false },
  ],
  counts: { total: 3, overdue: 1, actionable: 2 },
};

describe('InboxPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists what is waiting, links each record, marks overdue and waiting items, and keeps the count', async () => {
    TestBed.inject(AuthService).user.set(qa);
    const fixture = TestBed.createComponent(InboxPage);
    fixture.detectChanges();
    http.expectOne('/api/work/v1/inbox').flush(inbox);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const rows = Array.from(el.querySelectorAll('li'));
    expect(rows).toHaveLength(3);
    expect(rows[0].classList).toContain('overdue');
    expect(rows[0].textContent).toContain('overdue');
    expect(rows[1].querySelector('a')?.getAttribute('href')).toBe('/capas/c1');
    expect(rows[1].textContent).toContain('Sign ✍');
    expect(rows[2].classList).toContain('waiting');
    expect(rows[2].textContent).toContain('Waiting for 1 linked CAPA plan(s) to be approved');
    expect(TestBed.inject(InboxApi).counts()).toEqual({ total: 3, overdue: 1, actionable: 2 });
  });

  it('says so when nothing is waiting, and asks signed-out users to sign in without calling the API', async () => {
    TestBed.inject(AuthService).user.set(qa);
    const fixture = TestBed.createComponent(InboxPage);
    fixture.detectChanges();
    http.expectOne('/api/work/v1/inbox').flush({ items: [], counts: { total: 0, overdue: 0, actionable: 0 } });
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Nothing is waiting for you.');

    TestBed.inject(AuthService).user.set(null);
    const signedOut = TestBed.createComponent(InboxPage);
    signedOut.detectChanges();
    await signedOut.whenStable();
    expect((signedOut.nativeElement as HTMLElement).textContent).toContain('Sign in to see what is waiting for you.');
  });
});
