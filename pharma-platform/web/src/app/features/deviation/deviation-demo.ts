import { HttpClient } from '@angular/common/http';
import { Component, inject, OnInit, signal, viewChild } from '@angular/core';
import { MetadataForm } from '../../core/metadata/metadata-form';
import { RuntimeBundle, TransitionDef } from '../../core/metadata/runtime-bundle';
import { canonicalJson, Json } from '../../core/rules/rule-evaluator';
import { EsigDialog, SignIntent, SignSubmission } from '../../shared/esig-dialog';

/**
 * Demonstrates the metadata renderer and the signature flow on a Deviation bundle served as a
 * static file. When config-studio exists it serves the same bundle from
 * GET /api/meta/v1/entities/{code}/runtime?state=… and this page loads it from there instead.
 */
@Component({
  selector: 'pos-deviation-demo',
  imports: [MetadataForm, EsigDialog],
  template: `
    <p class="banner info" role="note">
      Demo screen. The form is generated from <code>metadata/deviation.bundle.json</code>. The e-signature
      and workflow services are not built yet, so signing here shows what would be sent and nothing is saved.
    </p>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    @if (bundle(); as b) {
      <header class="record-header">
        <h1>{{ b.entityLabel }} <small>DEV-2026-00412 · version {{ version }}</small></h1>
        <span class="state">● {{ b.workflowState }}</span>
      </header>
      <pos-metadata-form [bundle]="b" [initialValue]="initial" />
      <div class="actions">
        @for (t of b.transitions; track t.action) {
          <button type="button" (click)="startTransition(t)">{{ t.label }}@if (t.signatureMeaning) { ✍}</button>
        }
      </div>
      @if (outcome()) {<p class="banner ok" role="status">{{ outcome() }}</p>}
    }
    @if (intent(); as i) {
      <pos-esig-dialog [intent]="i" (signed)="onSigned($event)" (cancelled)="intent.set(null)" />
    }
  `,
  styles: `
    .record-header { display: flex; flex-wrap: wrap; align-items: baseline; gap: 12px; }
    .record-header small { font-size: 0.9rem; font-weight: 400; color: var(--pos-muted); }
    .state { font-weight: 600; color: var(--pos-accent); }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; justify-content: flex-end; }
  `,
})
export class DeviationDemoPage implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly form = viewChild(MetadataForm);

  readonly bundle = signal<RuntimeBundle | null>(null);
  readonly error = signal<string | null>(null);
  readonly intent = signal<SignIntent | null>(null);
  readonly outcome = signal<string | null>(null);
  readonly version = 7;
  readonly initial: Record<string, Json> = {
    title: 'Temperature excursion in Granulation Room G-104',
    classification: 'MAJOR',
    occurred_at: '2026-10-02T03:14',
    description: 'Room temperature exceeded 25 °C for 41 minutes during the night shift.',
  };
  private pending: TransitionDef | null = null;

  ngOnInit(): void {
    this.http.get<RuntimeBundle>('metadata/deviation.bundle.json').subscribe({
      next: (b) => this.bundle.set(b),
      error: () => this.error.set('Could not load the form definition.'),
    });
  }

  async startTransition(t: TransitionDef): Promise<void> {
    this.outcome.set(null);
    const form = this.form();
    if (!form?.checkValid()) {
      this.error.set('Complete the required fields before continuing.');
      return;
    }
    this.error.set(null);
    if (!t.signatureMeaning) {
      this.outcome.set(`"${t.label}" would now be sent to the workflow service.`);
      return;
    }
    this.pending = t;
    this.intent.set({
      entity: 'DEVIATION',
      recordNumber: 'DEV-2026-00412',
      recordVersion: this.version,
      recordHash: await sha256Hex(canonicalJson(form.rawValue())),
      meaningCode: t.signatureMeaning,
      meaningLabel: t.signatureMeaningLabel ?? t.signatureMeaning,
      meaningStatement: t.signatureStatement ?? '',
      signerName: 'Jane Doe',
      signerLogin: 'jdoe',
      reasonRequired: false,
      secondFactorRequired: true,
    });
  }

  onSigned(s: SignSubmission): void {
    const i = this.intent();
    this.intent.set(null);
    this.outcome.set(
      `Signature intent prepared for "${this.pending?.label}" (${i?.meaningLabel}) on version ${i?.recordVersion}, ` +
        `record hash ${i?.recordHash.slice(0, 12)}…${s.reason ? `, reason "${s.reason}"` : ''}. ` +
        'Nothing was sent: the e-signature service is not built yet.',
    );
  }
}

async function sha256Hex(text: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('');
}
