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

  test('displays update notification when newer version available', async ({ page }) => {
    // Mock GitHub API for a newer version
    await page.route('https://api.github.com/repos/briannippert/OpenScanner/releases/latest', async route => {
        await route.fulfill({ json: { 
            tag_name: "v9.9.9", 
            html_url: "https://github.com/briannippert/OpenScanner/releases/tag/v9.9.9",
            body: "Big update!"
        } });
    });

    // Reload to trigger version check
    await page.reload();
    
    // Open Settings
    await page.getByRole('button', { name: 'Settings' }).click();
    
    // Verify Update Alert is visible
    await expect(page.getByText('Update Available: v9.9.9')).toBeVisible();
    await expect(page.getByRole('link', { name: 'VIEW' })).toHaveAttribute('href', /releases\/tag\/v9.9.9/);
  });
});
