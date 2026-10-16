import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormControl, FormGroup, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { forkJoin, Observable } from 'rxjs';
import { AuthService, describeError, EsigApi, formatTimestamp, Versioned } from '../../core/api/api';
import { AppUser, CapaApi } from '../../core/api/capa-api';
import {
  CHANGE_STATES, ChangeApi, ChangeAvailable, ChangeControl, changeStateLabel, IMPACT_AREAS,
} from '../../core/api/change-api';
import { DeviationApi, HistoryEntry, Site } from '../../core/api/deviation-api';
import { AuditTrail } from '../../shared/audit-trail';
import { HistoryTable } from '../../shared/history-table';
import { Signatures } from '../../shared/signatures';
import { WorkflowPanel } from '../../shared/workflow-panel';

const CHANGE_TYPES = [
  { code: 'PLANNED', label: 'Planned' },
  { code: 'EMERGENCY', label: 'Emergency' },
];
const CATEGORIES = [
  { code: 'MINOR', label: 'Minor' },
  { code: 'MAJOR', label: 'Major' },
  { code: 'CRITICAL', label: 'Critical' },
];
const REGULATORY_IMPACTS = [
  { code: 'NONE', label: 'None' },
  { code: 'MINOR', label: 'Minor (notification)' },
  { code: 'MAJOR', label: 'Major (prior approval)' },
];
const PHASES = [
  { code: 'BEFORE', label: 'Before implementation' },
  { code: 'IMPLEMENTATION', label: 'Implementation' },
  { code: 'AFTER', label: 'After implementation' },
];

function today(plusDays = 0): string {
  const d = new Date();
  d.setDate(d.getDate() + plusDays);
  return d.toISOString().slice(0, 10);
}

/** Empty date inputs are sent as null, which clears the date. */
const orNull = (v: string): string | null => (v.trim() === '' ? null : v);

/** Change controls: list with status filter. */
@Component({
  selector: 'pos-change-list',
  imports: [FormsModule, RouterLink],
  template: `
    <div class="head">
      <h1>Change control</h1>
      @if (canCreate()) {<a class="button" routerLink="/changes/new">New change</a>}
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
        <thead><tr><th>Number</th><th>Title</th><th>Status</th><th>Category</th><th>Site</th><th>Owner</th><th>Implementation due</th></tr></thead>
        <tbody>
          @for (c of items(); track c.id) {
            <tr>
              <td><a [routerLink]="['/changes', c.id]">{{ c.ccNo }}</a></td>
              <td>{{ c.title }}</td>
              <td>{{ label(c.status) }}</td>
              <td>{{ c.changeCategory }}</td>
              <td>{{ c.siteCode }}</td>
              <td>{{ c.ownerName }}</td>
              <td>{{ c.implementationDue ?? '—' }}</td>
            </tr>
          } @empty {
            <tr><td colspan="7">No change controls.</td></tr>
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
export class ChangeListPage implements OnInit {
  private readonly api = inject(ChangeApi);
  private readonly auth = inject(AuthService);
  protected readonly states = CHANGE_STATES;
  protected readonly label = changeStateLabel;
  readonly items = signal<ChangeControl[] | null>(null);
  readonly error = signal<string | null>(null);
  readonly status = signal('');
  readonly canCreate = computed(() => this.auth.hasPermission('qms.change:write'));

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

/** Raise a change control. */
@Component({
  selector: 'pos-change-create',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <p><a routerLink="/changes">← Change control</a></p>
    <h1>New change</h1>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    <form [formGroup]="form" (ngSubmit)="save()" class="grid">
      @if (sourceLabel(); as source) {
        <p class="wide">Source: <strong>{{ source }}</strong> (the change takes its site and is linked to the action)</p>
      } @else {
        <label>Site <span class="req">*</span>
          <select formControlName="siteId">
            <option [ngValue]="null">Select…</option>
            @for (s of sites(); track s.id) {<option [ngValue]="s.id">{{ s.code }} · {{ s.name }}</option>}
          </select>
        </label>
      }
      <label class="wide">Title <span class="req">*</span><input formControlName="title" maxlength="1000" /></label>
      <label class="wide">Description of the change <span class="req">*</span><textarea formControlName="description" rows="4"></textarea></label>
      <label class="wide">Justification <span class="req">*</span><textarea formControlName="justification" rows="3"></textarea></label>
      <label>Type <span class="req">*</span>
        <select formControlName="changeType">@for (t of types; track t.code) {<option [value]="t.code">{{ t.label }}</option>}</select>
      </label>
      <label>Category <span class="req">*</span>
        <select formControlName="changeCategory">@for (c of categories; track c.code) {<option [value]="c.code">{{ c.label }}</option>}</select>
      </label>
      <label>Regulatory impact
        <select formControlName="regulatoryImpact">@for (r of impacts; track r.code) {<option [value]="r.code">{{ r.label }}</option>}</select>
      </label>
      <label class="check"><input type="checkbox" formControlName="isTemporary" /> Temporary change</label>
      @if (form.controls.isTemporary.value) {
        <label>End date <span class="req">*</span><input type="date" formControlName="temporaryEndDate" /></label>
      }
      <label>Implementation due <input type="date" formControlName="implementationDue" /></label>
      <div class="actions wide"><button type="submit" [disabled]="busy()">Save draft</button></div>
    </form>
  `,
  styles: `
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 12px; }
    .grid label { display: flex; flex-direction: column; gap: 4px; font-size: 0.875rem; }
    .grid label.check { flex-direction: row; align-items: center; gap: 6px; }
    .wide { grid-column: 1 / -1; }
    .actions { display: flex; justify-content: flex-end; }
    .req { color: var(--pos-danger); }
  `,
})
export class ChangeCreatePage implements OnInit {
  private readonly api = inject(ChangeApi);
  private readonly capas = inject(CapaApi);
  private readonly deviations = inject(DeviationApi);
  private readonly router = inject(Router);

