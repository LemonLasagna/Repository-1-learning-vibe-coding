import { HttpClient, HttpHeaders, HttpResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { map, Observable } from 'rxjs';
import { AvailableAction } from '../../shared/workflow-panel';
import { APP_CONFIG, Versioned } from './api';
import { HistoryEntry } from './deviation-api';

export interface CapaAction {
  no: number;
  actionType: string;
  description: string;
  assigneeId: string;
  assigneeName: string | null;
  dueDate: string;
  status: 'PLANNED' | 'DONE' | 'WITHDRAWN';
  completedAt: string | null;
  evidence: string | null;
  /** The change control this action is implemented through, if any. */
  changeId: string | null;
  changeNo: string | null;
}

export interface CapaEffectiveness {
  criteria: string;
  method: string | null;
  plannedDate: string;
  performedAt: string | null;
  result: 'EFFECTIVE' | 'INEFFECTIVE' | null;
  evidence: string | null;
  performedByName: string | null;
}

export interface Capa {
  id: string;
  capaNo: string;
  siteId: string;
  siteCode: string;
  sourceType: 'DEVIATION' | 'OTHER';
  sourceId: string | null;
  sourceNo: string | null;
  title: string;
  problemStatement: string;
  capaType: string;
  priority: string;
  ownerId: string;
  ownerName: string | null;
  dueDate: string;
  status: string;
  closedAt: string | null;
  rowVersion: number;
  updatedAt: string;
  actions: CapaAction[];
  effectiveness: CapaEffectiveness | null;
}

export interface CapaAvailable {
  state: string;
  canEditPlan: boolean;
  canCompleteActions: boolean;
  canRecordEffectiveness: boolean;
  canLinkChanges: boolean;
  actions: AvailableAction[];
}

export interface AppUser {
  id: string;
  username: string;
  displayName: string;
}

export const CAPA_STATES: { code: string; label: string }[] = [
  { code: 'DRAFT', label: 'Draft plan' },
  { code: 'PLAN_REVIEW', label: 'Plan review' },
  { code: 'IMPLEMENTATION', label: 'Implementation' },
  { code: 'EFFECTIVENESS_CHECK', label: 'Effectiveness check' },
  { code: 'CLOSED', label: 'Closed' },
];

export function capaStateLabel(code: string): string {
  return code === 'CANCELLED' ? 'Cancelled' : (CAPA_STATES.find((s) => s.code === code)?.label ?? code);
}

@Injectable({ providedIn: 'root' })
export class CapaApi {
  private readonly http = inject(HttpClient);
  private readonly apiBase = inject(APP_CONFIG).apiBase;
  private readonly base = `${this.apiBase}/api/qms/v1/capas`;

  users(): Observable<AppUser[]> {
    return this.http.get<AppUser[]>(`${this.apiBase}/api/iam/v1/users`);
  }

  list(filter: { sourceId?: string; status?: string } = {}): Observable<Capa[]> {
    const params: Record<string, string> = {};
    if (filter.sourceId) params['sourceId'] = filter.sourceId;
    if (filter.status) params['status'] = filter.status;
    return this.http.get<{ items: Capa[] }>(this.base, { params }).pipe(map((r) => r.items));
  }

  get(id: string): Observable<Versioned<Capa>> {
    return this.http.get<Capa>(`${this.base}/${id}`, { observe: 'response' }).pipe(map(versioned));
  }

  create(body: Record<string, unknown>): Observable<Versioned<Capa>> {
    return this.http.post<Capa>(this.base, body, { observe: 'response' }).pipe(map(versioned));
  }

  /** Every change below is checked against the CAPA's ETag and returns the CAPA with its new ETag. */
  updateHeader(id: string, etag: string, body: Record<string, unknown>): Observable<Versioned<Capa>> {
    return this.send('PATCH', `${this.base}/${id}`, etag, body);
  }

  addAction(id: string, etag: string, body: Record<string, unknown>): Observable<Versioned<Capa>> {
    return this.send('POST', `${this.base}/${id}/actions`, etag, body);
  }

  updateAction(id: string, etag: string, no: number, body: Record<string, unknown>): Observable<Versioned<Capa>> {
    return this.send('PATCH', `${this.base}/${id}/actions/${no}`, etag, body);
  }

  completeAction(id: string, etag: string, no: number, evidence: string): Observable<Versioned<Capa>> {
    return this.send('POST', `${this.base}/${id}/actions/${no}/complete`, etag, { evidence });
  }

  linkChange(id: string, etag: string, no: number, changeId: string): Observable<Versioned<Capa>> {
    return this.send('PUT', `${this.base}/${id}/actions/${no}/change`, etag, { changeId });
  }

  unlinkChange(id: string, etag: string, no: number): Observable<Versioned<Capa>> {
    return this.send('DELETE', `${this.base}/${id}/actions/${no}/change`, etag, null);
  }

  defineEffectiveness(id: string, etag: string, body: { criteria: string; method: string | null; plannedDate: string }): Observable<Versioned<Capa>> {
    return this.send('PUT', `${this.base}/${id}/effectiveness`, etag, body);
  }

  recordEffectiveness(id: string, etag: string, result: string, evidence: string): Observable<Versioned<Capa>> {
    return this.send('POST', `${this.base}/${id}/effectiveness/result`, etag, { result, evidence });
  }

  transition(id: string, etag: string, action: string, reason: string | null): Observable<Versioned<Capa>> {
    return this.send('POST', `${this.base}/${id}/transitions`, etag, { action, reason });
  }

  available(id: string): Observable<CapaAvailable> {
    return this.http.get<CapaAvailable>(`${this.base}/${id}/available-actions`);
  }

  history(id: string): Observable<HistoryEntry[]> {
    return this.http.get<HistoryEntry[]>(`${this.base}/${id}/history`);
  }

  private send(method: string, url: string, etag: string, body: unknown): Observable<Versioned<Capa>> {
    const headers = new HttpHeaders({ 'If-Match': etag });
    return this.http.request<Capa>(method, url, { body, headers, observe: 'response' }).pipe(map(versioned));
  }
}

function versioned<T>(res: HttpResponse<T>): Versioned<T> {
  const etag = res.headers.get('ETag');
  if (res.body === null || etag === null) throw new Error('Response is missing a body or ETag');
  return { value: res.body, etag };
}
