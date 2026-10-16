import { HttpClient, HttpHeaders, HttpResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { map, Observable } from 'rxjs';
import { APP_CONFIG, Versioned } from './api';

export interface WorkflowTransition {
  action: string;
  from: string;
  to: string;
  label: string | null;
  permission: string | null;
  guard: unknown;
  guardMessage: string | null;
  signatureMeaning: string | null;
  requireReason: boolean;
}

/** A record type's workflow as new records start on it. */
export interface CurrentWorkflow {
  code: string;
  recordType: string;
  name: string;
  version: number;
  initialState: string;
  transitions: WorkflowTransition[];
}

export type DraftStatus = 'DRAFT' | 'VALID' | 'SUBMITTED' | 'APPROVED' | 'ACTIVE' | 'CANCELLED';

export interface WorkflowDraft {
  id: string;
  code: string;
  version: number;
  initialState: string;
  transitions: WorkflowTransition[];
  status: DraftStatus;
  errors: string[];
  submittedBy: string | null;
  submittedAt: string | null;
  approvedBy: string | null;
  approvedAt: string | null;
  decisionComment: string | null;
  activatedAt: string | null;
  rowVersion: number;
  updatedAt: string;
}

export interface DraftContent {
  code: string;
  version: number;
  initialState: string;
  transitions: WorkflowTransition[];
}

export type DraftStep = 'submit' | 'approve' | 'reject' | 'activate';

export const DRAFT_STATUS_LABELS: Record<DraftStatus, string> = {
  DRAFT: 'Draft (has problems)',
  VALID: 'Draft (valid)',
  SUBMITTED: 'Submitted',
  APPROVED: 'Approved',
  ACTIVE: 'Active',
  CANCELLED: 'Cancelled',
};

@Injectable({ providedIn: 'root' })
export class WorkflowApi {
  private readonly http = inject(HttpClient);
  private readonly root = `${inject(APP_CONFIG).apiBase}/api/workflow/v1`;
  private readonly base = `${this.root}/definitions`;

  workflows(): Observable<CurrentWorkflow[]> {
    return this.http.get<{ items: CurrentWorkflow[] }>(`${this.root}/workflows`).pipe(map((r) => r.items));
  }

  list(): Observable<WorkflowDraft[]> {
    return this.http.get<{ items: WorkflowDraft[] }>(this.base).pipe(map((r) => r.items));
  }

  get(id: string): Observable<Versioned<WorkflowDraft>> {
    return this.http.get<WorkflowDraft>(`${this.base}/${id}`, { observe: 'response' }).pipe(map(versioned));
  }

  create(content: DraftContent): Observable<Versioned<WorkflowDraft>> {
    return this.http.post<WorkflowDraft>(this.base, content, { observe: 'response' }).pipe(map(versioned));
  }

  replace(id: string, etag: string, content: DraftContent): Observable<Versioned<WorkflowDraft>> {
    return this.send('PUT', `${this.base}/${id}`, etag, content);
  }

  validate(id: string): Observable<{ valid: boolean; errors: string[] }> {
    return this.http.post<{ valid: boolean; errors: string[] }>(`${this.base}/${id}/validate`, {});
  }

  /** Submit, approve, reject (needs a comment) or activate, at the version last read. */
  step(id: string, etag: string, step: DraftStep, comment: string | null): Observable<Versioned<WorkflowDraft>> {
    return this.send('POST', `${this.base}/${id}/${step}`, etag, { comment });
  }

  private send(method: string, url: string, etag: string, body: unknown): Observable<Versioned<WorkflowDraft>> {
    const headers = new HttpHeaders({ 'If-Match': etag });
    return this.http.request<WorkflowDraft>(method, url, { body, headers, observe: 'response' }).pipe(map(versioned));
  }
}

function versioned<T>(res: HttpResponse<T>): Versioned<T> {
  const etag = res.headers.get('ETag');
  if (res.body === null || etag === null) throw new Error('Response is missing a body or ETag');
  return { value: res.body, etag };
}
