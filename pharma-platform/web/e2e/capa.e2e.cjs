// End-to-end CAPA lifecycle and its link to a deviation, against a real Keycloak (see bff-signin.e2e.cjs).
// Requirements shown (doc 22): URS-QMS-006, URS-QMS-007, URS-QMS-008, URS-SEC-014
// The database needs the demo organisation (install.sh --demo-data). Run: node e2e/capa.e2e.cjs
'use strict';
const { chromium } = require('playwright-core');
const assert = require('node:assert/strict');
const { BASE, signIn, step, state, signAction, raiseDeviation } = require('./helpers.cjs');

(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined });
  try {
    const analyst = await (await browser.newContext()).newPage();
    const qa = await (await browser.newContext()).newPage();
    let deviation = '';
    let capa = '';

    await step('a deviation reaches investigation (analyst raises and signs, QA triages)', async () => {
      await signIn(analyst, 'qa.analyst');
      await signIn(qa, 'qa.approver');
      deviation = await raiseDeviation(analyst, `E2E CAPA source ${Date.now().toString(36)}`);
      await signAction(analyst, 'Submit ✍', 'Signed: Authored by Quinn Analyst.');
      await qa.goto(`${BASE}${deviation}`);
      await qa.getByRole('button', { name: 'Start investigation' }).click();
      await qa.getByText('Start investigation: now Investigation.').waitFor();
    });

    await step('the analyst raises a CAPA from the deviation and plans it', async () => {
      await analyst.goto(`${BASE}${deviation}`);
      await analyst.getByRole('link', { name: 'Raise CAPA' }).click();
      await analyst.getByText(/Source: deviation DEV-\d{4}-\d{5}/).waitFor();
      await analyst.getByRole('button', { name: 'Save draft' }).click();
      await analyst.waitForURL(/\/capas\/[0-9a-f-]{36}$/);
      capa = new URL(analyst.url()).pathname;
      assert.equal(await state(analyst), 'Draft plan');
      assert.equal(await analyst.getByRole('button', { name: 'Submit plan for review' }).isDisabled(), true);

      const check = analyst.getByRole('form', { name: 'Plan effectiveness check' });
      await check.locator('textarea').fill('No label misprint deviations for three months.');
      await check.getByRole('button', { name: 'Save check' }).click();
      await analyst.getByText('Effectiveness check saved.').waitFor();
      const add = analyst.getByRole('form', { name: 'Add action' });
      await add.locator('textarea').fill('Replace the print head and add it to preventive maintenance.');
      await add.getByRole('button', { name: 'Add action' }).click();
      await analyst.getByText('Action added.').waitFor();

      // The action is implemented through a change control raised from it; each side shows the other.
      await analyst.getByRole('link', { name: 'Raise change' }).click();
      await analyst.getByText(/Source: CAPA-\d{4}-\d{5}, action 1/).waitFor();
      await analyst.getByRole('button', { name: 'Save draft' }).click();
      await analyst.waitForURL(/\/changes\/[0-9a-f-]{36}$/);
      const changePath = new URL(analyst.url()).pathname;
      const linked = analyst.locator('section[aria-labelledby="linked-title"]');
      await linked.waitFor();
      assert.match(await linked.innerText(), /CAPA-\d{4}-\d{5}/);
      await linked.getByRole('link').click();
      await analyst.waitForURL(`${BASE}${capa}`);
      await analyst.locator(`a[href="${changePath}"]`).waitFor();
      assert.equal(await analyst.getByRole('link', { name: 'Raise change' }).count(), 0);
      await analyst.getByRole('button', { name: 'Submit plan for review' }).click();
      await analyst.getByText('Submit plan for review: now Plan review.').waitFor();
    });

    await step('the deviation cannot close while its CAPA plan is unapproved', async () => {
      await analyst.goto(`${BASE}${deviation}`);
      await analyst.fill('#f-root_cause_summary', 'Worn print head on labeller L3.');
      await analyst.getByLabel('Reason for change').fill('Investigation result');
      await analyst.getByRole('button', { name: 'Save', exact: true }).click();
      await analyst.getByText('Saved.').waitFor();
      await analyst.getByRole('button', { name: 'Submit for QA approval' }).click();
      await analyst.getByText('Submit for QA approval: now QA approval.').waitFor();

      await qa.goto(`${BASE}${deviation}`);
      assert.equal(await qa.getByRole('button', { name: 'Approve and close ✍' }).isDisabled(), true);
      await qa.getByText('Every linked CAPA needs an approved plan before the deviation can close.').waitFor();
    });

    await step('QA finds both in "My work": the plan to review, the deviation waiting for it', async () => {
      await qa.goto(`${BASE}/inbox`);
      const planItem = qa.locator('li', { hasText: 'Review and approve the plan' }).filter({ has: qa.locator(`a[href="${capa}"]`) });
      await planItem.waitFor();
      await qa.locator('li', { hasText: 'Waiting for 1 linked CAPA plan(s) to be approved' })
        .filter({ has: qa.locator(`a[href="${deviation}"]`) }).waitFor();
      assert.match(await qa.locator('nav .count').innerText(), /^\d+$/);
      await planItem.locator('a').click();
      await qa.waitForURL(`${BASE}${capa}`);
    });

    await step('QA approves the CAPA plan (signed), and then the deviation closes', async () => {
      await qa.goto(`${BASE}${capa}`);
      await signAction(qa, 'Approve plan ✍', 'Signed: Approved by Avery Approver.');
      await qa.locator('.progress .current', { hasText: 'Implementation' }).waitFor();
      await qa.goto(`${BASE}${deviation}`);
      await signAction(qa, 'Approve and close ✍', 'Signed: Approved by Avery Approver.');
      await qa.locator('.progress .current', { hasText: 'Closed' }).waitFor();
    });

    await step('the action is completed with evidence and QA verifies the implementation (signed)', async () => {
      await analyst.goto(`${BASE}${capa}`);
      await analyst.getByRole('button', { name: 'Complete…' }).click();
      await analyst.getByRole('group', { name: 'Complete action' }).locator('textarea').fill('Print head replaced, work order WO-4711.');
      await analyst.getByRole('button', { name: 'Mark done' }).click();
      await analyst.getByText('Action 1 done.').waitFor();
      await qa.goto(`${BASE}${capa}`);
      await signAction(qa, 'Verify implementation ✍', 'Signed: Verified by Avery Approver.');
      await qa.locator('.progress .current', { hasText: 'Effectiveness check' }).waitFor();
    });

    await step('the effectiveness result is recorded and QA closes the CAPA (signed)', async () => {
      await analyst.goto(`${BASE}${capa}`);
      const record = analyst.getByRole('group', { name: 'Record effectiveness result' });
      await record.locator('select').selectOption('EFFECTIVE');
      await record.locator('textarea').fill('Trend report: no label deviations since the change.');
      await record.getByRole('button', { name: 'Record result' }).click();
      await analyst.getByText('Result recorded.').waitFor();
      await qa.goto(`${BASE}${capa}`);
      assert.equal(await qa.getByRole('button', { name: 'Verify implementation ✍' }).count(), 0);
      await signAction(qa, 'Verify effectiveness and close ✍', 'Signed: Verified by Avery Approver.');
      await qa.locator('.progress .current', { hasText: 'Closed' }).waitFor();
      await qa.locator('pos-signatures li').nth(2).waitFor(); // the panel reloads after signing
      const sigs = await qa.locator('pos-signatures').innerText();
      assert.match(sigs, /Approved by Avery Approver/);
      assert.equal((sigs.match(/Verified by Avery Approver/g) ?? []).length, 2, sigs);
    });

    console.log('E2E CAPA: all checks passed');
  } finally {
    await browser.close();
  }
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
