import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormControl, FormGroup, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { forkJoin, Observable } from 'rxjs';
import { AuthService, describeError, EsigApi, formatTimestamp, Versioned } from '../../core/api/api';
import { AppUser, Capa, CAPA_STATES, CapaApi, CapaAvailable, capaStateLabel } from '../../core/api/capa-api';
import { DeviationApi, HistoryEntry, Site } from '../../core/api/deviation-api';
import { AuditTrail } from '../../shared/audit-trail';
import { HistoryTable } from '../../shared/history-table';
import { Signatures } from '../../shared/signatures';
import { WorkflowPanel } from '../../shared/workflow-panel';

const CAPA_TYPES = [
  { code: 'CORRECTIVE', label: 'Corrective' },
  { code: 'PREVENTIVE', label: 'Preventive' },
  { code: 'CORRECTIVE_AND_PREVENTIVE', label: 'Corrective and preventive' },
];
const PRIORITIES = ['LOW', 'MEDIUM', 'HIGH'];
const ACTION_TYPES = [
  { code: 'CORRECTION', label: 'Correction' },
  { code: 'CORRECTIVE', label: 'Corrective' },
  { code: 'PREVENTIVE', label: 'Preventive' },
];

function today(plusDays = 0): string {
  const d = new Date();
  d.setDate(d.getDate() + plusDays);
  return d.toISOString().slice(0, 10);
}

/** CAPAs: list with status filter. */
@Component({
  selector: 'pos-capa-list',
  imports: [FormsModule, RouterLink],
  template: `
    <div class="head">
      <h1>CAPA</h1>
      @if (canCreate()) {<a class="button" routerLink="/capas/new">New CAPA</a>}
    </div>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    <label class="filter">Status
      <select [ngModel]="status()" (ngModelChange)="filter($event)">
        <option value="">All</option>
        @for (s of states; track s.code) {<option [value]="s.code">{{ s.label }}</option>}
        <option value="CANCELLED">Cancelled</option>
      </select>
    </label>
    @if (items() === null) {
      <p>Loading…</p>
    } @else {
      <table>
        <thead><tr><th>Number</th><th>Title</th><th>Status</th><th>Priority</th><th>Source</th><th>Owner</th><th>Due</th></tr></thead>
        <tbody>
          @for (c of items(); track c.id) {
            <tr>
              <td><a [routerLink]="['/capas', c.id]">{{ c.capaNo }}</a></td>
              <td>{{ c.title }}</td>
              <td>{{ label(c.status) }}</td>
              <td>{{ c.priority }}</td>
              <td>{{ c.sourceNo ?? '—' }}</td>
              <td>{{ c.ownerName }}</td>
              <td>{{ c.dueDate }}</td>
            </tr>
          } @empty {
            <tr><td colspan="7">No CAPAs.</td></tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: `
    .head { display: flex; justify-content: space-between; align-items: center; }
    .filter { display: inline-flex; gap: 8px; align-items: center; margin: 0 0 12px; font-size: 0.9rem; }
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; border-bottom: 1px solid var(--pos-border); padding: 6px 8px; }
  `,
})
export class CapaListPage implements OnInit {
  private readonly api = inject(CapaApi);
  private readonly auth = inject(AuthService);
  protected readonly states = CAPA_STATES;
  protected readonly label = capaStateLabel;
  readonly items = signal<Capa[] | null>(null);
  readonly error = signal<string | null>(null);
  readonly status = signal('');
  readonly canCreate = computed(() => this.auth.hasPermission('qms.capa:write'));

  ngOnInit(): void {
    this.load();
  }

  filter(status: string): void {
    this.status.set(status);
    this.load();
  }

  private load(): void {
    this.api.list({ status: this.status() || undefined }).subscribe({
      next: (items) => this.items.set(items),
      error: (e: unknown) => {
        this.items.set([]);
        this.error.set(describeError(e));
      },
    });
  }
}

/** Raise a CAPA, from a deviation (?sourceId=…, site and text taken from it) or on its own (choose the site). */
@Component({
  selector: 'pos-capa-create',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <p><a routerLink="/capas">← CAPA</a></p>
    <h1>New CAPA</h1>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    <form [formGroup]="form" (ngSubmit)="save()" class="grid">
      @if (sourceNo(); as no) {
        <p class="wide">Source: deviation <strong>{{ no }}</strong> (the CAPA takes its site)</p>
      } @else {
        <label>Site <span class="req">*</span>
          <select formControlName="siteId">
            <option [ngValue]="null">Select…</option>
            @for (s of sites(); track s.id) {<option [ngValue]="s.id">{{ s.code }} · {{ s.name }}</option>}
          </select>
        </label>
      }
      <label class="wide">Title <span class="req">*</span><input formControlName="title" maxlength="1000" /></label>
      <label class="wide">Problem statement <span class="req">*</span><textarea formControlName="problemStatement" rows="4"></textarea></label>
      <label>Type <span class="req">*</span>
        <select formControlName="capaType">@for (t of types; track t.code) {<option [value]="t.code">{{ t.label }}</option>}</select>
      </label>
      <label>Priority <span class="req">*</span>
        <select formControlName="priority">@for (p of priorities; track p) {<option [value]="p">{{ p }}</option>}</select>
      </label>
      <label>Due date <input type="date" formControlName="dueDate" /></label>
      <div class="actions wide"><button type="submit" [disabled]="busy()">Save draft</button></div>
    </form>
  `,
  styles: `
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 12px; }
    .grid label { display: flex; flex-direction: column; gap: 4px; font-size: 0.875rem; }
    .wide { grid-column: 1 / -1; }
    .actions { display: flex; justify-content: flex-end; }
    .req { color: var(--pos-danger); }
  `,
})
export class CapaCreatePage implements OnInit {
  private readonly api = inject(CapaApi);
  private readonly deviations = inject(DeviationApi);
  private readonly router = inject(Router);

