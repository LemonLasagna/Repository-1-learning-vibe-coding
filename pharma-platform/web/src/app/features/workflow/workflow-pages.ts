import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService, describeError, EsigApi, formatTimestamp, SigningOptions } from '../../core/api/api';
import {
  AFTER_SIGNATURE, CurrentWorkflow, DRAFT_STATUS_LABELS, SIGNED_STEPS, SignedStep, WorkflowApi, WorkflowDraft, WorkflowTransition,
} from '../../core/api/workflow-api';
import { ConfigPackageApi } from '../../core/api/config-package-api';
import { EsigDialog, SignConfirmation, SignIntent } from '../../shared/esig-dialog';
import { Signatures } from '../../shared/signatures';

/** Record types' workflows and the drafts that change them. A new draft starts as a copy of the current workflow. */
@Component({
  selector: 'pos-workflow-list',
  imports: [RouterLink],
  template: `
    <h1>Workflows</h1>
    @if (production()) {
      <p class="banner warning" role="status">
        This is a production environment: workflows are read-only here. They change only by deploying an approved
        <a routerLink="/packages">configuration package</a>.
      </p>
    }
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    @for (w of workflows(); track w.code) {
      @if (w.releaseNotApplied) {
        <p class="banner warning" role="alert">
          The release defines version {{ w.releaseVersion }} of <code>{{ w.code }}</code>, but version {{ w.releaseVersion }} was activated
          from an approved draft and cannot change. The release is not applied: records use the activated version.
          Give the release a higher version number.
        </p>
      }
    }

    <h2>Record types</h2>
    <table aria-label="Record types">
      <thead><tr><th>Record type</th><th>Workflow</th><th>Current version</th><th></th></tr></thead>
      <tbody>
        @for (w of workflows(); track w.code) {
          <tr>
            <td>{{ w.name }}</td>
            <td><code>{{ w.code }}</code></td>
            <td>{{ w.version }}</td>
            <td><button type="button" class="secondary small" [disabled]="busy() || production()" (click)="newDraft(w)">New draft</button></td>
          </tr>
        } @empty {
          <tr><td colspan="4">Loading…</td></tr>
        }
      </tbody>
    </table>

    <h2>Drafts</h2>
    @if (drafts() === null) {
      <p>Loading…</p>
    } @else {
      <table aria-label="Drafts">
        <thead><tr><th>Workflow</th><th>Version</th><th>Status</th><th>Updated</th></tr></thead>
        <tbody>
          @for (d of drafts(); track d.id) {
            <tr>
              <td><a [routerLink]="['/workflows', d.id]"><code>{{ d.code }}</code></a></td>
              <td>{{ d.version }}</td>
              <td>{{ statusLabels[d.status] }}</td>
              <td>{{ time(d.updatedAt) }}</td>
            </tr>
          } @empty {
            <tr><td colspan="4">No drafts yet.</td></tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; margin: 0 0 24px; }
    th, td { text-align: left; border-bottom: 1px solid var(--pos-border); padding: 6px 8px; }
  `,
})
export class WorkflowListPage implements OnInit {
  private readonly api = inject(WorkflowApi);
  private readonly packages = inject(ConfigPackageApi);
  private readonly router = inject(Router);

  protected readonly statusLabels = DRAFT_STATUS_LABELS;
  protected readonly time = formatTimestamp;
  readonly workflows = signal<CurrentWorkflow[]>([]);
  readonly drafts = signal<WorkflowDraft[] | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);
  /** True in a production environment, where workflows change only by deploying a package. */
  readonly production = signal(false);

  ngOnInit(): void {
    this.packages.environment().subscribe({ next: (e) => this.production.set(e.production), error: () => this.production.set(false) });
    this.api.workflows().subscribe({ next: (w) => this.workflows.set(w), error: (e: unknown) => this.error.set(describeError(e)) });
    this.api.list().subscribe({
      next: (d) => this.drafts.set(d),
      error: (e: unknown) => {
        this.drafts.set([]);
        this.error.set(describeError(e));
      },
    });
  }

  /** Copies the current workflow into a draft of the next version. */
  newDraft(w: CurrentWorkflow): void {
    this.busy.set(true);
    this.api.create({ code: w.code, version: w.version + 1, initialState: w.initialState, transitions: w.transitions }).subscribe({
      next: ({ value }) => void this.router.navigate(['/workflows', value.id]),
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(describeError(e));
      },
    });
  }
}

