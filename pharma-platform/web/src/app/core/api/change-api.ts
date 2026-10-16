import { HttpClient, HttpHeaders, HttpResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { map, Observable } from 'rxjs';
import { AvailableAction } from '../../shared/workflow-panel';
import { APP_CONFIG, Versioned } from './api';
import { HistoryEntry } from './deviation-api';

export interface ChangeImpact {
  impactedArea: string;
  assessorId: string;
  assessorName: string | null;
  isImpacted: boolean;
  assessment: string | null;
  actionRequired: string | null;
  updatedAt: string;
}

export interface ChangeTask {
  no: number;
  phase: string;
  description: string;
  assigneeId: string;
  assigneeName: string | null;
  dueDate: string;
  status: 'PLANNED' | 'DONE' | 'WITHDRAWN';
  completedAt: string | null;
}

export interface ChangeControl {
  id: string;
  ccNo: string;
  siteId: string;
  siteCode: string;
  title: string;
  description: string;
  justification: string;
  changeType: string;
  changeCategory: string;
  isTemporary: boolean;
  temporaryEndDate: string | null;
  regulatoryImpact: string | null;
  implementationDue: string | null;
  effectiveDate: string | null;
  ownerId: string;
  ownerName: string | null;
  status: string;
  rowVersion: number;
  updatedAt: string;
  impacts: ChangeImpact[];
  tasks: ChangeTask[];
}

export interface ChangeAvailable {
  state: string;
  canEditHeader: boolean;
  canAssess: boolean;
  canPlanTasks: boolean;
  canCompleteTasks: boolean;
  canSetDates: boolean;
  actions: AvailableAction[];
}

export const CHANGE_STATES: { code: string; label: string }[] = [
  { code: 'DRAFT', label: 'Draft' },
  { code: 'ASSESSMENT', label: 'Assessment' },
  { code: 'APPROVAL', label: 'Approval' },
  { code: 'IMPLEMENTATION', label: 'Implementation' },
  { code: 'POST_REVIEW', label: 'Post-implementation review' },
  { code: 'CLOSURE', label: 'Closure' },
  { code: 'CLOSED', label: 'Closed' },
];

export const IMPACT_AREAS: { code: string; label: string }[] = [
  { code: 'QUALITY', label: 'Quality' },
  { code: 'VALIDATION', label: 'Validation' },
  { code: 'DOCUMENTATION', label: 'Documentation' },
  { code: 'REGULATORY', label: 'Regulatory' },
  { code: 'MANUFACTURING', label: 'Manufacturing' },
  { code: 'LABORATORY', label: 'Laboratory' },
  { code: 'TRAINING', label: 'Training' },
];

export function changeStateLabel(code: string): string {
  return code === 'CANCELLED' ? 'Cancelled' : (CHANGE_STATES.find((s) => s.code === code)?.label ?? code);
}

@Injectable({ providedIn: 'root' })
export class ChangeApi {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiBase}/api/qms/v1/changes`;

  list(filter: { status?: string } = {}): Observable<ChangeControl[]> {
    const params: Record<string, string> = {};
    if (filter.status) params['status'] = filter.status;
    return this.http.get<{ items: ChangeControl[] }>(this.base, { params }).pipe(map((r) => r.items));
  }

  get(id: string): Observable<Versioned<ChangeControl>> {
    return this.http.get<ChangeControl>(`${this.base}/${id}`, { observe: 'response' }).pipe(map(versioned));
  }

  create(body: Record<string, unknown>): Observable<Versioned<ChangeControl>> {
    return this.http.post<ChangeControl>(this.base, body, { observe: 'response' }).pipe(map(versioned));
  }

  /** Every change below is checked against the change control's ETag and returns it with its new ETag. */
  updateHeader(id: string, etag: string, body: Record<string, unknown>): Observable<Versioned<ChangeControl>> {
    return this.send('PATCH', `${this.base}/${id}`, etag, body);
  }

  saveImpact(id: string, etag: string, area: string, body: Record<string, unknown>): Observable<Versioned<ChangeControl>> {
    return this.send('PUT', `${this.base}/${id}/impacts/${area}`, etag, body);
  }

  removeImpact(id: string, etag: string, area: string): Observable<Versioned<ChangeControl>> {
    return this.send('DELETE', `${this.base}/${id}/impacts/${area}`, etag, null);
  }

  addTask(id: string, etag: string, body: Record<string, unknown>): Observable<Versioned<ChangeControl>> {
    return this.send('POST', `${this.base}/${id}/tasks`, etag, body);
  }

  updateTask(id: string, etag: string, no: number, body: Record<string, unknown>): Observable<Versioned<ChangeControl>> {
    return this.send('PATCH', `${this.base}/${id}/tasks/${no}`, etag, body);
  }

  completeTask(id: string, etag: string, no: number): Observable<Versioned<ChangeControl>> {
    return this.send('POST', `${this.base}/${id}/tasks/${no}/complete`, etag, null);
  }

  setImplementation(id: string, etag: string, body: Record<string, unknown>): Observable<Versioned<ChangeControl>> {
    return this.send('PATCH', `${this.base}/${id}/implementation`, etag, body);
  }

  transition(id: string, etag: string, action: string, reason: string | null): Observable<Versioned<ChangeControl>> {
    return this.send('POST', `${this.base}/${id}/transitions`, etag, { action, reason });
  }

  available(id: string): Observable<ChangeAvailable> {
    return this.http.get<ChangeAvailable>(`${this.base}/${id}/available-actions`);
  }

  history(id: string): Observable<HistoryEntry[]> {
    return this.http.get<HistoryEntry[]>(`${this.base}/${id}/history`);
  }

  private send(method: string, url: string, etag: string, body: unknown): Observable<Versioned<ChangeControl>> {
    const headers = new HttpHeaders({ 'If-Match': etag });
    return this.http.request<ChangeControl>(method, url, { body, headers, observe: 'response' }).pipe(map(versioned));
  }
}

function versioned<T>(res: HttpResponse<T>): Versioned<T> {
  const etag = res.headers.get('ETag');
  if (res.body === null || etag === null) throw new Error('Response is missing a body or ETag');
  return { value: res.body, etag };
}