  /** Query parameter: the deviation this CAPA addresses. */
  readonly sourceId = input<string | undefined>(undefined);
  protected readonly types = CAPA_TYPES;
  protected readonly priorities = PRIORITIES;
  readonly sites = signal<Site[]>([]);
  readonly sourceNo = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  readonly form = new FormGroup({
    siteId: new FormControl<string | null>(null),
    title: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(1000)] }),
    problemStatement: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    capaType: new FormControl('CORRECTIVE', { nonNullable: true }),
    priority: new FormControl('MEDIUM', { nonNullable: true }),
    dueDate: new FormControl(''),
  });

  ngOnInit(): void {
    const source = this.sourceId();
    if (source) {
      this.deviations.get(source).subscribe({
        next: ({ value: d }) => {
          this.sourceNo.set(d.deviationNo);
          this.form.patchValue({
            title: `CAPA for ${d.deviationNo}: ${d.title}`.slice(0, 1000),
            problemStatement: [d.description, d.rootCauseSummary && `Root cause: ${d.rootCauseSummary}`].filter(Boolean).join('\n\n'),
            priority: d.classification === 'MINOR' ? 'MEDIUM' : 'HIGH',
          });
        },
        error: (e: unknown) => this.error.set(describeError(e)),
      });
    } else {
      this.deviations.sites().subscribe({
        next: (sites) => {
          this.sites.set(sites);
          if (sites.length === 1) this.form.patchValue({ siteId: sites[0].id });
        },
        error: (e: unknown) => this.error.set(describeError(e)),
      });
    }
  }

  save(): void {
    const v = this.form.getRawValue();
    const source = this.sourceId();
    if (this.form.invalid || (!source && !v.siteId)) {
      this.form.markAllAsTouched();
      this.error.set(!source && !v.siteId ? 'Choose the site.' : 'Complete the required fields.');
      return;
    }
    this.busy.set(true);
    const body: Record<string, unknown> = {
      title: v.title, problemStatement: v.problemStatement, capaType: v.capaType, priority: v.priority,
      ...(v.dueDate ? { dueDate: v.dueDate } : {}),
      ...(source ? { sourceType: 'DEVIATION', sourceId: source } : { sourceType: 'OTHER', siteId: v.siteId }),
    };
    this.api.create(body).subscribe({
      next: ({ value }) => void this.router.navigate(['/capas', value.id]),
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(describeError(e));
      },
    });
  }
}

