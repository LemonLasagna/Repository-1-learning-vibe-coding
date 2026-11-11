import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService, describeError, EsigApi, formatTimestamp, SigningOptions } from '../../core/api/api';
import {
  AFTER_PACKAGE_SIGNATURE, ConfigPackage, ConfigPackageApi, DOWNLOAD, ImportRefusal, importRefusal, PACKAGE_ENTITY,
  PACKAGE_STATUS_LABELS, SIGNED_PACKAGE_STEPS, SignedPackageStep,
} from '../../core/api/config-package-api';
import { CurrentWorkflow, WorkflowApi } from '../../core/api/workflow-api';
import { EsigDialog, SignConfirmation, SignIntent } from '../../shared/esig-dialog';
import { Signatures } from '../../shared/signatures';

/** What a deployment of one item will do, in words. */
const OUTCOMES: Record<string, string> = {
  ADD: 'Will be added',
  ALREADY_RELEASED: 'Already released here, unchanged',
  REFUSED: 'Refused',
};

/**
 * Configuration packages: build one from released workflow versions, import one that came from another installation, and open any of
 * them. Importing deploys nothing; a package takes effect only when it is deployed from its own page.
 */
@Component({
  selector: 'pos-package-list',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <h1>Configuration packages</h1>
    @if (environment(); as env) {
      <p class="hint">This installation is <strong>{{ env.name }}</strong>.@if (env.production) { Workflows here change only by deploying a package.}</p>
    }
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}

    <h2>Packages</h2>
    @if (packages() === null) {
      <p>Loading…</p>
    } @else {
      <table aria-label="Packages">
        <thead><tr><th>Package</th><th>Name</th><th>Label</th><th>From</th><th>Status</th><th>Updated</th></tr></thead>
        <tbody>
          @for (p of packages(); track p.id) {
            <tr>
              <td><a [routerLink]="['/packages', p.id]"><code>{{ p.code }}</code></a></td>
              <td>{{ p.name }}</td>
              <td>{{ p.versionLabel }}</td>
              <td>{{ p.sourceEnvironment }}</td>
              <td>{{ statusLabels[p.status] }}</td>
              <td>{{ time(p.updatedAt) }}</td>
            </tr>
          } @empty {
            <tr><td colspan="6">No packages yet.</td></tr>
          }
        </tbody>
      </table>
    }

    <h2>Build a package</h2>
    <p class="hint">
      A package carries released workflow versions. Choose the ones to include; only a version that has been released here can be
      packaged. It is signed when QA approves it.
    </p>
    <form [formGroup]="form" (ngSubmit)="build()" class="build">
      <label>Package code <input formControlName="code" maxlength="50" placeholder="e.g. WF-2026-01" autocomplete="off" /></label>
      <label>Name <input formControlName="name" maxlength="255" placeholder="e.g. Workflow release, Q4" /></label>
      <label>Label <input formControlName="versionLabel" maxlength="50" placeholder="e.g. 1.0" autocomplete="off" /></label>
      <fieldset>
        <legend>Workflows (current version)</legend>
        @for (w of workflows(); track w.code) {
          <label class="check">
            <input type="checkbox" [checked]="isSelected(w)" (change)="toggle(w, $any($event.target).checked)" />
            {{ w.name }} (<code>{{ w.code }}</code>) version {{ w.version }}
          </label>
        } @empty {
          <p class="hint">No workflows listed.</p>
        }
      </fieldset>
      <div class="steps">
        <button type="submit" [disabled]="busy() || form.invalid || selectedCount() === 0">Build package</button>
      </div>
    </form>

    <h2>Import a package</h2>
    <p class="hint">
      Choose a package file exported from another installation. It is accepted only if its hash matches its content, its signature is
      valid under a key this installation trusts, and every item passes a dry run. Nothing is deployed by importing.
    </p>
    <label>Package file <input type="file" accept=".json,application/json" (change)="onFile($event)" [disabled]="busy()" /></label>
    @if (refusal(); as r) {
      <section class="banner error" aria-label="Import refused">
        <strong>The package was not imported. Nothing was stored.</strong>
        <ul>@for (p of r.problems; track $index) {<li>{{ p }}</li>}</ul>
        @if (r.items.length) {
          <table aria-label="Dry run">
            <thead><tr><th>Item</th><th>Version</th><th>Result</th></tr></thead>
            <tbody>
              @for (i of r.items; track $index) {
                <tr><td><code>{{ i.code }}</code></td><td>{{ i.version }}</td><td>{{ outcomes[i.outcome] ?? i.outcome }}</td></tr>
              }
            </tbody>
          </table>
        }
      </section>
    }
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; margin: 0 0 24px; }
    th, td { text-align: left; border-bottom: 1px solid var(--pos-border); padding: 6px 8px; }
    label { display: flex; flex-direction: column; font-size: 0.875rem; gap: 4px; margin: 0 0 12px; max-width: 420px; }
    label.check { flex-direction: row; align-items: center; gap: 8px; max-width: none; margin: 0 0 6px; }
    fieldset { border: 1px solid var(--pos-border); margin: 0 0 12px; max-width: 640px; }
    .steps { display: flex; gap: 8px; margin: 12px 0 20px; }
    .hint { color: var(--pos-muted); }
  `,
})
export class PackageListPage implements OnInit {
  private readonly api = inject(ConfigPackageApi);
  private readonly workflowApi = inject(WorkflowApi);
  private readonly router = inject(Router);

  protected readonly statusLabels = PACKAGE_STATUS_LABELS;
  protected readonly outcomes = OUTCOMES;
  protected readonly time = formatTimestamp;
  readonly packages = signal<ConfigPackage[] | null>(null);
  readonly workflows = signal<CurrentWorkflow[]>([]);
  readonly environment = signal<{ name: string; production: boolean } | null>(null);
  readonly error = signal<string | null>(null);
  readonly refusal = signal<ImportRefusal | null>(null);
  readonly busy = signal(false);
  private readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly selectedCount = computed(() => this.selected().size);

  readonly form = new FormGroup({
    code: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(/^[A-Z][A-Z0-9_-]{2,49}$/)] }),
    name: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(255)] }),
    versionLabel: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(50)] }),
  });

  ngOnInit(): void {
    this.api.environment().subscribe({ next: (e) => this.environment.set(e), error: () => this.environment.set(null) });
    this.api.list().subscribe({
      next: (p) => this.packages.set(p),
      error: (e: unknown) => {
        this.packages.set([]);
        this.error.set(describeError(e));
      },
    });
    this.workflowApi.workflows().subscribe({ next: (w) => this.workflows.set(w), error: (e: unknown) => this.error.set(describeError(e)) });
  }

  private key(w: CurrentWorkflow): string {
    return `${w.code}:${w.version}`;
  }

  isSelected(w: CurrentWorkflow): boolean {
    return this.selected().has(this.key(w));
  }

  toggle(w: CurrentWorkflow, on: boolean): void {
    const next = new Set(this.selected());
    if (on) next.add(this.key(w));
    else next.delete(this.key(w));
    this.selected.set(next);
  }

  build(): void {
    if (this.form.invalid) return;
    const { code, name, versionLabel } = this.form.getRawValue();
    const items = this.workflows()
      .filter((w) => this.isSelected(w))
      .map((w) => ({ artifactType: 'WORKFLOW', code: w.code, version: w.version }));
    this.busy.set(true);
    this.error.set(null);
    this.api.build({ code: code.trim(), name: name.trim(), versionLabel: versionLabel.trim(), items }).subscribe({
      next: (p) => void this.router.navigate(['/packages', p.id]),
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(describeError(e));
      },
    });
  }

  onFile(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) return;
    void file.text().then((text) => this.importText(text));
  }

  /** Sends the file's text to the server, which does all the checking. The text is only parsed here to catch a file that is not JSON. */
  importText(text: string): void {
    this.error.set(null);
    this.refusal.set(null);
    try {
      JSON.parse(text);
    } catch {
      this.error.set('That file is not a package: it is not valid JSON. Nothing was imported.');
      return;
    }
    this.busy.set(true);
    this.api.import(text).subscribe({
      next: (p) => void this.router.navigate(['/packages', p.id]),
      error: (e: unknown) => {
        this.busy.set(false);
        const refused = importRefusal(e);
        if (refused) this.refusal.set(refused);
        else this.error.set(describeError(e));
      },
    });
  }
}

/**
 * One package: what it contains and where it stands. Approving and deploying are electronic signatures: the dialog confirms what is
 * signed, Keycloak asks for the password, and the step is recorded together with the signature when the user returns. Export saves
 * the approved package as a file to carry to another installation. Buttons appear only for the steps the package's status and the
 * user's permissions allow; the server checks both again.
 */
@Component({
  selector: 'pos-package-detail',
  imports: [ReactiveFormsModule, RouterLink, EsigDialog, Signatures],
  template: `
    <p><a routerLink="/packages">← Packages</a></p>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    @if (notice()) {<p class="banner ok" role="status">{{ notice() }}</p>}

    @if (pkg(); as p) {
      <h1><code>{{ p.code }}</code> {{ p.versionLabel }} <small>{{ statusLabels[p.status] }}</small></h1>
      <p>{{ p.name }}</p>
      <dl>
        <dt>Built in</dt><dd>{{ p.sourceEnvironment }}</dd>
        <dt>Platform version</dt><dd>{{ p.manifest.platformVersion }}</dd>
        <dt>Content hash (SHA-256)</dt><dd><code class="hash">{{ p.contentHash }}</code></dd>
        @if (p.signingKeyVersion !== null) {<dt>Signed with key version</dt><dd>{{ p.signingKeyVersion }}</dd>}
        @if (p.approvedAt) {<dt>Approved</dt><dd>{{ time(p.approvedAt) }}</dd>}
        @if (p.exportedAt) {<dt>Exported</dt><dd>{{ time(p.exportedAt) }}</dd>}
      </dl>

      <div class="steps">
        @if (p.status === 'DRAFT' && canApprove() && canSign()) {
          <button type="button" [disabled]="busy() || !options()" (click)="startSigning('approve')">{{ steps.approve.button }}</button>
        }
        @if (p.status === 'APPROVED') {
          <button type="button" class="secondary" [disabled]="busy()" (click)="exportFile()">Export package file</button>
        }
        @if (p.status === 'IMPORTED' && canDeploy() && canSign()) {
          <button type="button" [disabled]="busy() || !options()" (click)="startSigning('deploy')">{{ steps.deploy.button }}</button>
        }
        @if (!canSign() && signingNeeded()) {
          <span class="hint">Signing needs the electronic signature permission.</span>
        }
        @if (p.status === 'IMPORTED' && !canDeploy()) {
          <span class="hint">Deploying needs the package deploy permission.</span>
        }
      </div>

      @if (p.status === 'IMPORTED' && canDeploy()) {
        <div class="steps">
          <label class="wide">Reason (required to discard)
            <input [formControl]="comment" maxlength="1000" placeholder="e.g. Wrong release for this environment" />
          </label>
          <button type="button" class="secondary" [disabled]="busy() || !commentText().trim()" (click)="discard()">Discard package</button>
        </div>
        <p class="hint">Discarding keeps the package and the reason for the record. It cannot be deployed afterwards, and its code is not reused.</p>
      }
      @if (p.status === 'DISCARDED') {
        <section class="banner warning" aria-label="Discarded">
          <strong>Discarded</strong> {{ p.discardedAt ? time(p.discardedAt) : '' }}. Reason: {{ p.discardReason }}
        </section>
      }

      @if (p.approvalSignatureId || p.status === 'DEPLOYED') {
        <pos-signatures [entity]="entity" [recordId]="p.id" [refresh]="signatureRefresh()" />
      }
      @if (intent(); as i) {
        <pos-esig-dialog [intent]="i" (confirmed)="confirmSigning($event)" (cancelled)="intent.set(null)" />
      }

      <h2>Contents</h2>
      <table aria-label="Items">
        <thead><tr><th>Type</th><th>Code</th><th>Version</th></tr></thead>
        <tbody>
          @for (i of p.items ?? p.manifest.items; track $index) {
            <tr><td>{{ i.artifactType }}</td><td><code>{{ i.code }}</code></td><td>{{ i.version }}</td></tr>
          }
        </tbody>
      </table>

      @if (p.importLog; as log) {
        <h2>Import</h2>
        <p>Imported {{ time(log.importedAt) }}. The signature is valid under: {{ log.trustedAs }}. Dry run against this installation:</p>
        <table aria-label="Dry run">
          <thead><tr><th>Item</th><th>Version</th><th>On deploy</th></tr></thead>
          <tbody>
            @for (i of log.items; track $index) {
              <tr><td><code>{{ i.code }}</code></td><td>{{ i.version }}</td><td>{{ outcomes[i.outcome] ?? i.outcome }}</td></tr>
            }
          </tbody>
        </table>
      }

      @if (p.deployments?.length) {
        <h2>Deployments</h2>
        <table aria-label="Deployments">
          <thead><tr><th>When</th><th>Where</th><th>Result</th><th>Detail</th></tr></thead>
          <tbody>
            @for (d of p.deployments; track d.id) {
              <tr><td>{{ time(d.deployedAt) }}</td><td>{{ d.targetEnvironment }}</td><td>{{ d.result }}</td><td><pre>{{ d.log }}</pre></td></tr>
            }
          </tbody>
        </table>
      }
    } @else if (!error()) {
      <p>Loading…</p>
    }
  `,
  styles: `
    h1 small { font-size: 0.9rem; font-weight: 400; color: var(--pos-muted); margin-left: 8px; }
    dl { display: grid; grid-template-columns: max-content 1fr; gap: 4px 16px; margin: 0 0 16px; }
    dt { color: var(--pos-muted); }
    dd { margin: 0; }
    .hash { word-break: break-all; font-size: 0.8rem; }
    table { width: 100%; border-collapse: collapse; margin: 0 0 24px; font-size: 0.9rem; }
    th, td { text-align: left; border-bottom: 1px solid var(--pos-border); padding: 4px 6px; vertical-align: top; }
    pre { margin: 0; white-space: pre-wrap; font-size: 0.8rem; }
    .steps { display: flex; flex-wrap: wrap; gap: 8px; align-items: end; margin: 12px 0 20px; }
    label { display: flex; flex-direction: column; font-size: 0.875rem; gap: 4px; margin: 0; }
    .wide { flex: 1 1 320px; }
    .hint { color: var(--pos-muted); }
  `,
})
export class PackageDetailPage implements OnInit {
  private readonly api = inject(ConfigPackageApi);
  private readonly auth = inject(AuthService);
  private readonly esig = inject(EsigApi);
  private readonly router = inject(Router);
  private readonly download = inject(DOWNLOAD);

  readonly id = input.required<string>();
  /** Set when Keycloak sends the user back after the password was confirmed for a signature. */
  readonly esigChallenge = input<string | undefined>(undefined, { alias: 'esig' });
  protected readonly entity = PACKAGE_ENTITY;
  protected readonly statusLabels = PACKAGE_STATUS_LABELS;
  protected readonly outcomes = OUTCOMES;
  protected readonly steps = SIGNED_PACKAGE_STEPS;
  protected readonly time = formatTimestamp;
  readonly options = signal<SigningOptions | null>(null);
  /** The step the open signing dialog is for. */
  private readonly signing = signal<SignedPackageStep>('approve');
  readonly intent = signal<SignIntent | null>(null);
  readonly signatureRefresh = signal(0);
  readonly pkg = signal<ConfigPackage | null>(null);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly busy = signal(false);

  readonly comment = new FormControl('', { nonNullable: true });
  /** The reason as a signal, so the Discard button follows what is typed. */
  readonly commentText = toSignal(this.comment.valueChanges, { initialValue: '' });

  readonly canApprove = computed(() => this.auth.hasPermission('cfg.package:approve'));
  readonly canDeploy = computed(() => this.auth.hasPermission('cfg.package:deploy'));
  readonly canSign = computed(() => this.auth.hasPermission('esig.sign'));
  /** True when the package is at a signed step this user could take, if they could sign. */
  readonly signingNeeded = computed(() => {
    const status = this.pkg()?.status;
    return (status === 'DRAFT' && this.canApprove()) || (status === 'IMPORTED' && this.canDeploy());
  });

  ngOnInit(): void {
    this.load();
    if (this.canSign()) {
      this.esig.options(PACKAGE_ENTITY).subscribe({ next: (o) => this.options.set(o), error: () => this.options.set(null) });
    }
    const challenge = this.esigChallenge();
    if (challenge) this.completeSigning(challenge);
  }

  load(): void {
    this.api.get(this.id()).subscribe({
      next: (p) => {
        this.pkg.set(p);
        this.busy.set(false);
      },
      error: (e: unknown) => this.error.set(describeError(e)),
    });
  }

  /** Shows what is being signed. Nothing is sent until the user confirms. */
  startSigning(step: SignedPackageStep): void {
    const p = this.pkg();
    const user = this.auth.user();
    const options = this.options();
    const meaning = options?.meanings.find((m) => m.code === SIGNED_PACKAGE_STEPS[step].meaning);
    if (!p || !user || !options || !meaning) return;
    this.notice.set(null);
    this.signing.set(step);
    this.intent.set({
      entity: PACKAGE_ENTITY,
      recordNumber: `${p.code} ${p.versionLabel}`,
      recordVersion: p.rowVersion,
      meaningCode: meaning.code,
      meaningLabel: meaning.label,
      meaningStatement: meaning.statement,
      signerName: user.name ?? user.login,
      signerLogin: user.login,
      reasonRequired: meaning.requiresReason,
      acknowledgementText: options.statement.acknowledged ? null : options.statement.text,
    });
  }

  /** Starts the challenge, then leaves for Keycloak, which asks for the password and sends the user back here. */
  confirmSigning(c: SignConfirmation): void {
    const p = this.pkg();
    if (!p) return;
    this.esig
      .challenge({
        entity: PACKAGE_ENTITY, recordId: p.id, recordVersion: p.rowVersion, meaning: SIGNED_PACKAGE_STEPS[this.signing()].meaning,
        reason: c.reason, acknowledge: c.acknowledged, action: this.signing(),
      })
      .subscribe({
        next: ({ challengeId }) => this.auth.reauthenticate(`/packages/${p.id}?esig=${encodeURIComponent(challengeId)}`),
        error: (e: unknown) => {
          this.intent.set(null);
          this.error.set(describeError(e));
        },
      });
  }

  private completeSigning(challengeId: string): void {
    this.esig.complete(challengeId).subscribe({
      next: (s) => {
        this.notice.set(`Signed: ${s.meaningLabel} by ${s.signerName}, version ${s.recordVersion}. ${AFTER_PACKAGE_SIGNATURE[s.meaning] ?? ''}`.trim());
        this.signatureRefresh.update((n) => n + 1);
        this.forgetChallenge();
        this.load();
      },
      error: (e: unknown) => {
        this.error.set(describeError(e));
        this.forgetChallenge();
      },
    });
  }

  /** A reload must not try to use the challenge again. */
  private forgetChallenge(): void {
    void this.router.navigate(['/packages', this.id()], { replaceUrl: true });
  }

  /** Sets an imported package aside. Nothing is deleted; the reason is required and kept. */
  discard(): void {
    const p = this.pkg();
    const reason = this.comment.value.trim();
    if (!p || !reason) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.discard(p.id, p.rowVersion, reason).subscribe({
      next: (updated) => {
        this.pkg.set(updated);
        this.comment.reset('');
        this.busy.set(false);
        this.notice.set('Discarded. Nothing was deployed. The package and your reason are kept for the record.');
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(describeError(e));
      },
    });
  }

  /** Saves the approved package as a file, exactly as the server wrote it, to carry to the other installation. */
  exportFile(): void {
    const p = this.pkg();
    if (!p) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.export(p.id).subscribe({
      next: (text) => {
        this.download(`${p.code}.pharmaos-package.json`, text);
        this.notice.set('Exported. Import the file in the other installation, which must trust this installation’s signing key.');
        this.load();
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(describeError(e));
      },
    });
  }
}
