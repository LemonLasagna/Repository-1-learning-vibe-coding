import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { APP_CONFIG, AuthService } from './core/api/api';

describe('App shell', () => {
  async function render(environment: 'DEV' | 'PROD') {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: APP_CONFIG, useValue: { environment, apiBase: '' } }],
    });
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the environment badge so PROD is never mistaken for VAL', async () => {
    const { el } = await render('PROD');
    const badge = el.querySelector('.env');
    expect(badge?.textContent?.trim()).toBe('PROD');
    expect(badge?.getAttribute('data-env')).toBe('PROD');
  });

  it('warns when not signed in and hides the warning once a token exists', async () => {
    const { fixture, el } = await render('DEV');
    expect(el.querySelector('.signin')).not.toBeNull();
    TestBed.inject(AuthService).accessToken.set('token');
    await fixture.whenStable();
    expect(el.querySelector('.signin')).toBeNull();
  });
});