/**
 * One CAPA: the plan (actions and effectiveness check) editable while drafting, actions completed with evidence during
 * implementation, the effectiveness result, the workflow with signing, and history, signatures and audit trail.
 */
@Component({
  selector: 'pos-capa-detail',
  imports: [FormsModule, ReactiveFormsModule, RouterLink, AuditTrail, HistoryTable, Signatures, WorkflowPanel],
  template: `
    <p><a routerLink="/capas">← CAPA</a></p>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    @if (message()) {<p class="banner ok" role="status">{{ message() }}</p>}

    @if (capa(); as c) {
      <h1>{{ c.capaNo }} <small>{{ c.title }}</small></h1>
      <ol class="progress" aria-label="Workflow">
        @for (s of states; track s.code) {
          <li [class.current]="s.code === c.status" [class.done]="isDone(s.code, c.status)">{{ s.label }}</li>
        }
        @if (c.status === 'CANCELLED') {<li class="current cancelled">Cancelled</li>}
      </ol>
      <dl class="facts">
        <dt>Source</dt>
        <dd>@if (c.sourceId) {<a [routerLink]="['/deviations', c.sourceId]">{{ c.sourceNo }}</a>} @else {—}</dd>
        <dt>Site</dt><dd>{{ c.siteCode }}</dd>
        <dt>Owner</dt><dd>{{ c.ownerName }}</dd>
        <dt>Type</dt><dd>{{ typeLabel(c.capaType) }}</dd>
        <dt>Priority</dt><dd>{{ c.priority }}</dd>
        <dt>Due</dt><dd>{{ c.dueDate }}</dd>
        <dt>Version</dt><dd>{{ c.rowVersion }}</dd>
      </dl>

      @if (available()?.canEditPlan) {
        <form [formGroup]="header" class="grid" (ngSubmit)="saveHeader()">
          <label class="wide">Title<input formControlName="title" maxlength="1000" /></label>
          <label class="wide">Problem statement<textarea formControlName="problemStatement" rows="3"></textarea></label>
          <label>Type<select formControlName="capaType">@for (t of types; track t.code) {<option [value]="t.code">{{ t.label }}</option>}</select></label>
          <label>Priority<select formControlName="priority">@for (p of priorities; track p) {<option [value]="p">{{ p }}</option>}</select></label>
          <label>Due date<input type="date" formControlName="dueDate" /></label>
          <div class="end"><button type="submit" class="secondary" [disabled]="busy() || header.pristine">Save</button></div>
        </form>
      } @else {
        <p class="problem">{{ c.problemStatement }}</p>
      }

      <section aria-labelledby="actions-title">
        <h2 id="actions-title">Actions</h2>
        <table>
          <thead><tr><th>#</th><th>Type</th><th>Description</th><th>Assignee</th><th>Due</th><th>Status</th><th>Evidence</th><th></th></tr></thead>
          <tbody>
            @for (a of c.actions; track a.no) {
              <tr [class.withdrawn]="a.status === 'WITHDRAWN'">
                <td>{{ a.no }}</td>
                <td>{{ typeLabel(a.actionType) }}</td>
                <td>{{ a.description }}</td>
                <td>{{ a.assigneeName }}</td>
                <td>{{ a.dueDate }}</td>
                <td>{{ a.status }}</td>
                <td>{{ a.evidence ?? '' }}</td>
                <td>
                  @if (available()?.canEditPlan && a.status === 'PLANNED') {
                    <button type="button" class="secondary small" (click)="withdraw(a.no)">Withdraw</button>
                  }
                  @if (available()?.canCompleteActions && a.status === 'PLANNED') {
                    <button type="button" class="small" (click)="completing.set(a.no); evidence = ''">Complete…</button>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="8">No actions planned.</td></tr>
            }
          </tbody>
        </table>
        @if (completing(); as no) {
          <div class="panel" role="group" aria-label="Complete action">
            <label>Evidence for action {{ no }} <span class="req">*</span>
              <textarea name="evidence" rows="2" [(ngModel)]="evidence"></textarea></label>
            <div class="end">
              <button type="button" class="secondary" (click)="completing.set(null)">Cancel</button>
              <button type="button" [disabled]="!evidence.trim() || busy()" (click)="complete(no)">Mark done</button>
            </div>
          </div>
        }
        @if (available()?.canEditPlan) {
          <form [formGroup]="newAction" class="grid panel" (ngSubmit)="addAction()" aria-label="Add action">
            <label>Type<select formControlName="actionType">@for (t of actionTypes; track t.code) {<option [value]="t.code">{{ t.label }}</option>}</select></label>
            <label>Assignee<select formControlName="assigneeId">
              @for (u of users(); track u.id) {<option [value]="u.id">{{ u.displayName }}</option>}</select></label>
            <label>Due date<input type="date" formControlName="dueDate" /></label>
            <label class="wide">Action<textarea formControlName="description" rows="2"></textarea></label>
            <div class="end"><button type="submit" class="secondary" [disabled]="busy() || newAction.invalid">Add action</button></div>
          </form>
        }
      </section>

      <section aria-labelledby="eff-title">
        <h2 id="eff-title">Effectiveness check</h2>
        @if (c.effectiveness; as e) {
          <dl class="facts">
            <dt>Criteria</dt><dd>{{ e.criteria }}</dd>
            <dt>Method</dt><dd>{{ e.method ?? '—' }}</dd>
            <dt>Planned</dt><dd>{{ e.plannedDate }}</dd>
            @if (e.result) {
              <dt>Result</dt><dd><strong>{{ e.result }}</strong> ({{ e.performedByName }}, {{ time(e.performedAt!) }})</dd>
              <dt>Evidence</dt><dd>{{ e.evidence }}</dd>
            }
          </dl>
        } @else {
          <p>Not planned yet.</p>
        }
        @if (available()?.canEditPlan) {
          <form [formGroup]="check" class="grid panel" (ngSubmit)="saveCheck()" aria-label="Plan effectiveness check">
            <label class="wide">Criteria<textarea formControlName="criteria" rows="2"></textarea></label>
            <label>Method<input formControlName="method" maxlength="1000" /></label>
            <label>Planned date<input type="date" formControlName="plannedDate" /></label>
            <div class="end"><button type="submit" class="secondary" [disabled]="busy() || check.invalid">Save check</button></div>
          </form>
        }
        @if (available()?.canRecordEffectiveness && !c.effectiveness?.result) {
          <div class="grid panel" role="group" aria-label="Record effectiveness result">
            <label>Result<select name="result" [(ngModel)]="result">
              <option value="EFFECTIVE">Effective</option><option value="INEFFECTIVE">Ineffective</option></select></label>
            <label class="wide">Evidence<textarea name="resultEvidence" rows="2" [(ngModel)]="resultEvidence"></textarea></label>
            <div class="end"><button type="button" [disabled]="!resultEvidence.trim() || busy()" (click)="recordResult()">Record result</button></div>
          </div>
        }
      </section>

      <pos-workflow-panel entity="qms.capa" [recordId]="c.id" [recordLabel]="c.capaNo" [recordVersion]="c.rowVersion"
        [actions]="available()?.actions ?? []" [returnPath]="'/capas/' + c.id" [blocked]="unsaved"
        [transition]="transition" (done)="afterTransition($event)" (failed)="fail($event)" />

      <section aria-labelledby="hist-title">
        <h2 id="hist-title">Workflow history</h2>
        <pos-history-table [entries]="history()" [label]="label" />
      </section>
      <pos-signatures entity="qms.capa" [recordId]="c.id" [refresh]="c.rowVersion" />
      <pos-audit-trail schema="qms" table="capa" [recordId]="c.id" [refresh]="c.rowVersion" />
    } @else if (!error()) {
      <p>Loading…</p>
    }
  `,
  styles: `
    h1 small { font-size: 1rem; font-weight: 400; color: var(--pos-muted); margin-left: 8px; }
    .facts { display: grid; grid-template-columns: max-content 1fr; gap: 2px 16px; margin: 0 0 16px; font-size: 0.9rem; }
    .facts dt { color: var(--pos-muted); }
    .facts dd { margin: 0; }
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 10px; margin: 0 0 12px; }
    .grid label, .panel label { display: flex; flex-direction: column; gap: 4px; font-size: 0.875rem; }
    .wide { grid-column: 1 / -1; }
    .end { grid-column: 1 / -1; display: flex; justify-content: flex-end; gap: 8px; }
    .panel { border: 1px solid var(--pos-border); border-radius: 6px; padding: 12px; margin: 8px 0 12px; }
    .problem { white-space: pre-wrap; }
    table { width: 100%; border-collapse: collapse; font-size: 0.9rem; }
    th, td { text-align: left; border-bottom: 1px solid var(--pos-border); padding: 4px 8px; vertical-align: top; }
    tr.withdrawn { opacity: 0.55; text-decoration: line-through; }
    .req { color: var(--pos-danger); }
  `,
})
export class CapaDetailPage implements OnInit {
  private readonly api = inject(CapaApi);
  private readonly esig = inject(EsigApi);
  private readonly router = inject(Router);

