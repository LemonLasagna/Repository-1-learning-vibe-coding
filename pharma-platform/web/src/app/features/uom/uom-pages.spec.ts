import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService, NAVIGATE, SessionUser, Signature } from '../../core/api/api';
import { UomDetailPage } from './uom-pages';

const uom = { id: 'u1', code: 'KG', name: 'Kilogram', dimension: 'MASS', rowVersion: 2, updatedAt: '2026-10-15T03:00:00Z' };

const approver: SessionUser = {
  sub: 's2', login: 'qa.approver', name: 'Avery Approver', tenantId: 't1',
  permissions: ['mdm.uom:read', 'esig.sign'], authTime: null, logoutUrl: null,
};

const options = {
  statement: { version: '1', text: 'My electronic signature is legally binding.', acknowledged: false },
  meanings: [
    { code: 'REVIEWED', label: 'Reviewed', statement: 'I have reviewed this record.', requiresReason: false, requiresSecondPerson: false },
    { code: 'APPROVED', label: 'Approved', statement: 'I approve this record for use.', requiresReason: false, requiresSecondPerson: true },
  ],
};

const signature: Signature = {
  id: 'sig1', signerName: 'Avery Approver', signerLogin: 'qa.approver', meaning: 'APPROVED', meaningLabel: 'Approved',
  signedAt: '2026-10-15T12:00:00Z', recordVersion: 2, recordHash: 'a'.repeat(64), reason: null,
  manifest: 'Electronically signed by Avery Approver (qa.approver)', appliesToCurrentVersion: true,
};

describe('UomDetailPage', () => {
  let http: HttpTestingController;
  const navigations: string[] = [];

  beforeEach(() => {
    navigations.length = 0;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'uoms/:id', component: UomDetailPage }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: NAVIGATE, useValue: (url: string) => navigations.push(url) },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function render(user: SessionUser | null = null, esig?: string) {
    TestBed.inject(AuthService).user.set(user);
    const fixture = TestBed.createComponent(UomDetailPage);
    fixture.componentRef.setInput('id', 'u1');
    if (esig) fixture.componentRef.setInput('esig', esig);
    fixture.detectChanges();
    if (user?.permissions.includes('esig.sign')) http.expectOne('/api/esig/v1/meanings?entity=mdm.uom').flush(options);
    const completion = esig ? http.expectOne(`/api/esig/v1/challenges/${esig}/complete`) : null;
    http.expectOne('/api/mdm/v1/uoms/u1').flush(uom, { headers: { ETag: '"2"' } });
    await fixture.whenStable();
    http.expectOne('/api/esig/v1/records/mdm.uom/u1/signatures').flush({ currentVersion: 2, items: [] });
    http.expectOne('/api/audit/v1/records/mdm/uom/u1').flush([]);
    await fixture.whenStable();
    return { fixture, cmp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, completion };
  }

  const button = (el: HTMLElement, text: string) =>
    Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.includes(text)) as HTMLButtonElement | undefined;

  it('cannot save without a reason for change', async () => {
    const { cmp } = await render();
    cmp.editForm.controls.name.setValue('Kilogramme');
    cmp.editForm.controls.name.markAsDirty();
    expect(cmp.editForm.invalid).toBe(true);
    cmp.editForm.controls.reason.setValue('   ');
    expect(cmp.editForm.invalid).toBe(true);
  });

  it('sends the ETag it read and shows a reload action when someone else saved first', async () => {
    const { fixture, cmp, el } = await render();
    cmp.editForm.patchValue({ name: 'Kilogramme', reason: 'Spelling' });
    cmp.save();
    const req = http.expectOne('/api/mdm/v1/uoms/u1');
    expect(req.request.headers.get('If-Match')).toBe('"2"');
    req.flush({ title: 'Stale version', detail: 'The record was changed by someone else.' }, { status: 412, statusText: 'Precondition Failed' });
    await fixture.whenStable();

    expect(el.textContent).toContain('changed by someone else');
    expect(button(el, 'Reload')).toBeDefined();
  });

  it('offers no signing without the esig.sign permission', async () => {
    const { el } = await render({ ...approver, permissions: ['mdm.uom:read'] });
    expect(button(el, 'Sign as')).toBeUndefined();
  });

  it('starts a challenge for the shown version and sends the user to Keycloak to confirm the password', async () => {
    const { fixture, el } = await render(approver);
    button(el, 'Sign as Approved')!.click();
    await fixture.whenStable();
    expect(el.querySelector('[role="dialog"]')?.textContent).toContain('KG (version 2)');

    (el.querySelector('.ack input') as HTMLInputElement).click();
    await fixture.whenStable();
    button(el, 'Continue to sign')!.click();
    const req = http.expectOne('/api/esig/v1/challenges');
    expect(req.request.body).toEqual({
      entity: 'mdm.uom', recordId: 'u1', recordVersion: 2, meaning: 'APPROVED', reason: null, acknowledge: true,
    });
    req.flush({ challengeId: 'c1', expiresAt: '2026-10-15T12:02:00Z' });
    expect(navigations).toEqual(['/bff/reauth?returnUrl=%2Fuoms%2Fu1%3Fesig%3Dc1']);
  });

  it('shows why a signature cannot start, e.g. segregation of duties', async () => {
    const { fixture, el } = await render(approver);
    button(el, 'Sign as Approved')!.click();
    await fixture.whenStable();
    (el.querySelector('.ack input') as HTMLInputElement).click();
    await fixture.whenStable();
    button(el, 'Continue to sign')!.click();
    http.expectOne('/api/esig/v1/challenges').flush(
      { title: 'Segregation of duties', detail: 'Another person must sign.' }, { status: 403, statusText: 'Forbidden' });
    await fixture.whenStable();
    expect(el.querySelector('[role="dialog"]')).toBeNull();
    expect(el.textContent).toContain('Another person must sign.');
    expect(navigations).toEqual([]);
  });

  it('completes the signature when Keycloak sends the user back, then reloads the signatures', async () => {
    const { fixture, el, completion } = await render(approver, 'c1');
    completion!.flush(signature);
    await fixture.whenStable();
    expect(el.textContent).toContain('Signed: Approved by Avery Approver, version 2.');
    http.expectOne('/api/esig/v1/records/mdm.uom/u1/signatures').flush({ currentVersion: 2, items: [signature] });
    await fixture.whenStable();
    expect(el.querySelector('pos-signatures')?.textContent).toContain('current version');
  });
});