/**
 * One draft: its problems, its transitions, editing while it is a draft, and the lifecycle: submit, approve and activate, each an
 * electronic signature, or reject. For a signature the dialog confirms what is signed, Keycloak asks for the password, and the step is
 * recorded together with the signature when the user returns.
 * Buttons appear only for the steps the draft's status and the user's permissions allow; the server checks both again.
 */
@Component({
  selector: 'pos-workflow-detail',
  imports: [ReactiveFormsModule, RouterLink, EsigDialog, Signatures],
  template: `
    <p><a routerLink="/workflows">← Workflows</a></p>
    @if (production()) {
      <p class="banner warning" role="status">
        This is a production environment: workflows are read-only here. They change only by deploying an approved
        <a routerLink="/packages">configuration package</a>.
      </p>
    }
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    @if (notice()) {<p class="banner ok" role="status">{{ notice() }}</p>}

    @if (draft(); as d) {
      <h1><code>{{ d.code }}</code> version {{ d.version }} <small>{{ statusLabels[d.status] }}</small></h1>
      @if (d.decisionComment) {<p class="comment"><strong>Comment:</strong> {{ d.decisionComment }}</p>}

      @if (d.errors.length) {
        <section class="banner warning" aria-label="Problems">
          <strong>Fix these before submitting:</strong>
          <ul>@for (e of d.errors; track $index) {<li>{{ e }}</li>}</ul>
        </section>
      }

      <div class="steps">
        @if (editable()) {
          <button type="button" class="secondary" [disabled]="busy()" (click)="validate()">Check</button>
          @if (canSign()) {
            <button type="button" [disabled]="busy() || !options() || d.status !== 'VALID' || dirty()" (click)="startSigning('submit')">{{ steps.submit.button }}</button>
          }
        }
        @if (canApprove()) {
          @if (d.status === 'SUBMITTED' && canSign()) {
            <button type="button" [disabled]="busy() || !options()" (click)="startSigning('approve')">{{ steps.approve.button }}</button>
          }
          @if (d.status === 'APPROVED' && canSign()) {
            <button type="button" [disabled]="busy() || !options()" (click)="startSigning('activate')">{{ steps.activate.button }}</button>
          }
          @if (d.status === 'SUBMITTED' || d.status === 'APPROVED') {
            <button type="button" class="secondary" [disabled]="busy() || !commentText().trim()" (click)="reject()">Reject</button>
            <label class="wide">Comment (required to reject)
              <input [formControl]="comment" maxlength="1000" placeholder="e.g. Checked against SOP-QA-012" />
            </label>
          }
        }
        @if (!canSign() && signingNeeded()) {
          <span class="hint">Signing needs the electronic signature permission.</span>
        }
      </div>

      @if (d.submissionSignatureId || d.approvalSignatureId || d.activationSignatureId) {
        <pos-signatures entity="wf.definition" [recordId]="d.id" [refresh]="signatureRefresh()" />
      }
      @if (intent(); as i) {
        <pos-esig-dialog [intent]="i" (confirmed)="confirmSigning($event)" (cancelled)="intent.set(null)" />
      }

      <h2>Transitions</h2>
      <table aria-label="Transitions">
        <thead><tr><th>From</th><th>Action</th><th>To</th><th>Label</th><th>Permission</th><th>Signature</th><th>Reason</th></tr></thead>
        <tbody>
          @for (t of d.transitions; track $index) {
            <tr>
              <td>{{ t.from }}</td><td><code>{{ t.action }}</code></td><td>{{ t.to }}</td><td>{{ t.label }}</td>
              <td><code>{{ t.permission }}</code></td><td>{{ t.signatureMeaning ?? '—' }}</td><td>{{ t.requireReason ? 'Yes' : '' }}</td>
            </tr>
          }
        </tbody>
      </table>

      @if (editable()) {
        <h2>Edit</h2>
        <label>Version <input type="number" [formControl]="version" min="1" /></label>
        <label class="editor">Transitions (JSON)
          <textarea [formControl]="transitions" rows="18" spellcheck="false"></textarea>
        </label>
        <div class="steps">
          @if (stale()) {<button type="button" class="secondary" (click)="load()">Reload latest version</button>}
          <button type="button" [disabled]="busy() || !dirty()" (click)="save()">Save draft</button>
        </div>
      }
    } @else if (!error()) {
      <p>Loading…</p>
    }
  `,
  styles: `
    h1 small { font-size: 0.9rem; font-weight: 400; color: var(--pos-muted); margin-left: 8px; }
    table { width: 100%; border-collapse: collapse; margin: 0 0 24px; font-size: 0.9rem; }
    th, td { text-align: left; border-bottom: 1px solid var(--pos-border); padding: 4px 6px; }
    .steps { display: flex; flex-wrap: wrap; gap: 8px; align-items: end; margin: 12px 0 20px; }
    label { display: flex; flex-direction: column; font-size: 0.875rem; gap: 4px; margin: 0 0 12px; }
    .wide { flex: 1 1 320px; margin: 0; }
    .editor textarea { font-family: monospace; font-size: 0.8rem; width: 100%; }
    .comment, .hint { color: var(--pos-muted); }
  `,
})
export class WorkflowDetailPage implements OnInit {
  private readonly api = inject(WorkflowApi);
  private readonly packages = inject(ConfigPackageApi);
  private readonly auth = inject(AuthService);
  private readonly esig = inject(EsigApi);
  private readonly router = inject(Router);

