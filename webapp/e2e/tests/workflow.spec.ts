import { expect, test, type Page } from '@playwright/test';

const shot = (page: Page, name: string) => page.screenshot({ path: `screenshots/${name}.png`, fullPage: false });

async function signIn(page: Page, name: string) {
  await page.goto('/login');
  await page.getByRole('button', { name: new RegExp(name) }).click();
  await expect(page.getByText('Workload Forecast').first()).toBeVisible();
  await expect(page.locator('.who')).toContainText(name);
}

test.describe.configure({ mode: 'serial' });

test('discipline manager proposes a change with a reason and submits', async ({ page }) => {
  page.on('dialog', d => d.accept());
  await signIn(page, 'GM Process');
  await page.getByRole('link', { name: 'Discipline Review' }).click();
  await expect(page.getByRole('heading', { name: '1100 Process Technology' })).toBeVisible();
  await shot(page, '02-discipline-review');

  // first editable proposed cell of the first Running line: raise it and give a reason
  const firstInput = page.locator('tr.proposed td.m input').first();
  const label = await firstInput.getAttribute('aria-label');
  const project = label!.split(' ')[0];
  await firstInput.fill('9999');
  await expect(firstInput).toHaveClass(/changed/);
  await page.getByLabel(`Reason for ${project}`).first().fill('Client asked for an additional process safety study and HAZOP close-out workshops');
  await page.getByRole('button', { name: /Save 1 draft/ }).click();
  await expect(page.getByText('1 line saved as draft')).toBeVisible();
  await expect(page.locator('.badge', { hasText: 'Draft' }).first()).toBeVisible();
  await shot(page, '03-review-draft-saved');

  await page.getByRole('button', { name: /Submit review/ }).click();
  await expect(page.getByText('Review submitted to Corporate Planning')).toBeVisible();
  await expect(page.locator('.badge', { hasText: 'Submitted' }).first()).toBeVisible();
});

test('a reason is mandatory before a department can submit', async ({ page, request }) => {
  // API-level check through the same session rules: Piping saves a change without a reason, submit is refused
  page.on('dialog', d => d.accept());
  await signIn(page, 'GM Piping');
  await page.getByRole('link', { name: 'Discipline Review' }).click();
  const firstInput = page.locator('tr.proposed td.m input').first();
  await firstInput.fill('1234');
  await page.getByRole('button', { name: /Save 1 draft/ }).click();
  await expect(page.getByText('1 line saved as draft')).toBeVisible();
  await page.getByRole('button', { name: /Submit review/ }).click();
  await expect(page.getByRole('alert')).toContainText('a reason is required');
  void request;
});

test('Corporate Planning decides in the approval queue', async ({ page }) => {
  await signIn(page, 'Corporate Planning');
  await page.getByRole('link', { name: 'Approval Queue' }).click();
  await expect(page.getByRole('heading', { name: 'Approval Queue' })).toBeVisible();
  await expect(page.locator('tbody tr').first()).toBeVisible();
  await shot(page, '04-approval-queue');

  const row = page.locator('tbody tr', { hasText: 'HAZOP' });
  await row.getByRole('button', { name: 'Decide' }).click();
  await expect(page.getByRole('dialog', { name: 'Decision' })).toBeVisible();
  await shot(page, '05-decision');
  await page.getByLabel('Comment to the department').fill('Agreed: HAZOP close-out is in the contract scope.');
  await page.getByRole('button', { name: 'Approve as requested' }).click();
  await expect(page.getByText('Request approved')).toBeVisible();

  // returning without a comment is refused
  await page.locator('tbody tr', { hasText: 'Electrical scope grew' }).getByRole('button', { name: 'Decide' }).click();
  await page.getByRole('button', { name: 'Return for revision' }).click();
  await expect(page.getByRole('alert')).toContainText('reason when returning');
  await page.getByRole('button', { name: 'Approve edited values' }).click();
  await expect(page.getByText('Request partly approved')).toBeVisible();
});

test('dashboard, status board and reports reflect the decisions', async ({ page }) => {
  await signIn(page, 'Corporate Planning');
  await expect(page.getByRole('heading', { name: /workload forecast/ })).toBeVisible();
  await expect(page.locator('.tile', { hasText: 'Planning input' })).toBeVisible();
  await page.waitForTimeout(300);
  await shot(page, '01-dashboard');
  await page.getByRole('link', { name: 'Cycle Control' }).click();
  await expect(page.getByText(/departments answered/)).toBeVisible();
  await expect(page.locator('tr', { hasText: 'Process Technology' }).locator('.badge', { hasText: 'Submitted' })).toBeVisible();
  await shot(page, '06-cycle-control');
  await page.getByRole('button', { name: 'Import data' }).click();
  await shot(page, '07-import');
  await page.getByRole('link', { name: 'Reports' }).click();
  await expect(page.getByRole('cell', { name: 'Process Technology' })).toBeVisible();
  await shot(page, '08-reports-departments');
  await page.getByRole('button', { name: 'Deviation register' }).click();
  await expect(page.locator('td', { hasText: 'HAZOP' }).first()).toBeVisible();
  await shot(page, '09-deviation-register');
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Power BI data (.zip)' }).click();
  expect((await download).suggestedFilename()).toMatch(/powerbi\.zip$/);
});

test('planner sees approved changes to implement', async ({ page }) => {
  await signIn(page, 'Planning Engineer 1');
  await page.getByRole('link', { name: 'My Projects' }).click();
  await expect(page.getByText('Planner values are locked after planner entry')).toBeVisible();
  await shot(page, '10-planner-grid');
  await page.getByRole('button', { name: 'Implementation' }).click();
  const mark = page.getByRole('button', { name: 'Mark implemented' }).first();
  if (await mark.count()) {
    await mark.click();
    await expect(page.getByText('Marked as implemented in Primavera')).toBeVisible();
  }
  await shot(page, '11-implementation');
});

test('viewer can read but not decide or edit', async ({ page }) => {
  await signIn(page, 'Management Viewer');
  await expect(page.getByRole('link', { name: 'Discipline Review' })).toHaveCount(0);
  await page.getByRole('link', { name: 'Approval Queue' }).click();
  await page.getByLabel('Status').selectOption('');
  await expect(page.getByRole('button', { name: 'Decide' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Bulk-approve small changes' })).toHaveCount(0);
});
