// End-to-end change control lifecycle, against a real Keycloak (see bff-signin.e2e.cjs).
// The database needs the demo organisation (install.sh --demo-data). Run: node e2e/change.e2e.cjs
'use strict';
const { chromium } = require('playwright-core');
const assert = require('node:assert/strict');
const { BASE, signIn, step, state, signAction } = require('./helpers.cjs');

(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || undefined });
  try {
    const analyst = await (await browser.newContext()).newPage();
    const qa = await (await browser.newContext()).newPage();
    let change = '';

    await step('the analyst raises a change control with a major regulatory impact', async () => {
      await signIn(analyst, 'qa.analyst');
      await signIn(qa, 'qa.approver');
      await analyst.goto(`${BASE}/changes/new`);
      await analyst.locator('select[formcontrolname="siteId"]').selectOption({ label: 'BER · Berlin (solid dose)' });
      await analyst.locator('input[formcontrolname="title"]').fill(`E2E labeller ${Date.now().toString(36)}`);
      await analyst.locator('textarea[formcontrolname="description"]').fill('Replace labeller L3 with the new model.');
      await analyst.locator('textarea[formcontrolname="justification"]').fill('The old model is out of support.');
      await analyst.locator('select[formcontrolname="changeCategory"]').selectOption('MAJOR');
      await analyst.locator('select[formcontrolname="regulatoryImpact"]').selectOption('MAJOR');
      await analyst.getByRole('button', { name: 'Save draft' }).click();
      await analyst.waitForURL(/\/changes\/[0-9a-f-]{36}$/);
      change = new URL(analyst.url()).pathname;
      assert.equal(await state(analyst), 'Draft');
      await analyst.getByRole('button', { name: 'Submit for assessment' }).click();
      await analyst.getByText('Submit for assessment: now Assessment.').waitFor();
    });

    await step('approval waits for an impact assessment, and for a regulatory one (major regulatory impact)', async () => {
      assert.equal(await analyst.getByRole('button', { name: 'Submit for approval' }).isDisabled(), true);
      const assess = analyst.getByRole('form', { name: 'Assess impact' });
      await assess.locator('textarea[formcontrolname="assessment"]').fill('The new labeller must be qualified.');
      await assess.getByRole('button', { name: 'Save assessment' }).click();
      await analyst.getByText('Quality assessment saved.').waitFor();
      assert.equal(await analyst.getByRole('button', { name: 'Submit for approval' }).isDisabled(), true);

      await assess.locator('select[formcontrolname="impactedArea"]').selectOption('REGULATORY');
      await assess.locator('textarea[formcontrolname="assessment"]').fill('Variation needed for the new equipment.');
      await assess.getByRole('button', { name: 'Save assessment' }).click();
      await analyst.getByText('Regulatory assessment saved.').waitFor();
      await analyst.locator('td', { hasText: /^Regulatory$/ }).waitFor();
      assert.equal(await analyst.getByRole('button', { name: 'Submit for approval' }).isDisabled(), false);
    });

    await step('a task is planned and the change goes to approval', async () => {
      const add = analyst.getByRole('form', { name: 'Add task' });
      await add.locator('textarea').fill('Install and qualify the labeller.');
      await add.getByRole('button', { name: 'Add task' }).click();
      await analyst.getByText('Task added.').waitFor();
      await analyst.getByRole('button', { name: 'Submit for approval' }).click();
      await analyst.getByText('Submit for approval: now Approval.').waitFor();
    });

    await step('QA finds it in "My work", returns it with a reason, and approves it after resubmission (signed)', async () => {
      await qa.goto(`${BASE}/inbox`);
      const item = qa.locator('li', { hasText: 'Review and approve the change' }).filter({ has: qa.locator(`a[href="${change}"]`) });
      await item.waitFor();
      await item.locator('a').click();
      await qa.waitForURL(`${BASE}${change}`);
      await qa.getByRole('button', { name: 'Return for assessment' }).click();
      await qa.locator('textarea[name="transitionReason"]').fill('Assess the training impact too.');
      await qa.getByRole('button', { name: 'Confirm' }).click();
      await qa.getByText('Return for assessment: now Assessment.').waitFor();

      await analyst.goto(`${BASE}${change}`);
      const assess = analyst.getByRole('form', { name: 'Assess impact' });
      await assess.locator('select[formcontrolname="impactedArea"]').selectOption('TRAINING');
      await assess.locator('textarea[formcontrolname="assessment"]').fill('Operators need a short briefing.');
      await assess.getByRole('button', { name: 'Save assessment' }).click();
      await analyst.getByText('Training assessment saved.').waitFor();
      await analyst.getByRole('button', { name: 'Submit for approval' }).click();
      await analyst.getByText('Submit for approval: now Approval.').waitFor();

      await qa.goto(`${BASE}${change}`);
      await signAction(qa, 'Approve change ✍', 'Signed: Approved by Avery Approver.');
      await qa.locator('.progress .current', { hasText: 'Implementation' }).waitFor();
    });

    await step('verification waits for the task and the effective date; QA verifies the implementation (signed)', async () => {
      await qa.goto(`${BASE}${change}`);
      assert.equal(await qa.getByRole('button', { name: 'Verify implementation ✍' }).isDisabled(), true);
      await qa.getByText('Plan and complete every task, and set the effective date first.').waitFor();

      await analyst.goto(`${BASE}${change}`);
      await analyst.getByRole('button', { name: 'Mark done' }).click();
      await analyst.getByText('Task 1 done.').waitFor();
      const dates = analyst.getByRole('form', { name: 'Set dates' });
      await dates.locator('input[formcontrolname="effectiveDate"]').fill('2099-01-15');
      await dates.getByRole('button', { name: 'Save dates' }).click();
      await analyst.getByText('Dates saved.').waitFor();

      await qa.goto(`${BASE}${change}`);
      await signAction(qa, 'Verify implementation ✍', 'Signed: Verified by Avery Approver.');
      await qa.locator('.progress .current', { hasText: 'Post-implementation review' }).waitFor();
    });

    await step('the owner records the post-implementation review and QA closes the change (signed)', async () => {
      await analyst.goto(`${BASE}${change}`);
      await analyst.getByRole('button', { name: 'Record post-implementation review' }).click();
      await analyst.locator('textarea[name="transitionReason"]').fill('The labeller runs as intended; no deviations in 30 days.');
      await analyst.getByRole('button', { name: 'Confirm' }).click();
      await analyst.getByText('Record post-implementation review: now Closure.').waitFor();

      await qa.goto(`${BASE}${change}`);
      await signAction(qa, 'Close change ✍', 'Signed: Verified by Avery Approver.');
      await qa.locator('.progress .current', { hasText: 'Closed' }).waitFor();
      await qa.locator('pos-signatures li').nth(2).waitFor(); // the panel reloads after signing
      const sigs = await qa.locator('pos-signatures').innerText();
      assert.match(sigs, /Approved by Avery Approver/);
      assert.equal((sigs.match(/Verified by Avery Approver/g) ?? []).length, 2, sigs);
      const history = await qa.locator('pos-history-table').innerText();
      assert.match(history, /no deviations in 30 days/);
    });

    console.log('E2E change control: all checks passed');
  } finally {
    await browser.close();
  }
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
