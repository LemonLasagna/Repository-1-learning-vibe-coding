import { TestBed } from '@angular/core/testing';
import { EsigDialog, SignConfirmation, SignIntent } from './esig-dialog';

const intent: SignIntent = {
  entity: 'DEVIATION',
  recordNumber: 'DEV-2026-00412',
  recordVersion: 7,
  meaningCode: 'APPROVED',
  meaningLabel: 'Approved',
  meaningStatement: 'I approve the investigation conclusion.',
  signerName: 'Jane Doe',
  signerLogin: 'jdoe',
  reasonRequired: true,
  acknowledgementText: 'My electronic signature is the legally binding equivalent of my handwritten signature.',
};

async function render(i: SignIntent = intent) {
  const fixture = TestBed.createComponent(EsigDialog);
  fixture.componentRef.setInput('intent', i);
  await fixture.whenStable();
  const el = fixture.nativeElement as HTMLElement;
  const type = async (value: string) => {
    const input = el.querySelector('#esig-reason') as HTMLTextAreaElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  };
  const tick = async () => {
    const box = el.querySelector('.ack input') as HTMLInputElement;
    box.click();
    await fixture.whenStable();
  };
  const signButton = () => el.querySelector('button[type="submit"]') as HTMLButtonElement;
  return { fixture, el, type, tick, signButton };
}

describe('EsigDialog', () => {
  it('shows record, version, meaning and signer, and never asks for a password', async () => {
    const { el } = await render();
    const text = el.textContent ?? '';
    expect(text).toContain('DEV-2026-00412');
    expect(text).toContain('version 7');
    expect(text).toContain('Approved');
    expect(text).toContain('Jane Doe (jdoe)');
    expect(text).toContain('sign-in page asks for your password');
    expect(el.querySelector('input[type="password"]')).toBeNull();
  });

  it('needs the required reason and, on first signature, the acknowledgement', async () => {
    const { type, tick, signButton } = await render();
    expect(signButton().disabled).toBe(true);
    await type('Investigation adequate');
    expect(signButton().disabled).toBe(true);
    await tick();
    expect(signButton().disabled).toBe(false);
  });

  it('does not show the acknowledgement once it has been given', async () => {
    const { el, signButton } = await render({ ...intent, reasonRequired: false, acknowledgementText: null });
    expect(el.querySelector('.ack')).toBeNull();
    expect(signButton().disabled).toBe(false);
  });

  it('emits the confirmation once', async () => {
    const { fixture, type, tick, signButton } = await render();
    const emitted: SignConfirmation[] = [];
    fixture.componentInstance.confirmed.subscribe((c) => emitted.push(c));
    await type(' Investigation adequate ');
    await tick();
    signButton().click();
    await fixture.whenStable();
    expect(emitted).toEqual([{ reason: 'Investigation adequate', acknowledged: true }]);
    expect(signButton().disabled).toBe(true);
  });
});
