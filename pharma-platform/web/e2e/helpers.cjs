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

async function step(name, fn) {
  await fn();
  console.log(`  ✓ ${name}`);
}

module.exports = { BASE, PASSWORD, signIn, confirmPassword, step };
