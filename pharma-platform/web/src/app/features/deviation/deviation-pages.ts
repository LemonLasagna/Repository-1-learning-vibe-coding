import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, input, OnInit, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { forkJoin, of } from 'rxjs';
import { AuthService, describeError, EsigApi, formatTimestamp } from '../../core/api/api';
import { Capa, CapaApi, capaStateLabel } from '../../core/api/capa-api';
import {
  apiField, DEVIATION_STATES, Deviation, DeviationActions, DeviationApi, HistoryEntry, Site, stateLabel,
  toApiBody, toFormValue,
} from '../../core/api/deviation-api';
import { MetadataForm } from '../../core/metadata/metadata-form';
import { Access, RuntimeBundle } from '../../core/metadata/runtime-bundle';
import { AuditTrail } from '../../shared/audit-trail';
import { HistoryTable } from '../../shared/history-table';
import { Signatures } from '../../shared/signatures';
import { WorkflowPanel } from '../../shared/workflow-panel';

const BUNDLE_URL = '/metadata/deviation.bundle.json';

/** The form bundle with each field's access set from what the server allows in this state. */
function withAccess(bundle: RuntimeBundle, access: (code: string) => Access): RuntimeBundle {
  return { ...bundle, stateAccess: Object.fromEntries(bundle.fields.map((f) => [f.code, access(f.code)])) };
}

