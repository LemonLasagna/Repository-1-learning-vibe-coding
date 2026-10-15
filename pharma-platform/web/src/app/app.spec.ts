import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { APP_CONFIG, AuthService, NAVIGATE, SessionUser } from './core/api/api';

const user: SessionUser = {
  sub: 's1', login: 'qa.analyst', name: 'Quinn Analyst', tenantId: 't1',
  permissions: ['mdm.uom:read'], authTime: '2026-10-15T12:00:00Z', logoutUrl: '/bff/logout?sid=abc',
};

describe('App shell', () => {
  const navigations: string[] = [];

  async function render(environment: 'DEV' | 'PROD', signedIn: SessionUser | null) {
    navigations.length = 0;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: APP_CONFIG, useValue: { environment, apiBase: '' } },
        { provide: NAVIGATE, useValue: (url: string) => navigations.push(url) },
      ],
    });
    TestBed.inject(AuthService).user.set(signedIn);
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('shows the environment badge so PROD is never mistaken for VAL', async () => {
    const { el } = await render('PROD', user);
    const badge = el.querySelector('.env');
    expect(badge?.textContent?.trim()).toBe('PROD');
    expect(badge?.getAttribute('data-env')).toBe('PROD');
  });

  it('offers sign-in when signed out and sends the user to the server login', async () => {
    const { el } = await render('DEV', null);
    const button = el.querySelector('.signin button') as HTMLButtonElement;
    expect(button.textContent).toContain('Sign in');
    button.click();
    expect(navigations[0]).toMatch(/^\/bff\/login\?returnUrl=/);
  });

  it('shows the signed-in user and signs out through the session-bound logout URL', async () => {
    const { el } = await render('DEV', user);
    expect(el.querySelector('.signin')).toBeNull();
    expect(el.querySelector('.who')?.textContent).toContain('Quinn Analyst');
    const out = Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.includes('Sign out'))!;
    out.click();
    expect(navigations).toEqual(['/bff/logout?sid=abc']);
  });
});
