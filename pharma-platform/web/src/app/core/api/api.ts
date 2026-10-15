import { HttpClient, HttpErrorResponse, HttpHeaders, HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { inject, Injectable, InjectionToken, signal } from '@angular/core';
import { catchError, firstValueFrom, map, Observable, of } from 'rxjs';

export interface AppConfig {
  /** Shown in the header so nobody executes validation scripts in PROD by mistake. */
  environment: 'DEV' | 'TEST' | 'CONFIG' | 'VAL' | 'PROD';
  apiBase: string;
}

export const APP_CONFIG = new InjectionToken<AppConfig>('APP_CONFIG', {
  providedIn: 'root',
  factory: () => ({ environment: 'DEV', apiBase: '' }),
});

export interface SessionUser {
  sub: string;
  login: string;
  name: string | null;
  tenantId: string;
  permissions: string[];
  /** When the user last typed their credentials (ISO 8601); used for e-signature re-authentication. */
  authTime: string | null;
  logoutUrl: string | null;
}

/** Full-page navigation, injectable so tests can observe it instead of leaving the page. */
export const NAVIGATE = new InjectionToken<(url: string) => void>('NAVIGATE', {
  providedIn: 'root',
  factory: () => (url: string) => window.location.assign(url),
});

/**
 * Server-side session (backend-for-frontend). The browser never sees a token: the server keeps them,
 * and this service only learns who is signed in from /bff/user. Sign-in and sign-out are full-page
 * navigations to the server, which redirects to and from Keycloak.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly navigate = inject(NAVIGATE);
  private readonly base = inject(APP_CONFIG).apiBase;

  /** undefined while loading, null when signed out. */
  readonly user = signal<SessionUser | null | undefined>(undefined);

  load(): Promise<SessionUser | null> {
    return firstValueFrom(
      this.http.get<SessionUser>(`${this.base}/bff/user`).pipe(catchError(() => of(null))),
    ).then((u) => {
      this.user.set(u);
      return u;
    });
  }

  hasPermission(permission: string): boolean {
    return this.user()?.permissions.includes(permission) ?? false;
  }

  login(returnUrl: string = currentPath()): void {
    this.navigate(`${this.base}/bff/login?returnUrl=${encodeURIComponent(returnUrl)}`);
  }

  /** Forces Keycloak to ask for the password again (e-signature), then returns here. */
  reauthenticate(returnUrl: string = currentPath()): void {
    this.navigate(`${this.base}/bff/reauth?returnUrl=${encodeURIComponent(returnUrl)}`);
  }

  logout(): void {
    const url = this.user()?.logoutUrl;
    if (url) this.navigate(`${this.base}${url}`);
  }
}

function currentPath(): string {
  return `${window.location.pathname}${window.location.search}`;
}

/**
 * Adds the CSRF header the server requires on every API call made with the session cookie, and a
 * correlation id the API copies into the audit trail.
 */
export const gxpHttpInterceptor: HttpInterceptorFn = (req, next) =>
  next(req.clone({ headers: req.headers.set('X-CSRF', '1').set('X-Correlation-Id', crypto.randomUUID()) }));

/** Turns an HTTP error into one sentence a user can act on. Problem Details (RFC 9457) first. */
export function describeError(err: unknown): string {
  if (!(err instanceof HttpErrorResponse)) return 'Unexpected error.';
  const p = err.error as { title?: string; detail?: string; errors?: Record<string, string[]> } | null;
  if (err.status === 0) return 'The server cannot be reached.';
  if (err.status === 401) return 'Your session has ended. Sign in again to continue.';
  if (err.status === 403) return p?.detail ?? 'You do not have permission for this action.';
  if (p?.errors) {
    return Object.entries(p.errors)
      .map(([field, msgs]) => `${field}: ${msgs.join(' ')}`)
      .join(' ');
  }
  return p?.detail ?? p?.title ?? `Request failed (${err.status}).`;
}

export interface Uom {
  id: string;
  code: string;
  name: string;
  dimension: string;
  rowVersion: number;
  updatedAt: string;
}

export interface Versioned<T> {
  value: T;
  etag: string;
}

@Injectable({ providedIn: 'root' })
export class UomApi {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiBase}/api/mdm/v1/uoms`;

  list(): Observable<Uom[]> {
    return this.http.get<{ items: Uom[] }>(this.base).pipe(map((r) => r.items));
  }

  get(id: string): Observable<Versioned<Uom>> {
    return this.http.get<Uom>(`${this.base}/${id}`, { observe: 'response' }).pipe(map(versioned));
  }

  create(req: { code: string; name: string; dimension: string }): Observable<Versioned<Uom>> {
    return this.http.post<Uom>(this.base, req, { observe: 'response' }).pipe(map(versioned));
  }

  /** Optimistic concurrency: the ETag from the last read goes back as If-Match (doc 21 §21.1). */
  update(id: string, etag: string, reason: string, patch: { name?: string; dimension?: string }): Observable<Versioned<Uom>> {
    const headers = new HttpHeaders({ 'If-Match': etag, 'X-Change-Reason': reason });
    return this.http.patch<Uom>(`${this.base}/${id}`, patch, { headers, observe: 'response' }).pipe(map(versioned));
  }
}

function versioned<T>(res: HttpResponse<T>): Versioned<T> {
  const etag = res.headers.get('ETag');
  if (res.body === null || etag === null) throw new Error('Response is missing a body or ETag');
  return { value: res.body, etag };
}

export interface AuditEntry {
  occurredAt: string;
  operation: 'INSERT' | 'UPDATE' | 'DELETE' | 'EVENT';
  changedFields: string[];
  oldValues: Record<string, unknown> | null;
  newValues: Record<string, unknown> | null;
  actorUserId: string;
  actorDisplay: string | null;
  reason: string | null;
}

@Injectable({ providedIn: 'root' })
export class AuditApi {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiBase}/api/audit/v1/records`;

  forRecord(schema: string, table: string, id: string): Observable<AuditEntry[]> {
    return this.http.get<AuditEntry[]>(`${this.base}/${schema}/${table}/${id}`);
  }
}

/** GxP timestamps are shown with their UTC offset (URS-GEN-003). */
export function formatTimestamp(iso: string): string {
  const d = new Date(iso);
  const pad = (n: number) => String(Math.abs(n)).padStart(2, '0');
  const off = -d.getTimezoneOffset();
  const sign = off >= 0 ? '+' : '-';
  return (
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ` +
    `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())} ` +
    `(UTC${sign}${pad(Math.trunc(off / 60))}:${pad(off % 60)})`
  );
}
