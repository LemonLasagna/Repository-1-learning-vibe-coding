// Shared helpers for the end-to-end tests (see bff-signin.e2e.cjs for prerequisites).
'use strict';

const BASE = process.env.E2E_BASE ?? 'http://localhost:4200';
const PASSWORD = process.env.E2E_PASSWORD ?? 'Dev-Only-Pass-2026';

async function signIn(page, login) {
  await page.goto(`${BASE}/uoms`);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL(/\/realms\/pharmaos\//);
  await page.fill('#username', login);
  await page.fill('#password', PASSWORD);
  await page.click('#kc-login');
  await page.waitForURL(`${BASE}/uoms`);
}

/** Keycloak asks for the password again (prompt=login) although the SSO session is alive. */
async function confirmPassword(page) {
  await page.waitForURL(/\/realms\/pharmaos\//);
  await page.locator('#password').waitFor();
  await page.fill('#password', PASSWORD);
  await page.click('#kc-login');
}

/** Value for a datetime-local input. */
function localInput(date) {
  const p = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${p(date.getMonth() + 1)}-${p(date.getDate())}T${p(date.getHours())}:${p(date.getMinutes())}`;
}

/** The current state in the workflow progress bar. */
async function state(page) {
  return (await page.locator('.progress .current').innerText()).trim();
}

/** Signs a workflow action through the dialog and Keycloak, and waits for the result banner. */
async function signAction(page, button, banner) {
  await page.getByRole('button', { name: button, exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Electronic signature' });
  await dialog.waitFor();
  const ack = dialog.locator('.ack input');
  if (await ack.count()) await ack.check();
  await dialog.getByRole('button', { name: 'Continue to sign' }).click();
  await confirmPassword(page);
  await page.getByText(banner).waitFor();
}

/** Raises a minor deviation at site BER as the signed-in user; returns its path. */
async function raiseDeviation(page, title) {
  await page.goto(`${BASE}/deviations/new`);
  await page.locator('select[name="site"]').selectOption({ label: 'BER · Berlin (solid dose)' });
  await page.fill('#f-title', title);
  await page.locator('#f-classification').selectOption({ label: 'Minor' });
  await page.locator('#f-deviation_type').selectOption({ label: 'Process' });
  await page.fill('#f-occurred_at', localInput(new Date(Date.now() - 3 * 3600 * 1000)));
  await page.fill('#f-description', 'Lot numbers printed with a missing digit on 40 cartons.');
  await page.getByRole('button', { name: 'Save draft' }).click();
  await page.waitForURL(/\/deviations\/[0-9a-f-]{36}$/);
  return new URL(page.url()).pathname;
}

async function step(name, fn) {
  await fn();
  console.log(`  ✓ ${name}`);
}

module.exports = { BASE, PASSWORD, signIn, confirmPassword, step, localInput, state, signAction, raiseDeviation };
