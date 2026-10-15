import { Component, input } from '@angular/core';
import { formatTimestamp } from '../core/api/api';
import { HistoryEntry } from '../core/api/deviation-api';

/** Workflow history of a record: each step, who took it, under which signature, and why. */
@Component({
  selector: 'pos-history-table',
  template: `
    <table>
      <thead><tr><th>When</th><th>Step</th><th>By</th><th>Signature</th><th>Comment</th></tr></thead>
      <tbody>
        @for (h of entries(); track $index) {
          <tr>
            <td>{{ time(h.occurredAt) }}</td>
            <td>{{ h.fromState ? label()(h.fromState) + ' → ' : '' }}{{ label()(h.toState) }}</td>
            <td>{{ h.actorName ?? '—' }}</td>
            <td>{{ h.signatureMeaning ?? '—' }}</td>
            <td>{{ h.comment ?? '' }}</td>
          </tr>
        }
      </tbody>
    </table>
  `,
  styles: `
    table { width: 100%; border-collapse: collapse; font-size: 0.9rem; }
    th, td { text-align: left; border-bottom: 1px solid var(--pos-border); padding: 4px 8px; }
  `,
})
export class HistoryTable {
  readonly entries = input.required<HistoryEntry[]>();
  /** State code → label. */
  readonly label = input.required<(code: string) => string>();

  protected time(iso: string): string {
    return formatTimestamp(iso);
  }
}