/** Deviations: list with status filter. */
@Component({
  selector: 'pos-deviation-list',
  imports: [FormsModule, RouterLink],
  template: `
    <div class="head">
      <h1>Deviations</h1>
      @if (canCreate()) {<a class="button" routerLink="/deviations/new">New deviation</a>}
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
        <thead><tr><th>Number</th><th>Title</th><th>Status</th><th>Class.</th><th>Site</th><th>Owner</th><th>Due</th></tr></thead>
        <tbody>
          @for (d of items(); track d.id) {
            <tr>
              <td><a [routerLink]="['/deviations', d.id]">{{ d.deviationNo }}</a></td>
              <td>{{ d.title }}</td>
              <td><span class="state" [attr.data-state]="d.status">{{ label(d.status) }}</span></td>
              <td>{{ d.classification }}</td>
              <td>{{ d.siteCode }}</td>
              <td>{{ d.ownerName }}</td>
              <td>{{ d.dueDate }}</td>
            </tr>
          } @empty {
            <tr><td colspan="7">No deviations.</td></tr>
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
export class DeviationListPage implements OnInit {
  private readonly api = inject(DeviationApi);
  private readonly auth = inject(AuthService);
  protected readonly states = DEVIATION_STATES;
  readonly items = signal<Deviation[] | null>(null);
  readonly error = signal<string | null>(null);
  readonly status = signal('');
  readonly canCreate = computed(() => this.auth.hasPermission('qms.deviation:write'));

  ngOnInit(): void {
    this.load();
  }

  filter(status: string): void {
    this.status.set(status);
    this.load();
  }

  protected label(code: string): string {
    return stateLabel(code);
  }

  private load(): void {
    this.api.list(this.status() || undefined).subscribe({
      next: (items) => this.items.set(items),
      error: (e: unknown) => {
        this.items.set([]);
        this.error.set(describeError(e));
      },
    });
  }
}

/** Raise a deviation: site plus the configured form. It starts as a draft owned by the user. */
@Component({
  selector: 'pos-deviation-create',
  imports: [FormsModule, MetadataForm, RouterLink],
  template: `
    <p><a routerLink="/deviations">← Deviations</a></p>
    <h1>New deviation</h1>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    <label class="site">Site <span class="req">*</span>
      <select name="site" [ngModel]="siteId()" (ngModelChange)="siteId.set($event)">
        <option [ngValue]="null">Select…</option>
        @for (s of sites(); track s.id) {<option [ngValue]="s.id">{{ s.code }} · {{ s.name }}</option>}
      </select>
    </label>
    @if (bundle(); as b) {
      <pos-metadata-form [bundle]="b" />
      <div class="actions">
        <button type="button" [disabled]="busy()" (click)="save()">Save draft</button>
      </div>
    } @else {
      <p>Loading…</p>
    }
  `,
  styles: `
    .site { display: flex; flex-direction: column; gap: 4px; max-width: 320px; margin: 0 0 12px; font-size: 0.875rem; }
    .actions { display: flex; justify-content: flex-end; margin-top: 12px; }
    .req { color: var(--pos-danger); }
  `,
})
export class DeviationCreatePage implements OnInit {
  private readonly api = inject(DeviationApi);
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly form = viewChild(MetadataForm);

  readonly sites = signal<Site[]>([]);
  readonly siteId = signal<string | null>(null);
  readonly bundle = signal<RuntimeBundle | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  ngOnInit(): void {
    forkJoin([this.api.sites(), this.http.get<RuntimeBundle>(BUNDLE_URL)]).subscribe({
      next: ([sites, bundle]) => {
        this.sites.set(sites);
        if (sites.length === 1) this.siteId.set(sites[0].id);
        // The due date is set by the server (discovery + 30 days) and by QA at triage.
        this.bundle.set(withAccess(bundle, (code) => (code === 'due_date' ? 'hidden' : 'edit')));
      },
      error: (e: unknown) => this.error.set(describeError(e)),
    });
  }

  save(): void {
    const form = this.form();
    const site = this.siteId();
    if (!form?.checkValid() || !site) {
      this.error.set(site ? 'Complete the required fields.' : 'Choose the site.');
      return;
    }
    const codes = this.bundle()!.fields.map((f) => f.code).filter((c) => c !== 'due_date');
    this.busy.set(true);
    this.api.create({ siteId: site, ...toApiBody(form.rawValue(), codes) }).subscribe({
      next: ({ value }) => void this.router.navigate(['/deviations', value.id]),
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(describeError(e));
      },
    });
  }
}

/**
 * One deviation: the configured form (editable as the server allows in this state), the workflow actions and signing,
 * linked CAPAs, and the record's history, signatures and audit trail.
 */
@Component({
  selector: 'pos-deviation-detail',
  imports: [FormsModule, MetadataForm, RouterLink, AuditTrail, Signatures, WorkflowPanel, HistoryTable],
  template: `
    <p><a routerLink="/deviations">← Deviations</a></p>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    @if (message()) {<p class="banner ok" role="status">{{ message() }}</p>}

    @if (deviation(); as d) {
      <h1>{{ d.deviationNo }} <small>{{ d.title }}</small></h1>
      <ol class="progress" aria-label="Workflow">
        @for (s of states; track s.code) {
          <li [class.current]="s.code === d.status" [class.done]="isDone(s.code, d.status)">{{ s.label }}</li>
        }
        @if (d.status === 'CANCELLED') {<li class="current cancelled">Cancelled</li>}
      </ol>
      <dl class="facts">
        <dt>Site</dt><dd>{{ d.siteCode }}</dd>
        <dt>Owner</dt><dd>{{ d.ownerName }}</dd>
        <dt>QA owner</dt><dd>{{ d.qaOwnerName ?? '—' }}</dd>
        <dt>Due</dt><dd>{{ d.dueDate }}</dd>
        <dt>Version</dt><dd>{{ d.rowVersion }}</dd>
        @if (d.closedAt) {<dt>Closed</dt><dd>{{ time(d.closedAt) }}</dd>}
      </dl>

      @if (formBundle(); as b) {
        <pos-metadata-form [bundle]="b" [initialValue]="initialValue()" />
        @if (canEdit()) {
          <div class="save">
            @if (actions()?.reasonForChangeRequired) {
              <label>Reason for change <span class="req">*</span>
                <input name="reason" [(ngModel)]="changeReason" maxlength="2000" /></label>
            }
            <button type="button" [disabled]="busy()" (click)="save()">Save</button>
          </div>
        }
      }

      <pos-workflow-panel entity="qms.deviation" [recordId]="d.id" [recordLabel]="d.deviationNo" [recordVersion]="d.rowVersion"
        [actions]="actions()?.actions ?? []" [returnPath]="'/deviations/' + d.id" [blocked]="unsaved"
        [transition]="transition" (done)="afterTransition($event)" (failed)="fail($event)" />

      <section aria-labelledby="capa-title">
        <h2 id="capa-title">CAPAs</h2>
        @if (capas().length) {
          <ul class="capas">
            @for (c of capas(); track c.id) {
              <li><a [routerLink]="['/capas', c.id]">{{ c.capaNo }}</a> {{ c.title }} · {{ capaLabel(c.status) }}</li>
            }
          </ul>
        } @else {
          <p>None linked.</p>
        }
        @if (canRaiseCapa()) {
          <a class="button secondary" [routerLink]="['/capas/new']" [queryParams]="{ sourceId: d.id }">Raise CAPA</a>
        }
      </section>

      <section aria-labelledby="hist-title">
        <h2 id="hist-title">Workflow history</h2>
        <pos-history-table [entries]="history()" [label]="label" />
      </section>

      <pos-signatures entity="qms.deviation" [recordId]="d.id" [refresh]="d.rowVersion" />
      <pos-audit-trail schema="qms" table="deviation" [recordId]="d.id" [refresh]="d.rowVersion" />
    } @else if (!error()) {
      <p>Loading…</p>
    }
  `,
  styles: `
    h1 small { font-size: 1rem; font-weight: 400; color: var(--pos-muted); margin-left: 8px; }
    .facts { display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, max-content)); gap: 2px 16px; margin: 0 0 16px; font-size: 0.9rem; }
    .facts dt { color: var(--pos-muted); font-size: 0.8rem; }
    .facts dd { margin: 0 0 6px; }
    .save { display: flex; gap: 12px; align-items: flex-end; justify-content: flex-end; margin: 12px 0; }
    .save label { display: flex; flex-direction: column; font-size: 0.875rem; gap: 4px; flex: 1; max-width: 480px; }
    .capas { margin: 0 0 8px; }
    .req { color: var(--pos-danger); }
  `,
})
export class DeviationDetailPage implements OnInit {
  private readonly api = inject(DeviationApi);
  private readonly capaApi = inject(CapaApi);
  private readonly esig = inject(EsigApi);
  private readonly auth = inject(AuthService);
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly form = viewChild(MetadataForm);

  readonly id = input.required<string>();
  /** Set when Keycloak sends the user back after re-authenticating for a signature. */
  readonly esigChallenge = input<string | undefined>(undefined, { alias: 'esig' });

  protected readonly states = DEVIATION_STATES;
  readonly deviation = signal<Deviation | null>(null);
  readonly actions = signal<DeviationActions | null>(null);
  readonly history = signal<HistoryEntry[]>([]);
  readonly capas = signal<Capa[]>([]);
  readonly bundle = signal<RuntimeBundle | null>(null);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly busy = signal(false);
  protected changeReason = '';
  private etag = '';
  /** The action just carried out; the message names the state it led to once reloaded. */
  private doneLabel: string | null = null;

  readonly initialValue = computed(() => {
    const d = this.deviation();
    return d ? toFormValue(d) : {};
  });

  readonly formBundle = computed(() => {
    const b = this.bundle();
    const a = this.actions();
    if (!b || !a) return null;
    const editable = new Set(a.editableFields);
    return withAccess(b, (code) => (editable.has(apiField(code) ?? '') ? 'edit' : 'read'));
  });

  readonly canEdit = computed(() => (this.actions()?.editableFields.length ?? 0) > 0);
  readonly canRaiseCapa = computed(() => {
    const d = this.deviation();
    return !!d && this.auth.hasPermission('qms.capa:write') && d.status !== 'CLOSED' && d.status !== 'CANCELLED';
  });

  /** Runs an unsigned transition with the ETag last read. */
  readonly transition = (action: string, reason: string | null) => this.api.transition(this.id(), this.etag, action, reason);

  ngOnInit(): void {
    this.http.get<RuntimeBundle>(BUNDLE_URL).subscribe({
      next: (b) => this.bundle.set(b),
      error: (e: unknown) => this.error.set(describeError(e)),
    });
    const challenge = this.esigChallenge();
    if (challenge) this.completeSigning(challenge);
    else this.load();
  }

  load(): void {
    const capas = this.auth.hasPermission('qms.capa:read') ? this.capaApi.list({ sourceId: this.id() }) : of([]);
    forkJoin([this.api.get(this.id()), this.api.actions(this.id()), this.api.history(this.id()), capas]).subscribe({
      next: ([{ value, etag }, actions, history, linked]) => {
        this.deviation.set(value);
        this.etag = etag;
        this.actions.set(actions);
        this.history.set(history);
        this.capas.set(linked);
        if (this.doneLabel) this.message.set(`${this.doneLabel}: now ${stateLabel(value.status)}.`);
        this.doneLabel = null;
        this.changeReason = '';
        this.busy.set(false);
      },
      error: (e: unknown) => this.fail(describeError(e)),
    });
  }

  /** Unsaved form changes block workflow actions. */
  readonly unsaved = (): string | null => (this.form()?.form().dirty ? 'Save or discard your changes first.' : null);

  save(): void {
    const form = this.form();
    const d = this.deviation();
    const a = this.actions();
    if (!form || !d || !a) return;
    if (!form.checkValid()) {
      this.error.set('Complete the required fields.');
      return;
    }
    const before = this.initialValue();
    const after = form.rawValue();
    const editable = new Set(a.editableFields);
    const changed = Object.keys(after).filter(
      (code) => editable.has(apiField(code) ?? '') && JSON.stringify(after[code] ?? null) !== JSON.stringify(before[code] ?? null),
    );
    if (changed.length === 0) {
      this.message.set('Nothing to save.');
      return;
    }
    const reason = this.changeReason.trim() || null;
    if (a.reasonForChangeRequired && !reason) {
      this.error.set('Enter the reason for change.');
      return;
    }
    this.busy.set(true);
    this.clear();
    this.api.update(d.id, this.etag, reason, toApiBody(after, changed)).subscribe({
      next: () => {
        this.message.set('Saved.');
        this.load();
      },
      error: (e: unknown) => this.fail(describeError(e)),
    });
  }

  afterTransition(label: string): void {
    this.clear();
    this.doneLabel = label;
    this.load();
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

  /** A reload must not try to use the challenge again. */
  private forgetChallenge(): void {
    void this.router.navigate(['/deviations', this.id()], { replaceUrl: true });
  }

  private clear(): void {
    this.error.set(null);
    this.message.set(null);
  }

  fail(message: string): void {
    this.busy.set(false);
    this.message.set(null);
    this.error.set(message);
  }

  protected isDone(state: string, current: string): boolean {
    const order = DEVIATION_STATES.map((s) => s.code);
    return current === 'CLOSED' || order.indexOf(state) < order.indexOf(current);
  }

  protected readonly label = stateLabel;
  protected readonly capaLabel = capaStateLabel;

  protected time(iso: string): string {
    return formatTimestamp(iso);
  }
}
