import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService, describeError, gxpHttpInterceptor, UomApi } from './api';

describe('API layer', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([gxpHttpInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends the CSRF header and a correlation id, and never an Authorization header', () => {
    TestBed.inject(HttpClient).get('/api/x').subscribe();
    const req = http.expectOne('/api/x');
    expect(req.request.headers.get('X-CSRF')).toBe('1');
    expect(req.request.headers.get('X-Correlation-Id')).toMatch(/^[0-9a-f-]{36}$/);
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
  });

  it('loads the signed-in user, or null when the session is missing', async () => {
    const auth = TestBed.inject(AuthService);
    const first = auth.load();
    http.expectOne('/bff/user').flush({ sub: 's', login: 'qa.analyst', name: null, tenantId: 't', permissions: ['a'], authTime: null, logoutUrl: null });
    expect((await first)?.login).toBe('qa.analyst');
    expect(auth.hasPermission('a')).toBe(true);
    expect(auth.hasPermission('b')).toBe(false);

    const second = auth.load();
    http.expectOne('/bff/user').flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(await second).toBeNull();
    expect(auth.user()).toBeNull();
  });

  it('update sends If-Match and the reason for change, and returns the new ETag', () => {
    let result: { etag: string } | undefined;
    TestBed.inject(UomApi)
      .update('id-1', '"3"', 'Correct spelling', { name: 'Kilogramme' })
      .subscribe((r) => (result = r));

    const req = http.expectOne('/api/mdm/v1/uoms/id-1');
    expect(req.request.method).toBe('PATCH');
    expect(req.request.headers.get('If-Match')).toBe('"3"');
    expect(req.request.headers.get('X-Change-Reason')).toBe('Correct spelling');
    expect(req.request.body).toEqual({ name: 'Kilogramme' });
    req.flush({ id: 'id-1', code: 'KG', name: 'Kilogramme', dimension: 'MASS', rowVersion: 4, updatedAt: '' }, {
      headers: { ETag: '"4"' },
    });
    expect(result?.etag).toBe('"4"');
  });

  it('describes Problem Details errors in user terms', () => {
    const err = (status: number, error: unknown) => new HttpErrorResponse({ status, error });
    expect(describeError(err(412, { title: 'Stale version', detail: 'Reload it.' }))).toBe('Reload it.');
    expect(describeError(err(400, { errors: { code: ['Required.'] } }))).toBe('code: Required.');
    expect(describeError(err(401, null))).toContain('Sign in again');
    expect(describeError(err(0, null))).toContain('cannot be reached');
  });
});
