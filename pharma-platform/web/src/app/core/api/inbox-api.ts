import { HttpClient } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { APP_CONFIG } from './api';

export interface InboxItem {
  entity: string;
  recordId: string;
  number: string;
  title: string;
  state: string;
  task: string;
  /** DO: the user does the work; SIGN: the user signs; WAITING: it waits on something else first. */
  kind: 'DO' | 'SIGN' | 'WAITING';
  detail: string | null;
  dueDate: string | null;
  overdue: boolean;
}

export interface Inbox {
  items: InboxItem[];
  counts: { total: number; overdue: number; actionable: number };
}

/** Route of a record in the app, by entity code. */
export function recordLink(item: Pick<InboxItem, 'entity' | 'recordId'>): string[] {
  return item.entity === 'qms.capa' ? ['/capas', item.recordId] : ['/deviations', item.recordId];
}

/** "My work" for the signed-in user; keeps the counts the shell shows next to the menu entry. */
@Injectable({ providedIn: 'root' })
export class InboxApi {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(APP_CONFIG).apiBase}/api/work/v1/inbox`;

  /** Items the user can act on now, and how many are overdue; null until loaded. */
  readonly counts = signal<Inbox['counts'] | null>(null);

  get(): Observable<Inbox> {
    return this.http.get<Inbox>(this.base).pipe(tap((inbox) => this.counts.set(inbox.counts)));
  }
}