  /** Query parameters: the CAPA action this change implements (site and text are taken from it). */
  readonly capaId = input<string | undefined>(undefined);
  readonly actionNo = input<string | undefined>(undefined);
  readonly sourceLabel = signal<string | null>(null);

  protected readonly types = CHANGE_TYPES;
  protected readonly categories = CATEGORIES;
  protected readonly impacts = REGULATORY_IMPACTS;
  readonly sites = signal<Site[]>([]);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  readonly form = new FormGroup({
    siteId: new FormControl<string | null>(null, Validators.required),
    title: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(1000)] }),
    description: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    justification: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    changeType: new FormControl('PLANNED', { nonNullable: true }),
    changeCategory: new FormControl('MINOR', { nonNullable: true }),
    regulatoryImpact: new FormControl('NONE', { nonNullable: true }),
    isTemporary: new FormControl(false, { nonNullable: true }),
    temporaryEndDate: new FormControl(''),
    implementationDue: new FormControl(''),
  });

  ngOnInit(): void {
    const capaId = this.capaId();
    const no = Number(this.actionNo());
    if (capaId && no > 0) {
      this.form.controls.siteId.clearValidators();
      this.form.controls.siteId.updateValueAndValidity();
      this.capas.get(capaId).subscribe({
        next: ({ value: c }) => {
          const action = c.actions.find((a) => a.no === no);
          this.sourceLabel.set(`${c.capaNo}, action ${no}`);
          this.form.patchValue({
            title: `${c.capaNo} action ${no}: ${action?.description ?? c.title}`.slice(0, 120),
            description: action?.description ?? '',
            justification: `Implements ${c.capaNo} action ${no}: ${c.title}`,
          });
        },
        error: (e: unknown) => this.error.set(describeError(e)),
      });
      return;
    }
    this.deviations.sites().subscribe({
      next: (sites) => {
        this.sites.set(sites);
        if (sites.length === 1) this.form.patchValue({ siteId: sites[0].id });
      },
      error: (e: unknown) => this.error.set(describeError(e)),
    });
  }

  save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.error.set('Complete the required fields.');
      return;
    }
    const v = this.form.getRawValue();
    this.busy.set(true);
    const fromAction = this.capaId() && Number(this.actionNo()) > 0;
    const body: Record<string, unknown> = {
      ...(fromAction ? { sourceCapaId: this.capaId(), sourceActionNo: Number(this.actionNo()) } : { siteId: v.siteId }),
      title: v.title, description: v.description, justification: v.justification, changeType: v.changeType,
      changeCategory: v.changeCategory, regulatoryImpact: v.regulatoryImpact, isTemporary: v.isTemporary,
      ...(v.isTemporary && v.temporaryEndDate ? { temporaryEndDate: v.temporaryEndDate } : {}),
      ...(v.implementationDue ? { implementationDue: v.implementationDue } : {}),
    };
    this.api.create(body).subscribe({
      next: ({ value }) => void this.router.navigate(['/changes', value.id]),
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(describeError(e));
      },
    });
  }
}

