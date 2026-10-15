import { Component, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

/** What the user is about to sign: shown in full before they confirm (FS-ESIG-001). */
export interface SignIntent {
  entity: string;
  recordNumber: string;
  recordVersion: number;
  meaningCode: string;
  meaningLabel: string;
  meaningStatement: string;
  signerName: string;
  signerLogin: string;
  reasonRequired: boolean;
  /** The legally-binding statement, when the user has not acknowledged it yet; otherwise null. */
  acknowledgementText: string | null;
}

export interface SignConfirmation {
  reason: string | null;
  acknowledged: boolean;
}

/**
 * Confirmation step of an electronic signature (21 CFR Part 11 §11.50, §11.200). The user sees what they
 * sign and why, then continues to the sign-in page, which asks for the password again. The password is never
 * typed into this application; the server binds the signature to the record version and hash.
 */
@Component({
  selector: 'pos-esig-dialog',
  imports: [FormsModule],
  template: `
    <div class="backdrop">
      <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="esig-title">
        <h2 id="esig-title">Electronic signature</h2>
        <dl class="intent">
          <dt>You are signing</dt>
          <dd>{{ intent().recordNumber }} (version {{ intent().recordVersion }})</dd>
          <dt>Meaning</dt>
          <dd><strong>{{ intent().meaningLabel }}</strong>: "{{ intent().meaningStatement }}"</dd>
          <dt>Signer</dt>
          <dd>{{ intent().signerName }} ({{ intent().signerLogin }})</dd>
        </dl>

        <form (ngSubmit)="submit()">
          <label for="esig-reason">Reason / comment @if (intent().reasonRequired) {<span class="req">*</span>}</label>
          <textarea id="esig-reason" name="reason" rows="2" maxlength="1000" [(ngModel)]="reason"
            [required]="intent().reasonRequired"></textarea>

          @if (intent().acknowledgementText; as text) {
            <label class="ack">
              <input type="checkbox" name="ack" [(ngModel)]="acknowledged" />
              <span>{{ text }}</span>
            </label>
          }

          <p class="next">Next, the sign-in page asks for your password to confirm that you are signing.
            Your password is never entered in this application.</p>

          <div class="actions">
            <button type="button" class="secondary" (click)="cancelled.emit()">Cancel</button>
            <button type="submit" [disabled]="!canSubmit()">Continue to sign</button>
          </div>
        </form>
      </div>
    </div>
  `,
  styles: `
    .backdrop { position: fixed; inset: 0; background: rgb(0 0 0 / 0.45); display: grid; place-items: center; z-index: 10; padding: 16px; }
    .dialog { background: var(--pos-surface); color: var(--pos-text); border-radius: 8px; padding: 20px 24px; width: min(520px, 100%); max-height: 100%; overflow: auto; }
    h2 { margin: 0 0 12px; font-size: 1.15rem; }
    .intent { display: grid; grid-template-columns: max-content 1fr; gap: 4px 12px; margin: 0 0 16px; font-size: 0.9rem; }
    dt { color: var(--pos-muted); }
    dd { margin: 0; }
    form { display: flex; flex-direction: column; gap: 6px; }
    .ack { display: flex; gap: 8px; align-items: flex-start; font-size: 0.9rem; margin-top: 8px; }
    .next { font-size: 0.85rem; color: var(--pos-muted); margin: 8px 0; }
    .actions { display: flex; justify-content: flex-end; gap: 8px; }
    .req { color: var(--pos-danger); }
  `,
})
export class EsigDialog {
  readonly intent = input.required<SignIntent>();
  readonly confirmed = output<SignConfirmation>();
  readonly cancelled = output<void>();

  protected reason = '';
  protected acknowledged = false;
  private readonly submitting = signal(false);

  canSubmit(): boolean {
    const i = this.intent();
    return (
      !this.submitting() &&
      (!i.reasonRequired || this.reason.trim().length > 0) &&
      (i.acknowledgementText === null || this.acknowledged)
    );
  }

  submit(): void {
    if (!this.canSubmit()) return;
    this.submitting.set(true);
    this.confirmed.emit({ reason: this.reason.trim() || null, acknowledged: this.intent().acknowledgementText !== null });
  }
}
