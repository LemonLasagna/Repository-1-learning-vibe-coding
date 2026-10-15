import { Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable } from 'rxjs';
import { AuthService, describeError, EsigApi, SigningOptions } from '../core/api/api';
import { EsigDialog, SignConfirmation, SignIntent } from './esig-dialog';

/** A workflow action the current user may see, and what stands in its way (GET …/available-actions). */
export interface AvailableAction {
  action: string;
  label: string;
  to: string;
  signatureMeaning: string | null;
  requiresReason: boolean;
  /** Why the action cannot be taken yet (a guard), or null. */
  blockedBy: string | null;
}

/**
 * The workflow actions of one record: plain transitions (with a reason prompt where needed) and signed ones, which
 * open the e-signature dialog, start a challenge carrying the action, and send the user to Keycloak. The page
 * completes the signature when Keycloak sends the user back to <code>returnPath?esig=…</code>.
 */
@Component({
  selector: 'pos-workflow-panel',
  imports: [FormsModule, EsigDialog],
  template: `
    @if (actions().length) {
      <section class="workflow" aria-label="Actions">
        @for (a of actions(); track a.action) {
          <div class="action">
            <button type="button" [class.secondary]="secondary(a)" [disabled]="busy() || !!a.blockedBy" (click)="start(a)">
              {{ a.label }}@if (a.signatureMeaning) { ✍}</button>
            @if (a.blockedBy) {<small>{{ a.blockedBy }}</small>}
          </div>
        }
      </section>
    }
    @if (reasonFor(); as a) {
      <div class="reason-panel" role="group" aria-label="Reason">
        <label>Reason for "{{ a.label }}" <span class="req">*</span>
          <textarea name="transitionReason" rows="2" maxlength="1000" [(ngModel)]="reason"></textarea></label>
        <div class="buttons">
          <button type="button" class="secondary" (click)="reasonFor.set(null)">Cancel</button>
          <button type="button" [disabled]="!reason.trim() || busy()" (click)="run(a, reason.trim())">Confirm</button>
        </div>
      </div>
    }
    @if (intent(); as i) {
      <pos-esig-dialog [intent]="i" (confirmed)="confirmSigning($event)" (cancelled)="intent.set(null)" />
    }
  `,
  styles: `
    .workflow { display: flex; flex-wrap: wrap; gap: 12px; margin: 16px 0; }
    .action { display: flex; flex-direction: column; gap: 2px; max-width: 320px; }
    .action small { color: var(--pos-muted); font-size: 0.8rem; }
    .reason-panel { border: 1px solid var(--pos-border); border-radius: 6px; padding: 12px; margin: 0 0 16px; max-width: 520px; }
    .reason-panel label { display: flex; flex-direction: column; gap: 4px; font-size: 0.875rem; }
    .buttons { display: flex; justify-content: flex-end; gap: 8px; margin-top: 8px; }
    .req { color: var(--pos-danger); }
  `,
})
export class WorkflowPanel {
  private readonly esig = inject(EsigApi);
  private readonly auth = inject(AuthService);

  /** Entity code for signatures, e.g. "qms.capa". */
  readonly entity = input.required<string>();
  readonly recordId = input.required<string>();
  /** Shown in the signature dialog, e.g. the record number. */
  readonly recordLabel = input.required<string>();
  readonly recordVersion = input.required<number>();
  readonly actions = input.required<AvailableAction[]>();
  /** Where Keycloak sends the user back after signing; the challenge id is appended as ?esig=. */
  readonly returnPath = input.required<string>();
  /** Asked when an action is clicked: a message refuses it (e.g. unsaved changes), null lets it go ahead. */
  readonly blocked = input<() => string | null>(() => null);
  /** Runs a transition without a signature. */
  readonly transition = input.required<(action: string, reason: string | null) => Observable<unknown>>();

  /** Emits the label of the action that was carried out. */
  readonly done = output<string>();
  readonly failed = output<string>();

  readonly busy = signal(false);
  readonly reasonFor = signal<AvailableAction | null>(null);
  readonly intent = signal<SignIntent | null>(null);
  private readonly options = signal<SigningOptions | null>(null);
  protected reason = '';
  private pending: AvailableAction | null = null;

  constructor() {
    effect(() => {
      const entity = this.entity();
      if (!untracked(() => this.auth.hasPermission('esig.sign'))) return;
      this.esig.options(entity).subscribe({ next: (o) => this.options.set(o), error: () => this.options.set(null) });
    });
  }

  protected secondary(a: AvailableAction): boolean {
    return a.action === 'cancel' || a.action.startsWith('reject') || a.action.startsWith('return');
  }

  start(a: AvailableAction): void {
    const blocked = this.blocked()();
    if (blocked) {
      this.failed.emit(blocked);
      return;
    }
    if (a.signatureMeaning) this.openSigning(a);
    else if (a.requiresReason) {
      this.reason = '';
      this.reasonFor.set(a);
    } else this.run(a, null);
  }

  run(a: AvailableAction, reason: string | null): void {
    this.busy.set(true);
    this.reasonFor.set(null);
    this.transition()(a.action, reason).subscribe({
      next: () => {
        this.busy.set(false);
        this.done.emit(a.label);
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.failed.emit(describeError(e));
      },
    });
  }

  private openSigning(a: AvailableAction): void {
    const user = this.auth.user();
    const options = this.options();
    const meaning = options?.meanings.find((m) => m.code === a.signatureMeaning);
    if (!user || !options || !meaning) {
      this.failed.emit('You cannot sign: the e-signature permission is missing.');
      return;
    }
    this.pending = a;
    this.intent.set({
      entity: this.entity(),
      recordNumber: `${this.recordLabel()} (${a.label})`,
      recordVersion: this.recordVersion(),
      meaningCode: meaning.code,
      meaningLabel: meaning.label,
      meaningStatement: meaning.statement,
      signerName: user.name ?? user.login,
      signerLogin: user.login,
      reasonRequired: a.requiresReason || meaning.requiresReason,
      acknowledgementText: options.statement.acknowledged ? null : options.statement.text,
    });
  }

  confirmSigning(c: SignConfirmation): void {
    const a = this.pending;
    if (!a?.signatureMeaning) return;
    this.esig
      .challenge({
        entity: this.entity(), recordId: this.recordId(), recordVersion: this.recordVersion(), meaning: a.signatureMeaning,
        reason: c.reason, acknowledge: c.acknowledged, action: a.action,
      })
      .subscribe({
        next: ({ challengeId }) => this.auth.reauthenticate(`${this.returnPath()}?esig=${encodeURIComponent(challengeId)}`),
        error: (e: unknown) => {
          this.intent.set(null);
          this.failed.emit(describeError(e));
        },
      });
  }
}