/**
 * One change control: the description editable while drafting and assessing, impact assessments per area and tasks
 * planned during assessment, tasks completed and the effective date set during implementation, the workflow with
 * signing, and history, signatures and audit trail.
 */
@Component({
  selector: 'pos-change-detail',
  imports: [FormsModule, ReactiveFormsModule, RouterLink, AuditTrail, HistoryTable, Signatures, WorkflowPanel],
  template: `
    <p><a routerLink="/changes">← Change control</a></p>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    @if (message()) {<p class="banner ok" role="status">{{ message() }}</p>}

    @if (change(); as c) {
      <h1>{{ c.ccNo }} <small>{{ c.title }}</small></h1>
      <ol class="progress" aria-label="Workflow">
        @for (s of states; track s.code) {
          <li [class.current]="s.code === c.status" [class.done]="isDone(s.code, c.status)">{{ s.label }}</li>
        }
        @if (c.status === 'CANCELLED') {<li class="current cancelled">Cancelled</li>}
      </ol>
      <dl class="facts">
        <dt>Site</dt><dd>{{ c.siteCode }}</dd>
        <dt>Owner</dt><dd>{{ c.ownerName }}</dd>
        <dt>Type</dt><dd>{{ typeLabel(c.changeType) }}</dd>
        <dt>Category</dt><dd>{{ typeLabel(c.changeCategory) }}</dd>
        <dt>Regulatory impact</dt><dd>{{ typeLabel(c.regulatoryImpact ?? 'NONE') }}</dd>
        @if (c.isTemporary) {<dt>Temporary until</dt><dd>{{ c.temporaryEndDate ?? '— (end date missing)' }}</dd>}
        <dt>Implementation due</dt><dd>{{ c.implementationDue ?? '—' }}</dd>
        <dt>Effective date</dt><dd>{{ c.effectiveDate ?? '—' }}</dd>
        <dt>Version</dt><dd>{{ c.rowVersion }}</dd>
      </dl>

      @if (available()?.canEditHeader) {
        <form [formGroup]="header" class="grid" (ngSubmit)="saveHeader()">
          <label class="wide">Title<input formControlName="title" maxlength="1000" /></label>
          <label class="wide">Description<textarea formControlName="description" rows="3"></textarea></label>
          <label class="wide">Justification<textarea formControlName="justification" rows="2"></textarea></label>
          <label>Type<select formControlName="changeType">@for (t of types; track t.code) {<option [value]="t.code">{{ t.label }}</option>}</select></label>
          <label>Category<select formControlName="changeCategory">@for (t of categories; track t.code) {<option [value]="t.code">{{ t.label }}</option>}</select></label>
          <label>Regulatory impact<select formControlName="regulatoryImpact">@for (r of regulatory; track r.code) {<option [value]="r.code">{{ r.label }}</option>}</select></label>
          <label class="check"><input type="checkbox" formControlName="isTemporary" /> Temporary change</label>
          <label>End date<input type="date" formControlName="temporaryEndDate" /></label>
          <label>Implementation due<input type="date" formControlName="implementationDue" /></label>
          <div class="end"><button type="submit" class="secondary" [disabled]="busy() || header.pristine || header.invalid">Save</button></div>
        </form>
      } @else {
        <h2>Description</h2>
        <p class="text">{{ c.description }}</p>
        <h2>Justification</h2>
        <p class="text">{{ c.justification }}</p>
      }

      @if (c.linkedActions.length) {
        <section aria-labelledby="linked-title">
          <h2 id="linked-title">CAPA actions implemented through this change</h2>
          <table>
            <thead><tr><th>CAPA</th><th>Action</th><th>Description</th><th>Status</th></tr></thead>
            <tbody>
              @for (l of c.linkedActions; track l.capaId + l.actionNo) {
                <tr>
                  <td><a [routerLink]="['/capas', l.capaId]">{{ l.capaNo }}</a></td>
                  <td>{{ l.actionNo }}</td>
                  <td>{{ l.description }}</td>
                  <td>{{ l.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </section>
      }

      <section aria-labelledby="impact-title">
        <h2 id="impact-title">Impact assessment</h2>
        <table>
          <thead><tr><th>Area</th><th>Impacted</th><th>Assessment</th><th>Action required</th><th>Assessed by</th><th></th></tr></thead>
          <tbody>
            @for (i of c.impacts; track i.impactedArea) {
              <tr>
                <td>{{ areaLabel(i.impactedArea) }}</td>
                <td>{{ i.isImpacted ? 'Yes' : 'No' }}</td>
                <td>{{ i.assessment }}</td>
                <td>{{ i.actionRequired ?? '' }}</td>
                <td>{{ i.assessorName }}</td>
                <td>
                  @if (available()?.canAssess) {
                    <button type="button" class="secondary small" (click)="editImpact(i.impactedArea)">Edit</button>
                    <button type="button" class="secondary small" (click)="removeImpact(i.impactedArea)">Remove</button>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="6">No impact assessed yet.</td></tr>
            }
          </tbody>
        </table>
        @if (available()?.canAssess) {
          <form [formGroup]="impact" class="grid panel" (ngSubmit)="saveImpact()" aria-label="Assess impact">
            <label>Area<select formControlName="impactedArea">@for (a of areas; track a.code) {<option [value]="a.code">{{ a.label }}</option>}</select></label>
            <label class="check"><input type="checkbox" formControlName="isImpacted" /> This area is impacted</label>
            <label class="wide">Assessment <span class="req">*</span><textarea formControlName="assessment" rows="2"></textarea></label>
            <label class="wide">Action required<textarea formControlName="actionRequired" rows="2"></textarea></label>
            <div class="end"><button type="submit" class="secondary" [disabled]="busy() || impact.invalid">Save assessment</button></div>
          </form>
        }
      </section>

      <section aria-labelledby="tasks-title">
        <h2 id="tasks-title">Tasks</h2>
        <table>
          <thead><tr><th>#</th><th>Phase</th><th>Description</th><th>Assignee</th><th>Due</th><th>Status</th><th></th></tr></thead>
          <tbody>
            @for (t of c.tasks; track t.no) {
              <tr [class.withdrawn]="t.status === 'WITHDRAWN'">
                <td>{{ t.no }}</td>
                <td>{{ phaseLabel(t.phase) }}</td>
                <td>{{ t.description }}</td>
                <td>{{ t.assigneeName }}</td>
                <td>{{ t.dueDate }}</td>
                <td>{{ t.status }}@if (t.completedAt) { ({{ time(t.completedAt) }})}</td>
                <td>
                  @if (available()?.canPlanTasks && t.status === 'PLANNED') {
                    <button type="button" class="secondary small" (click)="withdraw(t.no)">Withdraw</button>
                  }
                  @if (available()?.canCompleteTasks && t.status === 'PLANNED') {
                    <button type="button" class="small" [disabled]="busy()" (click)="complete(t.no)">Mark done</button>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="7">No tasks planned.</td></tr>
            }
          </tbody>
        </table>
        @if (available()?.canPlanTasks) {
          <form [formGroup]="newTask" class="grid panel" (ngSubmit)="addTask()" aria-label="Add task">
            <label>Phase<select formControlName="phase">@for (p of phases; track p.code) {<option [value]="p.code">{{ p.label }}</option>}</select></label>
            <label>Assignee<select formControlName="assigneeId">
              @for (u of users(); track u.id) {<option [value]="u.id">{{ u.displayName }}</option>}</select></label>
            <label>Due date<input type="date" formControlName="dueDate" /></label>
            <label class="wide">Task<textarea formControlName="description" rows="2"></textarea></label>
            <div class="end"><button type="submit" class="secondary" [disabled]="busy() || newTask.invalid">Add task</button></div>
          </form>
        }
      </section>

      @if (available()?.canSetDates) {
        <section aria-labelledby="dates-title">
          <h2 id="dates-title">Effective date</h2>
          <form [formGroup]="dates" class="grid panel" (ngSubmit)="saveDates()" aria-label="Set dates">
            <label>Effective date<input type="date" formControlName="effectiveDate" /></label>
            <label>Implementation due<input type="date" formControlName="implementationDue" /></label>
            <div class="end"><button type="submit" class="secondary" [disabled]="busy() || dates.pristine">Save dates</button></div>
          </form>
        </section>
      }

      <pos-workflow-panel entity="qms.change" [recordId]="c.id" [recordLabel]="c.ccNo" [recordVersion]="c.rowVersion"
        [actions]="available()?.actions ?? []" [returnPath]="'/changes/' + c.id" [blocked]="unsaved"
        [transition]="transition" (done)="afterTransition($event)" (failed)="fail($event)" />

      <section aria-labelledby="hist-title">
        <h2 id="hist-title">Workflow history</h2>
        <pos-history-table [entries]="history()" [label]="label" />
      </section>
      <pos-signatures entity="qms.change" [recordId]="c.id" [refresh]="c.rowVersion" />
      <pos-audit-trail schema="qms" table="change_control" [recordId]="c.id" [refresh]="c.rowVersion" />
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
    .grid label.check { flex-direction: row; align-items: center; gap: 6px; }
    .wide { grid-column: 1 / -1; }
    .end { grid-column: 1 / -1; display: flex; justify-content: flex-end; gap: 8px; }
    .panel { border: 1px solid var(--pos-border); border-radius: 6px; padding: 12px; margin: 8px 0 12px; }
    .text { white-space: pre-wrap; }
    table { width: 100%; border-collapse: collapse; font-size: 0.9rem; }
    th, td { text-align: left; border-bottom: 1px solid var(--pos-border); padding: 4px 8px; vertical-align: top; }
    tr.withdrawn { opacity: 0.55; text-decoration: line-through; }
    .req { color: var(--pos-danger); }
  `,
})
export class ChangeDetailPage implements OnInit {
  private readonly api = inject(ChangeApi);
  private readonly capa = inject(CapaApi);
  private readonly esig = inject(EsigApi);
  private readonly router = inject(Router);