  readonly id = input.required<string>();
  /** Set when Keycloak sends the user back after re-authenticating for a signature. */
  readonly esigChallenge = input<string | undefined>(undefined, { alias: 'esig' });

  protected readonly states = CAPA_STATES;
  protected readonly types = CAPA_TYPES;
  protected readonly actionTypes = ACTION_TYPES;
  protected readonly priorities = PRIORITIES;
  protected readonly label = capaStateLabel;
  readonly capa = signal<Capa | null>(null);
  readonly available = signal<CapaAvailable | null>(null);
  readonly history = signal<HistoryEntry[]>([]);
  readonly users = signal<AppUser[]>([]);
  readonly completing = signal<number | null>(null);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly busy = signal(false);
  protected evidence = '';
  protected result = 'EFFECTIVE';
  protected resultEvidence = '';
  private etag = '';
  private doneLabel: string | null = null;

  readonly header = new FormGroup({
    title: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    problemStatement: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    capaType: new FormControl('', { nonNullable: true }),
    priority: new FormControl('', { nonNullable: true }),
    dueDate: new FormControl('', { nonNullable: true }),
  });
  readonly newAction = new FormGroup({
    actionType: new FormControl('CORRECTIVE', { nonNullable: true }),
    description: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    assigneeId: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    dueDate: new FormControl(today(30), { nonNullable: true, validators: [Validators.required] }),
  });
  readonly check = new FormGroup({
    criteria: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    method: new FormControl('', { nonNullable: true }),
    plannedDate: new FormControl(today(120), { nonNullable: true, validators: [Validators.required] }),
  });

