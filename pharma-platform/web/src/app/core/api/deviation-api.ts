import { HttpClient, HttpHeaders, HttpResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { map, Observable } from 'rxjs';
import { Json } from '../rules/rule-evaluator';
import { AvailableAction } from '../../shared/workflow-panel';
import { APP_CONFIG, Versioned } from './api';

export interface Site {
  id: string;
  code: string;
  name: string;
  timezone: string;
}

export interface Deviation {
  id: string;
  deviationNo: string;
  siteId: string;
  siteCode: string;
  title: string;
  description: string;
  classification: string;
  deviationType: string;
  isPlanned: boolean;
  occurredAt: string;
  discoveredAt: string;
  immediateAction: string | null;
  impactAssessment: string | null;
  rootCauseSummary: string | null;
  riskScore: number | null;
  plannedJustification: string | null;
  shift: string | null;
  batchesAffected: number | null;
  ownerId: string;
  ownerName: string | null;
  qaOwnerId: string | null;
  qaOwnerName: string | null;
  dueDate: string;
  closedAt: string | null;
  status: string;
  rowVersion: number;
  updatedAt: string;
}

/** A workflow action the user may see; see the shared workflow panel. */
export type DeviationAction = AvailableAction;

export interface DeviationActions {
  state: string;
  /** API field names the user may change in this state. */
  editableFields: string[];
  reasonForChangeRequired: boolean;
  actions: DeviationAction[];
}

export interface HistoryEntry {
  occurredAt: string;
  fromState: string | null;
  toState: string;
  actorName: string | null;
  comment: string | null;
  signatureMeaning: string | null;
}

export const DEVIATION_STATES: { code: string; label: string }[] = [
  { code: 'DRAFT', label: 'Draft' },
  { code: 'TRIAGE', label: 'Triage' },
  { code: 'INVESTIGATION', label: 'Investigation' },
  { code: 'QA_APPROVAL', label: 'QA approval' },
  { code: 'CLOSED', label: 'Closed' },
];

export function stateLabel(code: string): string {
  return code === 'CANCELLED' ? 'Cancelled' : (DEVIATION_STATES.find((s) => s.code === code)?.label ?? code);
}

/** Form-bundle field code ↔ API field, and how values convert between the form and the API. */
const FIELDS: { code: string; api: keyof Deviation; kind?: 'datetime' }[] = [
  { code: 'title', api: 'title' },
  { code: 'classification', api: 'classification' },
  { code: 'deviation_type', api: 'deviationType' },
  { code: 'occurred_at', api: 'occurredAt', kind: 'datetime' },
  { code: 'shift', api: 'shift' },
  { code: 'description', api: 'description' },
  { code: 'is_planned', api: 'isPlanned' },
  { code: 'planned_justification', api: 'plannedJustification' },
  { code: 'immediate_action', api: 'immediateAction' },
  { code: 'batches_affected', api: 'batchesAffected' },
  { code: 'impact_assessment', api: 'impactAssessment' },
  { code: 'root_cause_summary', api: 'rootCauseSummary' },
  { code: 'risk_score', api: 'riskScore' },
  { code: 'due_date', api: 'dueDate' },
];

export function apiField(code: string): string | undefined {
  return FIELDS.find((f) => f.code === code)?.api;
}

/** API record → form values (datetimes as the browser's local time for datetime-local inputs). */
export function toFormValue(d: Deviation): Record<string, Json> {
  const v: Record<string, Json> = {};
  for (const f of FIELDS) {
    const raw = d[f.api] as Json;
    v[f.code] = f.kind === 'datetime' && typeof raw === 'string' ? toLocalInput(raw) : raw;
  }
  return v;
}

/** Form values → API body, only for the given form fields; empty strings become null. */
export function toApiBody(values: Record<string, Json>, codes: Iterable<string>): Record<string, Json> {
  const body: Record<string, Json> = {};
  for (const code of codes) {
    const f = FIELDS.find((x) => x.code === code);
    if (!f) continue;
    let v = values[code] ?? null;
    if (v === '') v = null;
    if (f.kind === 'datetime' && typeof v === 'string') v = new Date(v).toISOString();
    body[f.api] = v;
  }
  return body;
}

function toLocalInput(iso: string): string {
  const d = new Date(iso);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
}

@Injectable({ providedIn: 'root' })
export class DeviationApi {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(APP_CONFIG).apiBase;
  private readonly base = `${this.apiBase}/api/qms/v1/deviations`;

  sites(): Observable<Site[]> {
    return this.http.get<Site[]>(`${this.apiBase}/api/org/v1/sites`);
  }

  list(status?: string): Observable<Deviation[]> {
    const params: Record<string, string> = status ? { status } : {};
    return this.http.get<{ items: Deviation[] }>(this.base, { params }).pipe(map((r) => r.items));
  }

  get(id: string): Observable<Versioned<Deviation>> {
    return this.http.get<Deviation>(`${this.base}/${id}`, { observe: 'response' }).pipe(map(versioned));
  }

  create(body: Record<string, Json>): Observable<Versioned<Deviation>> {
    return this.http.post<Deviation>(this.base, body, { observe: 'response' }).pipe(map(versioned));
  }

  update(id: string, etag: string, reason: string | null, body: Record<string, Json>): Observable<Versioned<Deviation>> {
    let headers = new HttpHeaders({ 'If-Match': etag });
    if (reason) headers = headers.set('X-Change-Reason', reason);
    return this.http.patch<Deviation>(`${this.base}/${id}`, body, { headers, observe: 'response' }).pipe(map(versioned));
  }

  transition(id: string, etag: string, action: string, reason: string | null): Observable<Versioned<Deviation>> {
    const headers = new HttpHeaders({ 'If-Match': etag });
    return this.http
      .post<Deviation>(`${this.base}/${id}/transitions`, { action, reason }, { headers, observe: 'response' })
      .pipe(map(versioned));
  }

  actions(id: string): Observable<DeviationActions> {
    return this.http.get<DeviationActions>(`${this.base}/${id}/available-actions`);
  }

  history(id: string): Observable<HistoryEntry[]> {
    return this.http.get<HistoryEntry[]>(`${this.base}/${id}/history`);
  }
}

function versioned<T>(res: HttpResponse<T>): Versioned<T> {
  const etag = res.headers.get('ETag');
  if (res.body === null || etag === null) throw new Error('Response is missing a body or ETag');
  return { value: res.body, etag };
}
