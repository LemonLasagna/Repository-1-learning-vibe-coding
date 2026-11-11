import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthService, NAVIGATE, SessionUser, Signature } from '../../core/api/api';
import { ConfigPackage, DOWNLOAD, importRefusal } from '../../core/api/config-package-api';
import { CurrentWorkflow } from '../../core/api/workflow-api';
import { PackageDetailPage, PackageListPage } from './package-pages';

const HASH = 'ab'.repeat(32);

const pkg = (over: Partial<ConfigPackage> = {}): ConfigPackage => ({
  id: 'p1', code: 'WF-2026-01', name: 'Workflow release, Q4', versionLabel: '1.0', sourceEnvironment: 'CONFIG', contentHash: HASH,
  status: 'DRAFT', manifest: { format: 'pharmaos-config-package/1', platformVersion: '0.9.0', items: [{ artifactType: 'WORKFLOW', code: 'CAPA_STD', version: 2 }] },
  approvedBy: null, approvedAt: null, approvalSignatureId: null, signingKeyVersion: null, exportedAt: null, importLog: null, rowVersion: 1,
  updatedAt: '2026-11-10T10:00:00Z', discardedBy: null, discardedAt: null, discardReason: null, items: [{ artifactType: 'WORKFLOW', code: 'CAPA_STD', version: 2 }], deployments: [], ...over,
});

const workflow = (over: Partial<CurrentWorkflow> = {}): CurrentWorkflow => ({
  code: 'CAPA_STD', recordType: 'CAPA', name: 'CAPA (standard)', version: 2, initialState: 'DRAFT', transitions: [], releaseVersion: 1,
  releaseNotApplied: false, ...over,
});

const user = (...permissions: string[]): SessionUser => ({
  sub: 's1', login: 'o.owner', name: 'Olu Owner', tenantId: 't1', permissions, authTime: null, logoutUrl: null,
});

const options = {
  statement: { version: '1', text: 'My electronic signature is legally binding.', acknowledged: false },
  meanings: [
    { code: 'APPROVED', label: 'Approved', statement: 'I approve this record for use.', requiresReason: false, requiresSecondPerson: true },
    { code: 'RELEASED', label: 'Released', statement: 'I release this record for use.', requiresReason: false, requiresSecondPerson: false },
  ],
};

const signed = (meaning: string, label: string): Signature => ({
  id: 'sig1', signerName: 'Olu Owner', signerLogin: 'o.owner', meaning, meaningLabel: label, signedAt: '2026-11-10T12:00:00Z',
  recordVersion: 2, recordHash: 'a'.repeat(64), reason: null, manifest: 'Electronically signed by Olu Owner (o.owner)',
  appliesToCurrentVersion: true,
});

const button = (el: HTMLElement, text: string) =>
  Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === text) as HTMLButtonElement | undefined;

