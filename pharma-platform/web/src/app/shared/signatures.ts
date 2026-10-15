import { Component, effect, inject, input, signal } from '@angular/core';
import { describeError, EsigApi, formatTimestamp, Signature } from '../core/api/api';

/**
 * Signatures on one record (§11.50 manifestation): printed name, date and time, meaning, and the version
 * signed. A signature on an older version is marked as such until the record is signed again (DS §10.5).
 */
@Component({
  selector: 'pos-signatures',
  template: `
    <section class="signatures" aria-labelledby="sig-title">
      <h2 id="sig-title">Electronic signatures</h2>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      } @else if (items() === null) {
        <p>Loading…</p>
      } @else if (items()!.length === 0) {
        <p>Not signed.</p>
      } @else {
        <ul>
          @for (s of items(); track s.id) {
            <li [class.stale]="!s.appliesToCurrentVersion">
              <strong>{{ s.meaningLabel }}</strong> by {{ s.signerName }}@if (s.signerLogin) { ({{ s.signerLogin }})}
              · {{ time(s.signedAt) }} · version {{ s.recordVersion }}
              @if (s.appliesToCurrentVersion) {
                <span class="tag ok">current version</span>
              } @else {
                <span class="tag warn">record changed since (now version {{ currentVersion() }})</span>
              }
              @if (s.reason) {<div class="reason">Reason: {{ s.reason }}</div>}
              @if (s.manifest) {
                <details><summary>Signature manifest</summary><pre>{{ s.manifest }}</pre></details>
              }
            </li>
          }
        </ul>
      }
    </section>
  `,
  styles: `
    ul { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 10px; }
    li { border: 1px solid var(--pos-border); border-radius: 6px; padding: 8px 12px; }
    li.stale { opacity: 0.8; }
    .tag { font-size: 0.8rem; padding: 1px 6px; border-radius: 4px; margin-left: 6px; }
    .tag.ok { background: var(--pos-ok-bg, #e6f4ea); }
    .tag.warn { background: var(--pos-warning-bg, #fff4e5); }
    .reason { font-size: 0.9rem; margin-top: 4px; }
    pre { white-space: pre-wrap; font-size: 0.8rem; margin: 6px 0 0; }
  `,
})
export class Signatures {
  private readonly api = inject(EsigApi);

  readonly entity = input.required<string>();
  readonly recordId = input.required<string>();
  /** Change to reload (e.g. the record version after a save, or a counter after signing). */
  readonly refresh = input<unknown>();

  readonly items = signal<Signature[] | null>(null);
  readonly currentVersion = signal<number | null>(null);
  readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      this.refresh();
      this.api.forRecord(this.entity(), this.recordId()).subscribe({
        next: (r) => {
          this.currentVersion.set(r.currentVersion);
          this.items.set(r.items);
          this.error.set(null);
        },
        error: (e: unknown) => this.error.set(describeError(e)),
      });
    });
  }

  protected time(iso: string): string {
    return formatTimestamp(iso);
  }
}