  readonly id = input.required<string>();
  /** Set when Keycloak sends the user back after the password was confirmed for a signature. */
  readonly esigChallenge = input<string | undefined>(undefined, { alias: 'esig' });
  protected readonly statusLabels = DRAFT_STATUS_LABELS;
  protected readonly steps = SIGNED_STEPS;
  readonly options = signal<SigningOptions | null>(null);
  /** The step the open signing dialog is for. */
  private readonly signing = signal<SignedStep>('approve');
  readonly intent = signal<SignIntent | null>(null);
  readonly signatureRefresh = signal(0);
  readonly draft = signal<WorkflowDraft | null>(null);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly stale = signal(false);
  readonly busy = signal(false);
  private etag = '';

  readonly version = new FormControl(1, { nonNullable: true, validators: [Validators.required, Validators.min(1)] });
  readonly transitions = new FormControl('', { nonNullable: true });
  readonly comment = new FormControl('', { nonNullable: true });
  /** The comment as a signal, so the Reject button follows what is typed. */
  readonly commentText = toSignal(this.comment.valueChanges, { initialValue: '' });

  /** True in a production environment, where workflows change only by deploying a package. */
  readonly production = signal(false);
  readonly editable = computed(() => !this.production() && (this.draft()?.status === 'DRAFT' || this.draft()?.status === 'VALID'));
  readonly canApprove = computed(() => !this.production() && this.auth.hasPermission('wf.definition:approve'));
  readonly canSign = computed(() => this.auth.hasPermission('esig.sign'));
  /** True when the draft is at a step this user could take, if they could sign. */
  readonly signingNeeded = computed(() => {
    const status = this.draft()?.status;
    return this.editable() || (this.canApprove() && (status === 'SUBMITTED' || status === 'APPROVED'));
  });

  ngOnInit(): void {
    this.packages.environment().subscribe({ next: (e) => this.production.set(e.production), error: () => this.production.set(false) });
    this.load();
    if (this.canSign()) {
      this.esig.options(ENTITY).subscribe({ next: (o) => this.options.set(o), error: () => this.options.set(null) });
    }
    const challenge = this.esigChallenge();
    if (challenge) this.completeSigning(challenge);
  }

