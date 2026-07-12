import { test, expect } from '../fixtures';

test.describe('Settings', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
  });

  test('displays git commit hash', async ({ page }) => {
    // Open Settings
    await page.getByRole('button', { name: 'Settings' }).click();
    
    // Verify Git Commit is visible
    await expect(page.getByText('Git Commit')).toBeVisible();
    await expect(page.getByText('test-commit-hash')).toBeVisible();
  });

  test('shows the ribbon update indicator and opens the update dialog when a newer release is available', async ({ page }) => {
    // The server reports update availability at /api/update/status.
    await page.route(/\/api\/update\/status/, async route => {
      await route.fulfill({ json: {
        state: 'available',
        currentVersion: '1.0.0',
        currentCommit: 'test-commit-hash',
        latestTag: 'v9.9.9',
        latestName: 'Release 9.9.9',
        releaseNotes: 'Big update!',
        releaseUrl: 'https://github.com/briannippert/OpenScanner/releases/tag/v9.9.9',
        commitsBehind: 3,
        updateAvailable: true,
        log: [],
      } });
    });

    // Reload so the ribbon poll picks up the mocked status.
    await page.reload();

    // The UPDATE chip appears in the header ribbon.
    const updateChip = page.getByText('UPDATE', { exact: true });
    await expect(updateChip).toBeVisible();

    // Clicking it opens the Software Update dialog targeting the new release.
    await updateChip.click();
    await expect(page.getByText('Software Update')).toBeVisible();
    await expect(page.getByText(/latest v9\.9\.9/)).toBeVisible();
    await expect(page.getByRole('link', { name: 'Release notes' })).toHaveAttribute('href', /releases\/tag\/v9\.9\.9/);
  });
});
