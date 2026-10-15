import { Component, DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { APP_CONFIG, AuthService } from './core/api/api';
import { InboxApi } from './core/api/inbox-api';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly config = inject(APP_CONFIG);
  protected readonly auth = inject(AuthService);
  protected readonly inbox = inject(InboxApi);

  constructor() {
    // Refresh the "My work" count after each navigation (e.g. returning from a signature or a transition).
    inject(Router).events.pipe(filter((e) => e instanceof NavigationEnd), takeUntilDestroyed(inject(DestroyRef)))
      .subscribe(() => {
        if (this.auth.user()) this.inbox.get().subscribe({ error: () => this.inbox.counts.set(null) });
      });
  }
}