function type(el: HTMLElement, selector: string, value: string): void {
  const input = el.querySelector(selector) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('Package pages', () => {
  let http: HttpTestingController;
  const navigations: string[] = [];
  const downloads: { name: string; text: string }[] = [];

  beforeEach(() => {
    navigations.length = 0;
    downloads.length = 0;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: NAVIGATE, useValue: (url: string) => navigations.push(url) },
        { provide: DOWNLOAD, useValue: (name: string, text: string) => downloads.push({ name, text }) },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  describe('PackageListPage', () => {
    async function render(packages: ConfigPackage[] = [], workflows: CurrentWorkflow[] = [workflow()], production = false) {
      const fixture = TestBed.createComponent(PackageListPage);
      fixture.detectChanges();
      http.expectOne('/api/config/v1/environment').flush({ name: production ? 'PROD' : 'CONFIG', production });
      http.expectOne('/api/config/v1/packages').flush({ items: packages });
      http.expectOne('/api/workflow/v1/workflows').flush({ items: workflows });
      await fixture.whenStable();
      fixture.detectChanges();
      return { fixture, cmp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
    }

    it('lists packages with their status and the environment this installation is', async () => {
      const { el } = await render([pkg({ status: 'IMPORTED', sourceEnvironment: 'CONFIG' })]);

      expect(el.textContent).toContain('This installation is CONFIG');
      const row = el.querySelector('table[aria-label="Packages"] tbody tr')!;
      expect(row.textContent).toContain('WF-2026-01');
      expect(row.textContent).toContain('Imported, ready to deploy');
      expect(row.querySelector('a')!.getAttribute('href')).toBe('/packages/p1');
    });

    it('says in production that workflows change only by deploying a package', async () => {
      const { el } = await render([], [workflow()], true);
      expect(el.textContent).toContain('This installation is PROD');
      expect(el.textContent).toContain('Workflows here change only by deploying a package');
    });

    it('builds a package from the ticked workflows and opens it', async () => {
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
      const { fixture, el } = await render();
      expect(button(el, 'Build package')!.disabled).toBe(true);

      type(el, 'input[formControlName="code"]', 'WF-2026-01');
      type(el, 'input[formControlName="name"]', 'Workflow release, Q4');
      type(el, 'input[formControlName="versionLabel"]', '1.0');
      expect(button(el, 'Build package')!.disabled).toBe(true); // nothing ticked yet
      (el.querySelector('fieldset input[type="checkbox"]') as HTMLInputElement).click();
      fixture.detectChanges();
      expect(button(el, 'Build package')!.disabled).toBe(false);

      button(el, 'Build package')!.click();
      const build = http.expectOne('/api/config/v1/packages');
      expect(build.request.method).toBe('POST');
      expect(build.request.body).toEqual({
        code: 'WF-2026-01', name: 'Workflow release, Q4', versionLabel: '1.0', items: [{ artifactType: 'WORKFLOW', code: 'CAPA_STD', version: 2 }],
      });
      build.flush(pkg(), { status: 201, statusText: 'Created' });
      await fixture.whenStable();
      expect(navigate).toHaveBeenCalledWith(['/packages', 'p1']);
    });

    it('does not allow a badly formed code', async () => {
      const { fixture, el } = await render();
      type(el, 'input[formControlName="code"]', 'lower case');
      type(el, 'input[formControlName="name"]', 'n');
      type(el, 'input[formControlName="versionLabel"]', '1');
      (el.querySelector('fieldset input[type="checkbox"]') as HTMLInputElement).click();
      fixture.detectChanges();

      expect(button(el, 'Build package')!.disabled).toBe(true);
    });

    it('shows why a package could not be built, for example a version that is not released', async () => {
      const { fixture, el } = await render();
      type(el, 'input[formControlName="code"]', 'WF-2026-01');
      type(el, 'input[formControlName="name"]', 'n');
      type(el, 'input[formControlName="versionLabel"]', '1');
      (el.querySelector('fieldset input[type="checkbox"]') as HTMLInputElement).click();
      fixture.detectChanges();

      button(el, 'Build package')!.click();
      http.expectOne('/api/config/v1/packages').flush(
        { title: 'Not released', detail: 'Only released workflow versions can be packaged. Not released here: CAPA_STD version 2.' },
        { status: 422, statusText: 'Unprocessable Entity' });
      await fixture.whenStable();
      fixture.detectChanges();

      expect(el.querySelector('.banner.error')!.textContent).toContain('Only released workflow versions can be packaged');
      expect(button(el, 'Build package')!.disabled).toBe(false);
    });

    it('imports a package file and opens the package; the server does the checking', async () => {
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
      const { fixture, cmp } = await render();
      const file = JSON.stringify({ format: 'pharmaos-config-package/1', package: { code: 'WF-2026-01' }, contentHash: HASH });

      cmp.importText(file);
      const req = http.expectOne('/api/config/v1/packages/import');
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual(JSON.parse(file));
      req.flush(pkg({ status: 'IMPORTED' }), { status: 201, statusText: 'Created' });
      await fixture.whenStable();

      expect(navigate).toHaveBeenCalledWith(['/packages', 'p1']);
    });

    it('lists every reason an import was refused, with the dry run, and stores nothing', async () => {
      const { fixture, cmp, el } = await render();

      cmp.importText('{"format":"pharmaos-config-package/1"}');
      http.expectOne('/api/config/v1/packages/import').flush(
        {
          title: 'Package refused', detail: 'The package was not imported. Nothing was stored.', code: 'PACKAGE_REFUSED',
          problems: ['UNTRUSTED_SIGNER: the signature is not valid under any key this installation trusts for key version 1.'],
          items: [{ code: 'CAPA_STD', version: 2, outcome: 'REFUSED', problems: ['x'] }],
        },
        { status: 422, statusText: 'Unprocessable Entity' });
      await fixture.whenStable();
      fixture.detectChanges();

      const refused = el.querySelector('[aria-label="Import refused"]')!;
      expect(refused.textContent).toContain('Nothing was stored');
      expect(refused.textContent).toContain('UNTRUSTED_SIGNER');
      expect(refused.querySelector('table')!.textContent).toContain('Refused');
    });

    it('refuses a file that is not JSON without sending anything', async () => {
      const { fixture, cmp, el } = await render();

      cmp.importText('this is not json');
      await fixture.whenStable();
      fixture.detectChanges();

      expect(el.querySelector('.banner.error')!.textContent).toContain('not valid JSON');
      http.expectNone('/api/config/v1/packages/import');
    });

    it('shows other import errors as messages', async () => {
      const { fixture, cmp, el } = await render();

      cmp.importText('{}');
      http.expectOne('/api/config/v1/packages/import').flush(
        { title: 'Package already here', detail: 'A package with code WF-2026-01 already exists in this installation.' },
        { status: 409, statusText: 'Conflict' });
      await fixture.whenStable();
      fixture.detectChanges();

      expect(el.querySelector('.banner.error')!.textContent).toContain('already exists in this installation');
    });
  });

  describe('importRefusal', () => {
    it('reads the reasons only from a refusal the server marks as such', () => {
      const refused = new HttpErrorResponse({ status: 422, error: { code: 'PACKAGE_REFUSED', problems: ['a'], items: [] } });
      expect(importRefusal(refused)).toEqual({ problems: ['a'], items: [] });
      expect(importRefusal(new HttpErrorResponse({ status: 422, error: { code: 'OTHER' } }))).toBeNull();
      expect(importRefusal(new HttpErrorResponse({ status: 500, error: { code: 'PACKAGE_REFUSED' } }))).toBeNull();
    });
  });

  describe('PackageDetailPage', () => {
    const url = '/api/config/v1/packages/p1';

    /** Renders the package page and flushes what it requests on opening. */
    async function render(p: ConfigPackage, permissions: string[], esig?: string) {
      vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
      TestBed.inject(AuthService).user.set(user('cfg.package:author', ...permissions));
      const fixture = TestBed.createComponent(PackageDetailPage);
      fixture.componentRef.setInput('id', p.id);
      if (esig) fixture.componentRef.setInput('esig', esig);
      fixture.detectChanges();
      http.expectOne(url).flush(p);
      if (permissions.includes('esig.sign')) http.expectOne('/api/esig/v1/meanings?entity=meta.config_package').flush(options);
      const completion = esig ? http.expectOne(`/api/esig/v1/challenges/${esig}/complete`) : null;
      await fixture.whenStable();
      if (p.approvalSignatureId || p.status === 'DEPLOYED')
        http.expectOne('/api/esig/v1/records/meta.config_package/p1/signatures').flush({ currentVersion: p.rowVersion, items: [signed('APPROVED', 'Approved')] });
      await fixture.whenStable();
      fixture.detectChanges();
      return { fixture, cmp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, completion };
    }

    /** Opens the signing dialog with the button, confirms the acknowledgement and continues. Returns the challenge request. */
    async function startSigning(fixture: { whenStable(): Promise<unknown>; detectChanges(): void }, el: HTMLElement, label: string) {
      button(el, label)!.click();
      await fixture.whenStable();
      fixture.detectChanges();
      (el.querySelector('.ack input') as HTMLInputElement).click();
      await fixture.whenStable();
      fixture.detectChanges();
      button(el, 'Continue to sign')!.click();
      return http.expectOne('/api/esig/v1/challenges');
    }

    it('shows what the package contains, its hash and where it stands', async () => {
      const { el } = await render(pkg(), []);

      expect(el.querySelector('h1')!.textContent).toContain('WF-2026-01');
      expect(el.querySelector('h1')!.textContent).toContain('Built, waiting for approval');
      expect(el.querySelector('.hash')!.textContent).toBe(HASH);
      expect(el.querySelector('table[aria-label="Items"]')!.textContent).toContain('CAPA_STD');
      expect(el.textContent).toContain('Built in');
      expect(el.textContent).toContain('CONFIG');
    });

    it('asks for an electronic signature to approve: shows what is signed, then leaves for the password', async () => {
      const { fixture, el } = await render(pkg(), ['cfg.package:approve', 'esig.sign']);

      const challenge = await startSigning(fixture, el, 'Approve and sign…');
      expect(challenge.request.body).toEqual({
        entity: 'meta.config_package', recordId: 'p1', recordVersion: 1, meaning: 'APPROVED', reason: null, acknowledge: true, action: 'approve',
      });
      challenge.flush({ challengeId: 'ch1' });
      await fixture.whenStable();

      expect(navigations).toEqual(['/bff/reauth?returnUrl=%2Fpackages%2Fp1%3Fesig%3Dch1']);
    });

    it('records the approval when the user returns from the password step', async () => {
      const { fixture, el, completion } = await render(pkg(), ['cfg.package:approve', 'esig.sign'], 'ch1');
      completion!.flush({ ...signed('APPROVED', 'Approved'), meaning: 'APPROVED' });
      await fixture.whenStable();
      http.expectOne(url).flush(pkg({ status: 'APPROVED', approvalSignatureId: 'sig1', signingKeyVersion: 1, rowVersion: 2 }));
      await fixture.whenStable();
      http.expectOne('/api/esig/v1/records/meta.config_package/p1/signatures').flush({ currentVersion: 2, items: [signed('APPROVED', 'Approved')] });
      await fixture.whenStable();
      fixture.detectChanges();

      expect(el.textContent).toContain('Signed: Approved by Olu Owner');
      expect(el.textContent).toContain('can be exported');
      expect(el.textContent).toContain('Signed with key version');
    });

    it('does not offer approval without the approve permission, and says signing needs its own', async () => {
      const { el } = await render(pkg(), ['esig.sign']);
      expect(button(el, 'Approve and sign…')).toBeUndefined();

      const second = await render(pkg(), ['cfg.package:approve']);
      expect(button(second.el, 'Approve and sign…')).toBeUndefined();
      expect(second.el.textContent).toContain('Signing needs the electronic signature permission');
    });

    it('exports an approved package as a file and notes that the other installation must trust the key', async () => {
      const { fixture, el } = await render(pkg({ status: 'APPROVED', approvalSignatureId: 'sig1', signingKeyVersion: 1, rowVersion: 2 }), []);
      expect(button(el, 'Approve and sign…')).toBeUndefined();

      button(el, 'Export package file')!.click();
      const exported = http.expectOne(`${url}/export`);
      expect(exported.request.method).toBe('POST');
      exported.flush('{"format":"pharmaos-config-package/1"}');
      await fixture.whenStable();
      http.expectOne(url).flush(pkg({ status: 'APPROVED', exportedAt: '2026-11-10T11:00:00Z', approvalSignatureId: 'sig1', rowVersion: 3 }));
      await fixture.whenStable();
      fixture.detectChanges();

      expect(downloads).toEqual([{ name: 'WF-2026-01.pharmaos-package.json', text: '{"format":"pharmaos-config-package/1"}' }]);
      expect(el.textContent).toContain('must trust this installation’s signing key');
      expect(el.textContent).toContain('Exported');
    });

    it('shows how an imported package fared in the dry run and offers to deploy it with a signature', async () => {
      const imported = pkg({
        status: 'IMPORTED', signingKeyVersion: 1, items: undefined,
        importLog: { trustedAs: 'config-env', importedAt: '2026-11-10T10:30:00Z', items: [{ code: 'CAPA_STD', version: 2, outcome: 'ADD' }] },
      });
      const { fixture, el } = await render(imported, ['cfg.package:deploy', 'esig.sign']);

      expect(el.textContent).toContain('valid under: config-env');
      expect(el.querySelector('table[aria-label="Dry run"]')!.textContent).toContain('Will be added');
      expect(el.querySelector('table[aria-label="Items"]')!.textContent).toContain('CAPA_STD'); // from the manifest

      const challenge = await startSigning(fixture, el, 'Deploy and sign…');
      expect(challenge.request.body).toMatchObject({ entity: 'meta.config_package', recordId: 'p1', meaning: 'RELEASED', action: 'deploy' });
      challenge.flush({ challengeId: 'ch2' });
      await fixture.whenStable();
      expect(navigations).toEqual(['/bff/reauth?returnUrl=%2Fpackages%2Fp1%3Fesig%3Dch2']);
    });

    it('does not offer to deploy without the deploy permission', async () => {
      const { el } = await render(pkg({ status: 'IMPORTED' }), ['cfg.package:approve', 'esig.sign']);

      expect(button(el, 'Deploy and sign…')).toBeUndefined();
      expect(el.textContent).toContain('Deploying needs the package deploy permission');
    });

    it('records the deployment when the user returns from the password step and shows it', async () => {
      const { fixture, el, completion } = await render(pkg({ status: 'IMPORTED' }), ['cfg.package:deploy', 'esig.sign'], 'ch2');
      completion!.flush({ ...signed('RELEASED', 'Released'), meaning: 'RELEASED' });
      await fixture.whenStable();
      http.expectOne(url).flush(pkg({
        status: 'DEPLOYED', rowVersion: 3,
        deployments: [{ id: 'd1', targetEnvironment: 'VAL', deployedAt: '2026-11-10T12:00:00Z', deployedBy: 's1', result: 'DEPLOYED', log: 'CAPA_STD version 2: ADD' }],
      }));
      await fixture.whenStable();
      http.expectOne('/api/esig/v1/records/meta.config_package/p1/signatures').flush({ currentVersion: 3, items: [signed('RELEASED', 'Released')] });
      await fixture.whenStable();
      fixture.detectChanges();

      expect(el.textContent).toContain('Signed: Released by Olu Owner');
      expect(el.textContent).toContain('new records start on its workflows');
      expect(el.querySelector('table[aria-label="Deployments"]')!.textContent).toContain('CAPA_STD version 2: ADD');
      expect(button(el, 'Deploy and sign…')).toBeUndefined();
    });

    it('discards an imported package with a reason, keeps it, and offers no way to deploy it afterwards', async () => {
      const { fixture, el } = await render(pkg({ status: 'IMPORTED', rowVersion: 4 }), ['cfg.package:deploy', 'esig.sign']);
      expect(button(el, 'Discard package')!.disabled).toBe(true); // a reason is required

      type(el, 'input[maxlength="1000"]', '  Wrong release for this environment ');
      fixture.detectChanges();
      expect(button(el, 'Discard package')!.disabled).toBe(false);
      button(el, 'Discard package')!.click();

      const req = http.expectOne(`${url}/discard`);
      expect(req.request.method).toBe('POST');
      expect(req.request.headers.get('If-Match')).toBe('"4"');
      expect(req.request.body).toEqual({ comment: 'Wrong release for this environment' });
      req.flush(pkg({
        status: 'DISCARDED', rowVersion: 5, discardedBy: 's1', discardedAt: '2026-11-10T13:00:00Z', discardReason: 'Wrong release for this environment',
      }));
      await fixture.whenStable();
      fixture.detectChanges();

      expect(el.querySelector('h1')!.textContent).toContain('Discarded');
      expect(el.querySelector('[aria-label="Discarded"]')!.textContent).toContain('Wrong release for this environment');
      expect(el.querySelector('.banner.ok')!.textContent).toContain('kept for the record');
      expect(el.querySelector('.banner.ok')!.textContent).toContain('code can be used again');
      expect(button(el, 'Deploy and sign…')).toBeUndefined();
      expect(button(el, 'Discard package')).toBeUndefined();
    });

    it('discards a package that was built but never approved, for anyone who may build packages', async () => {
      const { fixture, el } = await render(pkg({ status: 'DRAFT', rowVersion: 2 }), []); // only the author permission
      expect(button(el, 'Discard package')!.disabled).toBe(true);

      type(el, 'input[maxlength="1000"]', 'Built from the wrong versions');
      fixture.detectChanges();
      button(el, 'Discard package')!.click();
      const req = http.expectOne(`${url}/discard`);
      expect(req.request.headers.get('If-Match')).toBe('"2"');
      expect(req.request.body).toEqual({ comment: 'Built from the wrong versions' });
      req.flush(pkg({ status: 'DISCARDED', rowVersion: 3, discardedBy: 's1', discardedAt: '2026-11-10T13:00:00Z', discardReason: 'Built from the wrong versions' }));
      await fixture.whenStable();
      fixture.detectChanges();

      expect(el.querySelector('[aria-label="Discarded"]')!.textContent).toContain('Built from the wrong versions');
      expect(button(el, 'Discard package')).toBeUndefined();
    });

    it('does not offer to discard an approved package', async () => {
      const { el } = await render(pkg({ status: 'APPROVED', approvalSignatureId: 'sig1', signingKeyVersion: 1 }), ['cfg.package:approve', 'cfg.package:deploy', 'esig.sign']);
      expect(button(el, 'Discard package')).toBeUndefined();
      expect(el.querySelector('input[maxlength="1000"]')).toBeNull();
    });

    it('does not offer to discard without the deploy permission', async () => {
      const { el } = await render(pkg({ status: 'IMPORTED' }), ['cfg.package:approve', 'esig.sign']);

      expect(button(el, 'Discard package')).toBeUndefined();
      expect(el.querySelector('input[maxlength="1000"]')).toBeNull();
    });

    it('shows why a discard was refused, for example a package someone else already changed', async () => {
      const { fixture, el } = await render(pkg({ status: 'IMPORTED' }), ['cfg.package:deploy']);
      type(el, 'input[maxlength="1000"]', 'no longer needed');
      fixture.detectChanges();

      button(el, 'Discard package')!.click();
      http.expectOne(`${url}/discard`).flush(
        { title: 'Stale version', detail: 'The package was changed by someone else. Reload it and review the changes.' },
        { status: 412, statusText: 'Precondition Failed' });
      await fixture.whenStable();
      fixture.detectChanges();

      expect(el.querySelector('.banner.error')!.textContent).toContain('changed by someone else');
      expect(el.querySelector('h1')!.textContent).toContain('Imported');
    });

    it('shows a discarded package with its reason and no steps to take', async () => {
      const { el } = await render(
        pkg({ status: 'DISCARDED', discardedBy: 's1', discardedAt: '2026-11-10T13:00:00Z', discardReason: 'Superseded by WF-2026-02' }),
        ['cfg.package:deploy', 'cfg.package:approve', 'esig.sign']);

      expect(el.querySelector('[aria-label="Discarded"]')!.textContent).toContain('Superseded by WF-2026-02');
      for (const label of ['Deploy and sign…', 'Approve and sign…', 'Export package file', 'Discard package']) expect(button(el, label)).toBeUndefined();
    });

    it('shows the server’s reason when the signature step is refused', async () => {
      const { fixture, el } = await render(pkg(), ['cfg.package:approve', 'esig.sign']);

      const challenge = await startSigning(fixture, el, 'Approve and sign…');
      challenge.flush(
        { title: 'Signing key unavailable', detail: 'The package cannot be signed now: OpenBao is sealed. Nothing was changed.' },
        { status: 503, statusText: 'Service Unavailable' });
      await fixture.whenStable();
      fixture.detectChanges();

      expect(el.querySelector('.banner.error')!.textContent).toContain('OpenBao is sealed');
    });
  });
});
