import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { describeError, Uom, UomApi } from '../../core/api/api';
import { AuditTrail } from '../../shared/audit-trail';

export const DIMENSIONS = ['MASS', 'VOLUME', 'LENGTH', 'COUNT', 'TIME', 'TEMPERATURE', 'AMOUNT'];

/** Reference CRUD screens over the real API: list + create. */
@Component({
  selector: 'pos-uom-list',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <h1>Units of measure</h1>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}

    <form [formGroup]="createForm" (ngSubmit)="create()" class="create" aria-label="Create unit of measure">
      <label>Code <input formControlName="code" maxlength="50" /></label>
      <label>Name <input formControlName="name" maxlength="255" /></label>
      <label>Dimension
        <select formControlName="dimension">
          @for (d of dimensions; track d) {<option [value]="d">{{ d }}</option>}
        </select>
      </label>
      <button type="submit" [disabled]="createForm.invalid || busy()">Create</button>
    </form>

    @if (items() === null) {
      <p>Loading…</p>
    } @else {
      <table>
        <thead><tr><th>Code</th><th>Name</th><th>Dimension</th><th>Version</th></tr></thead>
        <tbody>
          @for (u of items(); track u.id) {
            <tr>
              <td><a [routerLink]="['/uoms', u.id]">{{ u.code }}</a></td>
              <td>{{ u.name }}</td>
              <td>{{ u.dimension }}</td>
              <td>{{ u.rowVersion }}</td>
            </tr>
          } @empty {
            <tr><td colspan="4">No units of measure yet.</td></tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: `
    .create { display: flex; flex-wrap: wrap; gap: 8px 12px; align-items: end; margin: 0 0 16px; }
    .create label { display: flex; flex-direction: column; font-size: 0.875rem; gap: 4px; }
    table { width: 100%; border-collapse: collapse; }
    th, td { text-align: left; border-bottom: 1px solid var(--pos-border); padding: 6px 8px; }
  `,
})
export class UomListPage implements OnInit {
  private readonly api = inject(UomApi);
  private readonly router = inject(Router);

  protected readonly dimensions = DIMENSIONS;
  readonly items = signal<Uom[] | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  readonly createForm = new FormGroup({
    code: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(50)] }),
    name: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(255)] }),
    dimension: new FormControl('MASS', { nonNullable: true, validators: [Validators.required] }),
  });

  ngOnInit(): void {
    this.api.list().subscribe({
      next: (items) => this.items.set(items),
      error: (e: unknown) => {
        this.items.set([]);
        this.error.set(describeError(e));
      },
    });
  }

  create(): void {
    if (this.createForm.invalid) return;
    this.busy.set(true);
    this.api.create(this.createForm.getRawValue()).subscribe({
      next: ({ value }) => void this.router.navigate(['/uoms', value.id]),
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(describeError(e));
      },
    });
  }
}

/** Detail + edit with ETag/If-Match and a mandatory reason for change (FS-REC-003/004). */
@Component({
  selector: 'pos-uom-detail',
  imports: [ReactiveFormsModule, RouterLink, AuditTrail],
  template: `
    <p><a routerLink="/uoms">← Units of measure</a></p>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    @if (saved()) {<p class="banner ok" role="status">Saved as version {{ uom()?.rowVersion }}.</p>}

    @if (uom(); as u) {
      <h1>{{ u.code }} <small>version {{ u.rowVersion }}</small></h1>
      <form [formGroup]="editForm" (ngSubmit)="save()" class="edit">
        <label>Name <input formControlName="name" maxlength="255" /></label>
        <label>Dimension
          <select formControlName="dimension">
            @for (d of dimensions; track d) {<option [value]="d">{{ d }}</option>}
          </select>
        </label>
        <label class="wide"><span>Reason for change <span class="req">*</span></span>
          <input formControlName="reason" maxlength="2000" placeholder="e.g. Align with EU labelling (CC-2026-0042)" />
        </label>
        <div class="actions">
          @if (stale()) {<button type="button" class="secondary" (click)="load()">Reload latest version</button>}
          <button type="submit" [disabled]="editForm.invalid || editForm.pristine || busy()">Save</button>
        </div>
      </form>
      <pos-audit-trail schema="mdm" table="uom" [recordId]="u.id" [refresh]="u.rowVersion" />
    } @else if (!error()) {
      <p>Loading…</p>
    }
  `,
  styles: `
    h1 small { font-size: 0.9rem; font-weight: 400; color: var(--pos-muted); }
    .edit { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 12px; margin: 0 0 24px; }
    .edit label { display: flex; flex-direction: column; font-size: 0.875rem; gap: 4px; }
    .wide { grid-column: 1 / -1; }
    .actions { grid-column: 1 / -1; display: flex; gap: 8px; justify-content: flex-end; }
    .req { color: var(--pos-danger); }
  `,
})
export class UomDetailPage implements OnInit {
  private readonly api = inject(UomApi);

  readonly id = input.required<string>();
  protected readonly dimensions = DIMENSIONS;
  readonly uom = signal<Uom | null>(null);
  readonly error = signal<string | null>(null);
  readonly stale = signal(false);
  readonly saved = signal(false);
  readonly busy = signal(false);
  private etag = '';

  readonly editForm = new FormGroup({
    name: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(255)] }),
    dimension: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    reason: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(/\S/)] }),
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.api.get(this.id()).subscribe({
      next: ({ value, etag }) => this.apply(value, etag),
      error: (e: unknown) => this.error.set(describeError(e)),
    });
  }

  save(): void {
    if (this.editForm.invalid) return;
    const { name, dimension, reason } = this.editForm.getRawValue();
    this.busy.set(true);
    this.saved.set(false);
    this.api.update(this.id(), this.etag, reason.trim(), { name, dimension }).subscribe({
      next: ({ value, etag }) => {
        this.apply(value, etag);
        this.saved.set(true);
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.stale.set((e as { status?: number }).status === 412);
        this.error.set(describeError(e));
      },
    });
  }

  private apply(u: Uom, etag: string): void {
    this.uom.set(u);
    this.etag = etag;
    this.stale.set(false);
    this.error.set(null);
    this.busy.set(false);
    this.editForm.reset({ name: u.name, dimension: u.dimension, reason: '' });
  }
}
