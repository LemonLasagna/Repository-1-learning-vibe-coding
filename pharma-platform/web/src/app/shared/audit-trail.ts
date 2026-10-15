import { Component, inject, input, signal, effect } from '@angular/core';
import { AuditApi, AuditEntry, describeError, formatTimestamp } from '../core/api/api';

/** Field-level history of one record (FS-REC-007): who, when (with UTC offset), why, old → new. */
@Component({
  selector: 'pos-audit-trail',
  template: `
    <section class="audit" aria-labelledby="audit-title">
      <h2 id="audit-title">Audit trail</h2>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      } @else if (entries() === null) {
        <p>Loading…</p>
      } @else if (entries()!.length === 0) {
        <p>No entries.</p>
      } @else {
        <table>
          <thead>
            <tr><th>When</th><th>Action</th><th>User</th><th>Reason</th><th>Changes</th></tr>
          </thead>
          <tbody>
            @for (e of entries(); track $index) {
              <tr>
                <td>{{ time(e.occurredAt) }}</td>
                <td>{{ e.operation }}</td>
                <td>{{ e.actorDisplay ?? e.actorUserId }}</td>
                <td>{{ e.reason ?? '—' }}</td>
                <td>
                  <ul>
                    @for (c of changes(e); track c.field) {
                      <li><strong>{{ c.field }}</strong>:
                        @if (e.operation === 'UPDATE') {<span class="old">{{ c.old }}</span> → }
                        <span class="new">{{ c.new }}</span></li>
                    }
                  </ul>
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    </section>
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; font-size: 0.875rem; }
    th, td { text-align: left; border-bottom: 1px solid var(--pos-border); padding: 6px 8px; vertical-align: top; }
    ul { margin: 0; padding-left: 16px; }
    .old { text-decoration: line-through; color: var(--pos-muted); }
    .error { color: var(--pos-danger); }
    .audit { overflow-x: auto; }
  `,
})
export class AuditTrail {
  private readonly api = inject(AuditApi);

  readonly schema = input.required<string>();
  readonly table = input.required<string>();
  readonly recordId = input.required<string>();
  /** Bump to reload after a save. */
  readonly refresh = input(0);

  readonly entries = signal<AuditEntry[] | null>(null);
  readonly error = signal<string | null>(null);

  private static readonly hiddenFields = new Set(['id', 'tenant_id', 'ext', 'row_version', 'created_at', 'created_by', 'updated_at', 'updated_by']);

  constructor() {
    effect((onCleanup) => {
      this.refresh();
      const sub = this.api.forRecord(this.schema(), this.table(), this.recordId()).subscribe({
        next: (e) => {
          this.entries.set(e);
          this.error.set(null);
        },
        error: (err: unknown) => this.error.set(describeError(err)),
      });
      onCleanup(() => sub.unsubscribe());
    });
  }

  time(iso: string): string {
    return formatTimestamp(iso);
  }

  changes(e: AuditEntry): { field: string; old: string; new: string }[] {
    const fields = e.operation === 'UPDATE' ? e.changedFields : Object.keys(e.newValues ?? e.oldValues ?? {});
    return fields
      .filter((f) => !AuditTrail.hiddenFields.has(f))
      .map((f) => ({ field: f, old: show(e.oldValues?.[f]), new: show(e.newValues?.[f]) }));
  }
}

function show(v: unknown): string {
  if (v === null || v === undefined) return '∅';
  return typeof v === 'string' ? v : JSON.stringify(v);
}
