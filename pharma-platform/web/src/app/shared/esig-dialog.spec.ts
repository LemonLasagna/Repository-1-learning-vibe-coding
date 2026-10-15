import { TestBed } from '@angular/core/testing';
import { EsigDialog, SignIntent, SignSubmission } from './esig-dialog';

const intent: SignIntent = {
  entity: 'DEVIATION',
  recordNumber: 'DEV-2026-00412',
  recordVersion: 7,
  recordHash: '9f3a000000000000000000000000000000000000000000000000000000c21e',
  meaningCode: 'APPROVED',
  meaningLabel: 'Approved',
  meaningStatement: 'I approve the investigation conclusion.',
  signerName: 'Jane Doe',
  signerLogin: 'jdoe',
  reasonRequired: true,
  secondFactorRequired: true,
};

async function render() {
  const fixture = TestBed.createComponent(EsigDialog);
  fixture.componentRef.setInput('intent', intent);
  await fixture.whenStable();
  const el = fixture.nativeElement as HTMLElement;
  const type = async (selector: string, value: string) => {
    const input = el.querySelector(selector) as HTMLInputElement | HTMLTextAreaElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  };
  const signButton = () => el.querySelector('button[type="submit"]') as HTMLButtonElement;
  return { fixture, el, type, signButton };
}

describe('EsigDialog', () => {
  it('shows record, version, short hash, meaning and signer before credentials are entered', async () => {
    const { el } = await render();
    const text = el.textContent ?? '';
    expect(text).toContain('DEV-2026-00412');
    expect(text).toContain('version 7');
    expect(text).toContain('9f3a…c21e');
    expect(text).toContain('Approved');
    expect(text).toContain('Jane Doe (jdoe)');
    expect((el.querySelector('#esig-user') as HTMLInputElement).readOnly).toBe(true);
  });

  it('keeps Sign disabled until password, one-time code and required reason are present', async () => {
    const { type, signButton } = await render();
    expect(signButton().disabled).toBe(true);
    await type('#esig-password', 'secret');
    expect(signButton().disabled).toBe(true);
    await type('#esig-otp', '123456');
    expect(signButton().disabled).toBe(true);
    await type('#esig-reason', 'Investigation adequate');
    expect(signButton().disabled).toBe(false);
  });

  it('emits the credentials once and clears the password from the component', async () => {
    const { fixture, el, type, signButton } = await render();
    const emitted: SignSubmission[] = [];
    fixture.componentInstance.signed.subscribe((s) => emitted.push(s));
    await type('#esig-password', 'secret');
    await type('#esig-otp', ' 123456 ');
    await type('#esig-reason', 'Investigation adequate');
    signButton().click();
    await fixture.whenStable();

    expect(emitted).toEqual([{ password: 'secret', otp: '123456', reason: 'Investigation adequate' }]);
    expect((el.querySelector('#esig-password') as HTMLInputElement).value).toBe('');
    expect(signButton().disabled).toBe(true);
  });
});