  /** Runs an unsigned transition with the ETag last read. */
  readonly transition = (action: string, reason: string | null) => this.api.transition(this.id(), this.etag, action, reason);

  ngOnInit(): void {
    const challenge = this.esigChallenge();
    if (challenge) this.completeSigning(challenge);
    else this.load();
  }

  load(): void {
    forkJoin([this.api.get(this.id()), this.api.available(this.id()), this.api.history(this.id())]).subscribe({
      next: ([{ value, etag }, available, history]) => {
        this.apply(value, etag);
        this.available.set(available);
        this.history.set(history);
        if (this.doneLabel) this.message.set(`${this.doneLabel}: now ${capaStateLabel(value.status)}.`);
        this.doneLabel = null;
        if (available.canEditPlan && this.users().length === 0) {
          this.api.users().subscribe({
            next: (u) => {
              this.users.set(u);
              if (!this.newAction.controls.assigneeId.value) this.newAction.patchValue({ assigneeId: value.ownerId });
            },
          });
        }
      },
      error: (e: unknown) => this.fail(describeError(e)),
    });
  }

  /** Unsaved header changes block workflow actions. */
  readonly unsaved = (): string | null => (this.header.dirty ? 'Save or discard your changes first.' : null);

  saveHeader(): void {
    this.mutate(this.api.updateHeader(this.id(), this.etag, this.header.getRawValue()), 'Saved.');
  }

