// End-to-end sign-in and electronic-signature test against a real Keycloak (realm deploy/keycloak/dev-import/pharmaos-realm.json),
// Requirements shown (doc 22): URS-SEC-001, URS-SEC-013, URS-SEC-014, URS-DI-001
// the backend in Development mode on :5080 and `ng serve` on :4200. See INSTALL.md, A5 and A6.
// Run: node e2e/bff-signin.e2e.cjs   (needs playwright-core and a Chromium; CHROMIUM_PATH overrides the browser)
'use strict';
const { chromium } = require('playwright-core');
const assert = require('node:assert/strict');

const { BASE, PASSWORD, signIn, step } = require('./helpers.cjs');
const code = `E2E${Date.now().toString(36).toUpperCase()}`.slice(0, 12);
let recordPath = '';

(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined });
  try {
    // ---- qa.analyst: full rights ----
    const ctx = await browser.newContext();
    const page = await ctx.newPage();

    await step('signed-out shell offers sign-in', async () => {
      await page.goto(`${BASE}/uoms`);
      await page.getByText('You are not signed in.').waitFor();
    });

    await step('sign in through Keycloak as qa.analyst', async () => {
      await signIn(page, 'qa.analyst');
      await page.locator('.who', { hasText: 'Quinn Analyst' }).waitFor();
    });

    await step('session cookie is HttpOnly and no token reaches JavaScript', async () => {
      const cookies = await ctx.cookies(BASE);
      const session = cookies.find((c) => c.name === 'pharmaos.session');
      assert.ok(session, `session cookie missing; got ${cookies.map((c) => c.name).join(', ')}`);
      assert.equal(session.httpOnly, true);
      assert.ok(session.value.length < 1000, 'cookie should carry only a session key, not tokens');
      const visible = await page.evaluate(() =>
        JSON.stringify([document.cookie, { ...localStorage }, { ...sessionStorage }]));
      assert.doesNotMatch(visible, /eyJ[A-Za-z0-9_-]{10,}/, 'a JWT is visible to JavaScript');
    });

    await step('/bff/user reports tenant, permissions and auth_time', async () => {
      const u = await page.evaluate(() => fetch('/bff/user').then((r) => r.json()));
      assert.equal(u.login, 'qa.analyst');
      assert.equal(u.tenantId, '0000000a-0000-0000-0000-000000000000');
      assert.deepEqual(u.permissions, ['audit.trail:read', 'esig.sign', 'mdm.uom:read', 'mdm.uom:write', 'qms.capa:read', 'qms.capa:write', 'qms.change:read', 'qms.change:write', 'qms.deviation:read', 'qms.deviation:write']);
      assert.ok(u.authTime, 'auth_time missing');
      assert.match(u.logoutUrl, /^\/bff\/logout\?sid=/);
    });

    await step('API call without the CSRF header is refused', async () => {
      const status = await page.evaluate(() => fetch('/api/mdm/v1/uoms').then((r) => r.status));
      assert.equal(status, 403);
    });

    await step(`create unit of measure ${code}`, async () => {
      const form = page.getByRole('form', { name: 'Create unit of measure' });
      await form.getByLabel('Code').fill(code);
      await form.getByLabel('Name').fill('End-to-end test unit');
      await form.getByLabel('Dimension').selectOption('MASS');
      await form.getByRole('button', { name: 'Create' }).click();
      await page.waitForURL(/\/uoms\/[0-9a-f-]{36}$/);
      await page.getByRole('heading', { name: new RegExp(code) }).waitFor();
      recordPath = new URL(page.url()).pathname;
    });

    await step('edit with a reason for change; audit trail names the signed-in user', async () => {
      await page.getByLabel('Name').fill('End-to-end test unit (renamed)');
      await page.getByLabel('Reason for change').fill('E2E: verify audit attribution');
      await page.getByRole('button', { name: 'Save' }).click();
      await page.getByText('Saved as version 2.').waitFor();
      const trail = page.locator('pos-audit-trail');
      await trail.getByText('E2E: verify audit attribution').waitFor();
      const text = await trail.innerText();
      assert.match(text, /Quinn Analyst \(qa\.analyst\)/, `audit actor not shown: ${text}`);
    });

    await step('the last editor cannot approve: refused before any password is asked', async () => {
      await page.getByRole('button', { name: 'Sign as Approved…' }).click();
      const dialog = page.getByRole('dialog', { name: 'Electronic signature' });
      await dialog.getByText(`${code} (version 2)`).waitFor();
      if (await dialog.locator('.ack input').count()) await dialog.locator('.ack input').check();
      await dialog.getByRole('button', { name: 'Continue to sign' }).click();
      await page.getByText('Another person must sign.').waitFor();
      assert.equal(new URL(page.url()).origin, new URL(BASE).origin, 'should not have left for Keycloak');
    });

    await step('re-authentication asks Keycloak for the password again and refreshes auth_time', async () => {
      const before = await page.evaluate(() => fetch('/bff/user').then((r) => r.json()));
      await new Promise((r) => setTimeout(r, 1100)); // auth_time has one-second resolution
      const back = page.url().replace(BASE, '');
      await page.goto(`${BASE}/bff/reauth?returnUrl=${encodeURIComponent(back)}`);
      await page.waitForURL(/\/realms\/pharmaos\//);
      // The SSO session is still alive, yet prompt=login forces the password form.
      await page.locator('#password').waitFor();
      await page.fill('#password', PASSWORD);
      await page.click('#kc-login');
      await page.waitForURL(`${BASE}${back}`);
      const after = await page.evaluate(() => fetch('/bff/user').then((r) => r.json()));
      assert.equal(after.sub, before.sub);
      assert.ok(Date.parse(after.authTime) > Date.parse(before.authTime), `${before.authTime} -> ${after.authTime}`);
    });

    await step('sign out ends the server session', async () => {
      await page.getByRole('button', { name: 'Sign out' }).click();
      await page.getByText('You are not signed in.').waitFor();
      const status = await page.evaluate(() => fetch('/bff/user').then((r) => r.status));
      assert.equal(status, 401);
      // ...and the Keycloak SSO session: signing in again must ask for credentials.
      await page.getByRole('button', { name: 'Sign in' }).click();
      await page.waitForURL(/\/realms\/pharmaos\//);
      await page.locator('#password').waitFor();
    });

    // ---- read.only: may read, may not write ----
    const ro = await (await browser.newContext()).newPage();
    await step('read.only can list but the server refuses create (403)', async () => {
      await signIn(ro, 'read.only');
      await ro.getByRole('link', { name: code }).waitFor();
      const status = await ro.evaluate((c) => fetch('/api/mdm/v1/uoms', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-CSRF': '1' },
        body: JSON.stringify({ code: c + 'X', name: 'nope', dimension: 'MASS' }),
      }).then((r) => r.status), code);
      assert.equal(status, 403);
    });

    // ---- qa.approver: electronic signature through Keycloak re-authentication ----
    const ap = await (await browser.newContext()).newPage();
    await step('qa.approver signs as Approved; Keycloak asks for the password again', async () => {
      await signIn(ap, 'qa.approver');
      await ap.goto(`${BASE}${recordPath}`);
      await ap.getByRole('heading', { name: new RegExp(code) }).waitFor();
      await ap.getByRole('button', { name: 'Sign as Approved…' }).click();
      const dialog = ap.getByRole('dialog', { name: 'Electronic signature' });
      await dialog.getByText('Avery Approver (qa.approver)').waitFor();
      assert.equal(await dialog.locator('input[type="password"]').count(), 0, 'the app must not ask for the password');
      await dialog.locator('#esig-reason').fill('E2E: approved for use');
      if (await dialog.locator('.ack input').count()) await dialog.locator('.ack input').check();
      await dialog.getByRole('button', { name: 'Continue to sign' }).click();
      await ap.waitForURL(/\/realms\/pharmaos\//);
      await ap.locator('#password').waitFor(); // live SSO session, yet the password is required
      await ap.fill('#password', PASSWORD);
      await ap.click('#kc-login');
      await ap.getByText('Signed: Approved by Avery Approver, version 2.').waitFor();
      assert.equal(new URL(ap.url()).search, '', 'the used challenge must be dropped from the address');
      const panel = ap.locator('pos-signatures');
      await panel.getByText('current version').waitFor();
      const text = await panel.innerText();
      assert.match(text, /Approved by Avery Approver \(qa\.approver\)/);
      assert.match(text, /Reason: E2E: approved for use/);
    });

    // Run last: it leaves Keycloak's SSO session belonging to read.only in this browser context.
    const sw = await (await browser.newContext()).newPage();
    await signIn(sw, 'qa.analyst');
    await step('re-authentication completed by a different user is refused and the session is unchanged', async () => {
      await sw.goto(`${BASE}/bff/reauth?returnUrl=%2Fuoms`);
      await sw.waitForURL(/\/realms\/pharmaos\//);
      await sw.click('#reset-login'); // Keycloak locks the username to the hint; "restart login" unlocks it
      await sw.fill('#username', 'read.only');
      await sw.fill('#password', PASSWORD);
      await sw.click('#kc-login');
      await sw.getByText('Re-authentication was performed by a different user.').waitFor();
      const u = await sw.evaluate(() => fetch('/bff/user').then((r) => r.json()));
      assert.equal(u.login, 'qa.analyst');
    });

    console.log('E2E sign-in: all checks passed');
  } finally {
    await browser.close();
  }
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