  readonly id = input.required<string>();
  /** Set when Keycloak sends the user back after re-authenticating for a signature. */
  readonly esigChallenge = input<string | undefined>(undefined, { alias: 'esig' });

  protected readonly states = CHANGE_STATES;
  protected readonly types = CHANGE_TYPES;
  protected readonly categories = CATEGORIES;
  protected readonly regulatory = REGULATORY_IMPACTS;
  protected readonly areas = IMPACT_AREAS;
  protected readonly phases = PHASES;
  protected readonly label = changeStateLabel;
  readonly change = signal<ChangeControl | null>(null);
  readonly available = signal<ChangeAvailable | null>(null);
  readonly history = signal<HistoryEntry[]>([]);
  readonly users = signal<AppUser[]>([]);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly busy = signal(false);
  private etag = '';
  private doneLabel: string | null = null;

  readonly header = new FormGroup({
    title: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    description: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    justification: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    changeType: new FormControl('PLANNED', { nonNullable: true }),
    changeCategory: new FormControl('MINOR', { nonNullable: true }),
    regulatoryImpact: new FormControl('NONE', { nonNullable: true }),
    isTemporary: new FormControl(false, { nonNullable: true }),
    temporaryEndDate: new FormControl('', { nonNullable: true }),
    implementationDue: new FormControl('', { nonNullable: true }),
  });
  readonly impact = new FormGroup({
    impactedArea: new FormControl('QUALITY', { nonNullable: true }),
    isImpacted: new FormControl(true, { nonNullable: true }),
    assessment: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    actionRequired: new FormControl('', { nonNullable: true }),
  });
  readonly newTask = new FormGroup({
    phase: new FormControl('IMPLEMENTATION', { nonNullable: true }),
    description: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    assigneeId: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    dueDate: new FormControl(today(30), { nonNullable: true, validators: [Validators.required] }),
  });
  readonly dates = new FormGroup({
    effectiveDate: new FormControl('', { nonNullable: true }),
    implementationDue: new FormControl('', { nonNullable: true }),
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
        if (this.doneLabel) this.message.set(`${this.doneLabel}: now ${changeStateLabel(value.status)}.`);
        this.doneLabel = null;
        if (available.canPlanTasks && this.users().length === 0) {
          this.capa.users().subscribe({
            next: (u) => {
              this.users.set(u);
              if (!this.newTask.controls.assigneeId.value) this.newTask.patchValue({ assigneeId: value.ownerId });
            },
          });
        }
      },
      error: (e: unknown) => this.fail(describeError(e)),
    });
  }

  /** Unsaved description or date changes block workflow actions. */
  readonly unsaved = (): string | null => (this.header.dirty || this.dates.dirty ? 'Save or discard your changes first.' : null);

  saveHeader(): void {
    const v = this.header.getRawValue();
    this.mutate(
      this.api.updateHeader(this.id(), this.etag, {
        ...v, temporaryEndDate: orNull(v.temporaryEndDate), implementationDue: orNull(v.implementationDue),
      }),
      'Saved.',
    );
  }

  saveImpact(): void {
    const v = this.impact.getRawValue();
    this.mutate(
      this.api.saveImpact(this.id(), this.etag, v.impactedArea, {
        isImpacted: v.isImpacted, assessment: v.assessment, actionRequired: orNull(v.actionRequired),
      }),
      `${this.areaLabel(v.impactedArea)} assessment saved.`,
      () => this.impact.patchValue({ assessment: '', actionRequired: '' }),
    );
  }

  editImpact(area: string): void {
    const i = this.change()?.impacts.find((x) => x.impactedArea === area);
    if (i) this.impact.reset({ impactedArea: i.impactedArea, isImpacted: i.isImpacted, assessment: i.assessment ?? '', actionRequired: i.actionRequired ?? '' });
  }

  removeImpact(area: string): void {
    this.mutate(this.api.removeImpact(this.id(), this.etag, area), `${this.areaLabel(area)} assessment removed.`);
  }

  addTask(): void {
    this.mutate(this.api.addTask(this.id(), this.etag, this.newTask.getRawValue()), 'Task added.', () =>
      this.newTask.patchValue({ description: '' }),
    );
  }

  withdraw(no: number): void {
    this.mutate(this.api.updateTask(this.id(), this.etag, no, { withdrawn: true }), `Task ${no} withdrawn.`);
  }

  complete(no: number): void {
    this.mutate(this.api.completeTask(this.id(), this.etag, no), `Task ${no} done.`);
  }

  saveDates(): void {
    const v = this.dates.getRawValue();
    this.mutate(
      this.api.setImplementation(this.id(), this.etag, { effectiveDate: orNull(v.effectiveDate), implementationDue: orNull(v.implementationDue) }),
      'Dates saved.',
    );
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
    return [...CHANGE_TYPES, ...CATEGORIES, ...REGULATORY_IMPACTS].find((t) => t.code === code)?.label ?? code;
  }

  protected areaLabel(code: string): string {
    return IMPACT_AREAS.find((a) => a.code === code)?.label ?? code;
  }

  protected phaseLabel(code: string): string {
    return PHASES.find((p) => p.code === code)?.label ?? code;
  }

  protected isDone(state: string, current: string): boolean {
    const order = CHANGE_STATES.map((s) => s.code);
    return current === 'CLOSED' || order.indexOf(state) < order.indexOf(current);
  }

  protected time(iso: string): string {
    return formatTimestamp(iso);
  }

  private mutate(call: Observable<Versioned<ChangeControl>>, done: string, after?: () => void): void {
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

  private apply(c: ChangeControl, etag: string): void {
    this.change.set(c);
    this.etag = etag;
    this.header.reset({
      title: c.title, description: c.description, justification: c.justification, changeType: c.changeType, changeCategory: c.changeCategory,
      regulatoryImpact: c.regulatoryImpact ?? 'NONE', isTemporary: c.isTemporary, temporaryEndDate: c.temporaryEndDate ?? '',
      implementationDue: c.implementationDue ?? '',
    });
    this.dates.reset({ effectiveDate: c.effectiveDate ?? '', implementationDue: c.implementationDue ?? '' });
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
    void this.router.navigate(['/changes', this.id()], { replaceUrl: true });
  }

  private clear(): void {
    this.error.set(null);
    this.message.set(null);
  }
}