  addAction(): void {
    this.mutate(this.api.addAction(this.id(), this.etag, this.newAction.getRawValue()), 'Action added.', () =>
      this.newAction.patchValue({ description: '' }),
    );
  }

  withdraw(no: number): void {
    this.mutate(this.api.updateAction(this.id(), this.etag, no, { withdrawn: true }), `Action ${no} withdrawn.`);
  }

  complete(no: number): void {
    this.mutate(this.api.completeAction(this.id(), this.etag, no, this.evidence.trim()), `Action ${no} done.`, () => this.completing.set(null));
  }

  saveCheck(): void {
    const v = this.check.getRawValue();
    this.mutate(this.api.defineEffectiveness(this.id(), this.etag, { ...v, method: v.method.trim() || null }), 'Effectiveness check saved.');
  }

  recordResult(): void {
    this.mutate(this.api.recordEffectiveness(this.id(), this.etag, this.result, this.resultEvidence.trim()), 'Result recorded.');
  }

  afterTransition(label: string): void {
    this.clear();
    this.doneLabel = label;
    this.load();
  }

  fail(message: string): void {
    this.busy.set(false);
    this.message.set(null);
    this.error.set(message);
  }

  protected typeLabel(code: string): string {
    return [...CAPA_TYPES, ...ACTION_TYPES].find((t) => t.code === code)?.label ?? code;
  }

  protected isDone(state: string, current: string): boolean {
    const order = CAPA_STATES.map((s) => s.code);
    return current === 'CLOSED' || order.indexOf(state) < order.indexOf(current);
  }

  protected time(iso: string): string {
    return formatTimestamp(iso);
  }

  private mutate(call: Observable<Versioned<Capa>>, done: string, after?: () => void): void {
    this.busy.set(true);
    this.clear();
    call.subscribe({
      next: () => {
        after?.();
        this.message.set(done);
        this.busy.set(false);
        this.load();
      },
      error: (e: unknown) => this.fail(describeError(e)),
    });
  }

  private apply(c: Capa, etag: string): void {
    this.capa.set(c);
    this.etag = etag;
    this.header.reset({ title: c.title, problemStatement: c.problemStatement, capaType: c.capaType, priority: c.priority, dueDate: c.dueDate });
    if (c.effectiveness) {
      this.check.reset({ criteria: c.effectiveness.criteria, method: c.effectiveness.method ?? '', plannedDate: c.effectiveness.plannedDate });
    }
    this.busy.set(false);
  }

  private completeSigning(challengeId: string): void {
    this.esig.complete(challengeId).subscribe({
      next: (s) => {
        this.message.set(`Signed: ${s.meaningLabel} by ${s.signerName}.`);
        this.forgetChallenge();
        this.load();
      },
      error: (e: unknown) => {
        this.error.set(describeError(e));
        this.forgetChallenge();
        this.load();
      },
    });
  }

  private forgetChallenge(): void {
    void this.router.navigate(['/capas', this.id()], { replaceUrl: true });
  }

  private clear(): void {
    this.error.set(null);
    this.message.set(null);
  }
}

