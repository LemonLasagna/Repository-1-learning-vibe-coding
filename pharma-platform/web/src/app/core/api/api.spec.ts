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

  it('adds a correlation id to every request and the bearer token when signed in', () => {
    const client = TestBed.inject(HttpClient);
    client.get('/x').subscribe();
    const anonymous = http.expectOne('/x');
    expect(anonymous.request.headers.get('X-Correlation-Id')).toMatch(/^[0-9a-f-]{36}$/);
    expect(anonymous.request.headers.has('Authorization')).toBe(false);
    anonymous.flush({});

    TestBed.inject(AuthService).accessToken.set('abc');
    client.get('/y').subscribe();
    const signedIn = http.expectOne('/y');
    expect(signedIn.request.headers.get('Authorization')).toBe('Bearer abc');
    signedIn.flush({});
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
    expect(describeError(err(401, null))).toContain('not signed in');
    expect(describeError(err(0, null))).toContain('cannot be reached');
  });
});
