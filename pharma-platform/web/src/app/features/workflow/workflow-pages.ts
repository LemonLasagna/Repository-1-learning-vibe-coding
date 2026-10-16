import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthService, describeError, formatTimestamp } from '../../core/api/api';
import {
  CurrentWorkflow, DRAFT_STATUS_LABELS, DraftStep, WorkflowApi, WorkflowDraft, WorkflowTransition,
} from '../../core/api/workflow-api';

/** Record types' workflows and the drafts that change them. A new draft starts as a copy of the current workflow. */
@Component({
  selector: 'pos-workflow-list',
  imports: [RouterLink],
  template: `
    <h1>Workflows</h1>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}

    <h2>Record types</h2>
    <table aria-label="Record types">
      <thead><tr><th>Record type</th><th>Workflow</th><th>Current version</th><th></th></tr></thead>
      <tbody>
        @for (w of workflows(); track w.code) {
          <tr>
            <td>{{ w.name }}</td>
            <td><code>{{ w.code }}</code></td>
            <td>{{ w.version }}</td>
            <td><button type="button" class="secondary small" [disabled]="busy()" (click)="newDraft(w)">New draft</button></td>
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
  private readonly router = inject(Router);

  protected readonly statusLabels = DRAFT_STATUS_LABELS;
  protected readonly time = formatTimestamp;
  readonly workflows = signal<CurrentWorkflow[]>([]);
  readonly drafts = signal<WorkflowDraft[] | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  ngOnInit(): void {
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
 * One draft: its problems, its transitions, editing while it is a draft, and the lifecycle (submit, approve or reject, activate).
 * Buttons appear only for the steps the draft's status and the user's permissions allow; the server checks both again.
 */
@Component({
  selector: 'pos-workflow-detail',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <p><a routerLink="/workflows">← Workflows</a></p>
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
          <button type="button" [disabled]="busy() || d.status !== 'VALID' || dirty()" (click)="run('submit')">Submit for approval</button>
        }
        @if (canApprove()) {
          @if (d.status === 'SUBMITTED') {
            <button type="button" [disabled]="busy()" (click)="run('approve')">Approve</button>
          }
          @if (d.status === 'APPROVED') {
            <button type="button" [disabled]="busy()" (click)="run('activate')">Activate</button>
          }
          @if (d.status === 'SUBMITTED' || d.status === 'APPROVED') {
            <button type="button" class="secondary" [disabled]="busy() || !commentText().trim()" (click)="run('reject')">Reject</button>
            <label class="wide">Comment (required to reject)
              <input [formControl]="comment" maxlength="1000" placeholder="e.g. Checked against SOP-QA-012" />
            </label>
          }
        }
      </div>

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
    .comment { color: var(--pos-muted); }
  `,
})
export class WorkflowDetailPage implements OnInit {
  private readonly api = inject(WorkflowApi);
  private readonly auth = inject(AuthService);

  readonly id = input.required<string>();
  protected readonly statusLabels = DRAFT_STATUS_LABELS;
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

  readonly editable = computed(() => this.draft()?.status === 'DRAFT' || this.draft()?.status === 'VALID');
  readonly canApprove = computed(() => this.auth.hasPermission('wf.definition:approve'));

  ngOnInit(): void {
    this.load();
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

  run(step: DraftStep): void {
    const d = this.draft();
    if (!d) return;
    const comment = this.comment.value.trim() || null;
    if (step === 'reject' && !comment) return;
    this.busy.set(true);
    this.api.step(d.id, this.etag, step, comment).subscribe({
      next: ({ value, etag }) => {
        this.apply(value, etag);
        this.notice.set(DONE[step]);
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

const DONE: Record<DraftStep, string> = {
  submit: 'Submitted for approval.',
  approve: 'Approved. Activate it to make new records use it.',
  reject: 'Rejected. The draft can be edited again.',
  activate: 'Active. New records start on this version; records in progress keep theirs.',
};
