import { Component, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

/** What the user is about to sign: shown in full before any credential is entered (FS-ESIG-001). */
export interface SignIntent {
  entity: string;
  recordNumber: string;
  recordVersion: number;
  recordHash: string;
  meaningCode: string;
  meaningLabel: string;
  meaningStatement: string;
  signerName: string;
  signerLogin: string;
  reasonRequired: boolean;
  secondFactorRequired: boolean;
}

export interface SignSubmission {
  password: string;
  otp: string | null;
  reason: string | null;
}

/**
 * Re-authentication dialog for an electronic signature (21 CFR Part 11 §11.50, §11.200).
 * It only collects credentials; the esignature service verifies them through a Keycloak step-up
 * and binds the result to the record hash (DS §10.5). Credentials are never stored client-side.
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
          <dd>{{ intent().recordNumber }} (version {{ intent().recordVersion }},
            hash <code [title]="intent().recordHash">{{ shortHash() }}</code>)</dd>
          <dt>Meaning</dt>
          <dd><strong>{{ intent().meaningLabel }}</strong>: "{{ intent().meaningStatement }}"</dd>
          <dt>Signer</dt>
          <dd>{{ intent().signerName }} ({{ intent().signerLogin }})</dd>
        </dl>

        <form (ngSubmit)="submit()" #f="ngForm">
          <label for="esig-user">User ID</label>
          <input id="esig-user" [value]="intent().signerLogin" readonly />

          <label for="esig-password">Password</label>
          <input id="esig-password" type="password" name="password" autocomplete="current-password"
            [(ngModel)]="password" required />

          @if (intent().secondFactorRequired) {
            <label for="esig-otp">One-time code</label>
            <input id="esig-otp" name="otp" inputmode="numeric" autocomplete="one-time-code"
              [(ngModel)]="otp" required />
          }

          <label for="esig-reason">Reason / comment @if (intent().reasonRequired) {<span class="req">*</span>}</label>
          <textarea id="esig-reason" name="reason" rows="2" [(ngModel)]="reason"
            [required]="intent().reasonRequired"></textarea>

          <p class="statement">This electronic signature is the legally binding equivalent of your handwritten signature.</p>

          <div class="actions">
            <button type="button" class="secondary" (click)="cancelled.emit()">Cancel</button>
            <button type="submit" [disabled]="!canSubmit()">Sign</button>
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
    .statement { font-size: 0.85rem; color: var(--pos-warning); margin: 8px 0; }
    .actions { display: flex; justify-content: flex-end; gap: 8px; }
    .req { color: var(--pos-danger); }
  `,
})
export class EsigDialog {
  readonly intent = input.required<SignIntent>();
  readonly signed = output<SignSubmission>();
  readonly cancelled = output<void>();

  protected password = '';
  protected otp = '';
  protected reason = '';
  private readonly submitting = signal(false);

  readonly shortHash = computed(() => {
    const h = this.intent().recordHash;
    return h.length > 12 ? `${h.slice(0, 4)}…${h.slice(-4)}` : h;
  });

  canSubmit(): boolean {
    const i = this.intent();
    return (
      !this.submitting() &&
      this.password.length > 0 &&
      (!i.secondFactorRequired || this.otp.trim().length > 0) &&
      (!i.reasonRequired || this.reason.trim().length > 0)
    );
  }

  submit(): void {
    if (!this.canSubmit()) return;
    this.submitting.set(true);
    const submission: SignSubmission = {
      password: this.password,
      otp: this.intent().secondFactorRequired ? this.otp.trim() : null,
      reason: this.reason.trim() || null,
    };
    this.password = '';
    this.otp = '';
    this.signed.emit(submission);
  }
}
