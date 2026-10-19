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
  /** AGPL-3.0 section 13: users who interact with the software over a network are offered its source. */
  protected readonly sourceUrl = 'https://github.com/LemonLasagna/Repository-1-learning-vibe-coding';

  constructor() {
    // Refresh the "My work" count after each navigation (e.g. returning from a signature or a transition).
    inject(Router).events.pipe(filter((e) => e instanceof NavigationEnd), takeUntilDestroyed(inject(DestroyRef)))
      .subscribe(() => {
        if (this.auth.user()) this.inbox.get().subscribe({ error: () => this.inbox.counts.set(null) });
      });
  }
}