  /** Shows what is being signed. Nothing is sent until the user confirms. */
  startSigning(step: SignedStep): void {
    const d = this.draft();
    const user = this.auth.user();
    const options = this.options();
    const meaning = options?.meanings.find((m) => m.code === SIGNED_STEPS[step].meaning);
    if (!d || !user || !options || !meaning) return;
    this.notice.set(null);
    this.signing.set(step);
    this.intent.set({
      entity: ENTITY,
      recordNumber: `${d.code} v${d.version}`,
      recordVersion: d.rowVersion,
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
    const d = this.draft();
    if (!d) return;
    this.esig
      .challenge({
        entity: ENTITY, recordId: d.id, recordVersion: d.rowVersion, meaning: SIGNED_STEPS[this.signing()].meaning, reason: c.reason,
        acknowledge: c.acknowledged, action: this.signing(),
      })
      .subscribe({
        next: ({ challengeId }) => this.auth.reauthenticate(`/workflows/${d.id}?esig=${encodeURIComponent(challengeId)}`),
        error: (e: unknown) => {
          this.intent.set(null);
          this.error.set(describeError(e));
        },
      });
  }

  private completeSigning(challengeId: string): void {
    this.esig.complete(challengeId).subscribe({
      next: (s) => {
        this.notice.set(`Signed: ${s.meaningLabel} by ${s.signerName}, version ${s.recordVersion}. ${AFTER_SIGNATURE[s.meaning] ?? ''}`.trim());
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
    void this.router.navigate(['/workflows', this.id()], { replaceUrl: true });
  }

  dirty(): boolean {
    return this.version.dirty || this.transitions.dirty;
  }

  load(): void {
    this.api.get(this.id()).subscribe({
      next: ({ value, etag }) => this.apply(value, etag),
      error: (e: unknown) => this.error.set(describeError(e)),
    });
  }

  save(): void {
    const d = this.draft();
    if (!d) return;
    let parsed: WorkflowTransition[];
    try {
      parsed = JSON.parse(this.transitions.value) as WorkflowTransition[];
      if (!Array.isArray(parsed)) throw new Error('not a list');
    } catch {
      this.error.set('Transitions must be a JSON list of transitions. Nothing was saved.');
      return;
    }
    this.busy.set(true);
    this.api.replace(d.id, this.etag, { code: d.code, version: this.version.value, initialState: d.initialState, transitions: parsed })
      .subscribe({
        next: ({ value, etag }) => {
          this.apply(value, etag);
          this.notice.set(value.status === 'VALID' ? 'Saved. The draft is valid.' : 'Saved. Fix the problems listed before submitting.');
        },
        error: (e: unknown) => this.fail(e),
      });
  }

  validate(): void {
    const d = this.draft();
    if (!d) return;
    this.api.validate(d.id).subscribe({
      next: (r) => {
        this.draft.set({ ...d, errors: r.errors });
        this.notice.set(r.valid ? 'Every rule passes.' : null);
      },
      error: (e: unknown) => this.error.set(describeError(e)),
    });
  }

  /** Rejecting signs nothing, so it is a plain call; the reason is required. */
  reject(): void {
    const d = this.draft();
    const comment = this.comment.value.trim();
    if (!d || !comment) return;
    this.busy.set(true);
    this.api.reject(d.id, this.etag, comment).subscribe({
      next: ({ value, etag }) => {
        this.apply(value, etag);
        this.notice.set('Rejected. The draft can be edited again.');
      },
      error: (e: unknown) => this.fail(e),
    });
  }

  private fail(e: unknown): void {
    this.busy.set(false);
    this.notice.set(null);
    this.stale.set((e as { status?: number }).status === 412);
    this.error.set(describeError(e));
  }

  private apply(d: WorkflowDraft, etag: string): void {
    this.draft.set(d);
    this.etag = etag;
    this.stale.set(false);
    this.error.set(null);
    this.busy.set(false);
    this.version.reset(d.version);
    this.transitions.reset(JSON.stringify(d.transitions, null, 2));
    this.comment.reset('');
  }
}

const ENTITY = 'wf.definition';
