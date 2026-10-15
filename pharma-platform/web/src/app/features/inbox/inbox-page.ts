import { Component, inject, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService, describeError } from '../../core/api/api';
import { InboxApi, InboxItem, recordLink } from '../../core/api/inbox-api';

/** Everything waiting on the signed-in user, overdue first. */
@Component({
  selector: 'pos-inbox',
  imports: [RouterLink],
  template: `
    <h1>My work</h1>
    @if (error()) {<p class="banner error" role="alert">{{ error() }}</p>}
    @if (auth.user() === null) {
      <p>Sign in to see what is waiting for you.</p>
    } @else if (items() === null) {
      <p>Loading…</p>
    } @else if (items()!.length === 0) {
      <p class="empty">Nothing is waiting for you.</p>
    } @else {
      <ul class="items">
        @for (i of items(); track i.entity + i.recordId + i.task) {
          <li [class.overdue]="i.overdue" [class.waiting]="i.kind === 'WAITING'">
            <div class="what">
              <span class="kind" [attr.data-kind]="i.kind">{{ kindLabel(i) }}</span>
              <strong>{{ i.task }}</strong>
              @if (i.detail) {<span class="detail">{{ i.detail }}</span>}
            </div>
            <div class="record">
              <a [routerLink]="link(i)">{{ i.number }}</a> {{ i.title }}
            </div>
            <div class="due">
              @if (i.dueDate) {Due {{ i.dueDate }}}
              @if (i.overdue) {<span class="late">overdue</span>}
            </div>
          </li>
        }
      </ul>
    }
  `,
  styles: `
    .items { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 8px; }
    li { display: grid; grid-template-columns: 1fr 1fr max-content; gap: 4px 16px; align-items: center;
         border: 1px solid var(--pos-border); border-radius: 6px; padding: 10px 12px; }
    li.overdue { border-left: 4px solid var(--pos-danger); }
    li.waiting { opacity: 0.75; }
    .what { display: flex; flex-direction: column; gap: 2px; }
    .detail { font-size: 0.85rem; color: var(--pos-muted); }
    .kind { font-size: 0.75rem; font-weight: 600; text-transform: uppercase; color: var(--pos-muted); }
    .kind[data-kind='SIGN'] { color: var(--pos-accent, #1f5fa8); }
    .due { font-size: 0.85rem; text-align: right; }
    .late { color: var(--pos-danger); font-weight: 600; margin-left: 6px; }
    .empty { color: var(--pos-muted); }
    @media (max-width: 640px) { li { grid-template-columns: 1fr; } .due { text-align: left; } }
  `,
})
export class InboxPage implements OnInit {
  private readonly api = inject(InboxApi);
  protected readonly auth = inject(AuthService);
  readonly items = signal<InboxItem[] | null>(null);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    if (!this.auth.user()) return;
    this.api.get().subscribe({
      next: (inbox) => this.items.set(inbox.items),
      error: (e: unknown) => {
        this.items.set([]);
        this.error.set(describeError(e));
      },
    });
  }

  protected link(i: InboxItem): string[] {
    return recordLink(i);
  }

  protected kindLabel(i: InboxItem): string {
    return i.kind === 'SIGN' ? 'Sign ✍' : i.kind === 'WAITING' ? 'Waiting' : 'To do';
  }
}
