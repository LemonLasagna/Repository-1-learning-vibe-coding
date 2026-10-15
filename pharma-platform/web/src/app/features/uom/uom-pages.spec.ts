import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { UomDetailPage } from './uom-pages';

const uom = { id: 'u1', code: 'KG', name: 'Kilogram', dimension: 'MASS', rowVersion: 2, updatedAt: '2026-10-15T03:00:00Z' };

describe('UomDetailPage', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  async function render() {
    const fixture = TestBed.createComponent(UomDetailPage);
    fixture.componentRef.setInput('id', 'u1');
    fixture.detectChanges();
    http.expectOne('/api/mdm/v1/uoms/u1').flush(uom, { headers: { ETag: '"2"' } });
    await fixture.whenStable();
    http.expectOne('/api/audit/v1/records/mdm/uom/u1').flush([]);
    await fixture.whenStable();
    return { fixture, cmp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
  }

  it('cannot save without a reason for change', async () => {
    const { cmp } = await render();
    cmp.editForm.controls.name.setValue('Kilogramme');
    cmp.editForm.controls.name.markAsDirty();
    expect(cmp.editForm.invalid).toBe(true);
    cmp.editForm.controls.reason.setValue('   ');
    expect(cmp.editForm.invalid).toBe(true);
  });

  it('sends the ETag it read and shows a reload action when someone else saved first', async () => {
    const { fixture, cmp, el } = await render();
    cmp.editForm.patchValue({ name: 'Kilogramme', reason: 'Spelling' });
    cmp.save();
    const req = http.expectOne('/api/mdm/v1/uoms/u1');
    expect(req.request.headers.get('If-Match')).toBe('"2"');
    req.flush({ title: 'Stale version', detail: 'The record was changed by someone else.' }, { status: 412, statusText: 'Precondition Failed' });
    await fixture.whenStable();

    expect(el.textContent).toContain('changed by someone else');
    const reload = Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.includes('Reload'));
    expect(reload).toBeDefined();
  });
});
