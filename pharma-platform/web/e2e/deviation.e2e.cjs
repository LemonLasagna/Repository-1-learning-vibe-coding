// End-to-end deviation lifecycle against a real Keycloak, the backend and `ng serve` (see bff-signin.e2e.cjs).
// Requirements shown (doc 22): URS-QMS-003, URS-SEC-013, URS-SEC-014
// The database needs the demo organisation (install.sh --demo-data). Run: node e2e/deviation.e2e.cjs
'use strict';
const { chromium } = require('playwright-core');
const assert = require('node:assert/strict');
const { BASE, signIn, step, localInput, state, signAction } = require('./helpers.cjs');

const title = `E2E deviation ${Date.now().toString(36)}`;

(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined });
  try {
    const analyst = await (await browser.newContext()).newPage();
    const qa = await (await browser.newContext()).newPage();
    let path = '';

    await step('qa.analyst raises a deviation as a draft', async () => {
      await signIn(analyst, 'qa.analyst');
      await analyst.getByRole('link', { name: 'Deviations' }).click();
      await analyst.getByRole('link', { name: 'New deviation' }).click();
      await analyst.locator('select[name="site"]').selectOption({ label: 'BER · Berlin (solid dose)' });
      await analyst.fill('#f-title', title);
      await analyst.locator('#f-classification').selectOption({ label: 'Minor' });
      await analyst.locator('#f-deviation_type').selectOption({ label: 'Process' });
      await analyst.fill('#f-occurred_at', localInput(new Date(Date.now() - 3 * 3600 * 1000)));
      await analyst.fill('#f-description', 'Lot numbers printed with a missing digit on 40 cartons.');
      await analyst.getByRole('button', { name: 'Save draft' }).click();
      await analyst.waitForURL(/\/deviations\/[0-9a-f-]{36}$/);
      path = new URL(analyst.url()).pathname;
      await analyst.getByRole('heading', { name: /DEV-\d{4}-\d{5}/ }).waitFor();
      assert.equal(await state(analyst), 'Draft');
    });

    await step('submitting is signed as Authored, with the password at Keycloak', async () => {
      await signAction(analyst, 'Submit ✍', 'Signed: Authored by Quinn Analyst.');
      await analyst.locator('.progress .current', { hasText: 'Triage' }).waitFor();
      assert.equal(await analyst.getByRole('button', { name: 'Submit ✍', exact: true }).count(), 0);
    });

    await step('qa.approver triages and becomes QA owner', async () => {
      await signIn(qa, 'qa.approver');
      await qa.goto(`${BASE}${path}`);
      await qa.getByRole('button', { name: 'Start investigation' }).click();
      await qa.getByText('Start investigation: now Investigation.').waitFor();
      await qa.locator('.facts').getByText('Avery Approver').waitFor();
    });

    await step('submission for approval waits for the root cause (guard), then goes through', async () => {
      await analyst.goto(`${BASE}${path}`);
      await analyst.locator('.progress .current', { hasText: 'Investigation' }).waitFor();
      const submit = analyst.getByRole('button', { name: 'Submit for QA approval' });
      assert.equal(await submit.isDisabled(), true);
      await analyst.getByText(/Enter the root cause summary/).first().waitFor();
      await analyst.fill('#f-root_cause_summary', 'Worn print head on labeller L3.');
      await analyst.getByLabel('Reason for change').fill('Investigation result');
      await analyst.getByRole('button', { name: 'Save', exact: true }).click();
      await analyst.getByText('Saved.').waitFor();
      await submit.click();
      await analyst.getByText('Submit for QA approval: now QA approval.').waitFor();
    });

    await step('the owner cannot approve; qa.approver approves with a signature and the deviation closes', async () => {
      assert.equal(await analyst.getByRole('button', { name: 'Approve and close ✍' }).count(), 0);
      await qa.goto(`${BASE}${path}`);
      await signAction(qa, 'Approve and close ✍', 'Signed: Approved by Avery Approver.');
      await qa.locator('.progress .current', { hasText: 'Closed' }).waitFor();
      assert.equal(await qa.locator('#f-title').isDisabled(), true, 'a closed deviation is read-only');
    });

    await step('history and signatures record who did what, and under which signature', async () => {
      const rows = await qa.locator('section[aria-labelledby="hist-title"] tbody tr').allInnerTexts();
      assert.equal(rows.length, 5, rows.join('\n'));
      assert.match(rows[1], /Draft → Triage\s+Quinn Analyst\s+Authored/);
      assert.match(rows[4], /QA approval → Closed\s+Avery Approver\s+Approved/);
      await qa.locator('pos-signatures li').nth(1).waitFor(); // the panel reloads after signing
      const sigs = await qa.locator('pos-signatures').innerText();
      assert.match(sigs, /Authored by Quinn Analyst/);
      assert.match(sigs, /Approved by Avery Approver/);
    });

    console.log('E2E deviation: all checks passed');
  } finally {
    await browser.close();
  }
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
