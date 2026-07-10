import { test, expect, mockLogs } from '../fixtures';

test.describe('Transmission Log', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
  });

  test('displays logs', async ({ page }) => {
    // Recent is the default tab in the segmented control.
    await expect(page.getByRole('tab', { name: 'Recent' })).toBeVisible();

    // Scope to the transmission-log region (the channel grid also lists tags).
    const log = page.getByTestId('transmission-log');
    await expect(log.getByText('Police Dispatch').first()).toBeVisible();
  });

  test('supports search', async ({ page }) => {
    // Test Search
    const searchInput = page.getByPlaceholder('Search logs…');
    
    // Mock search results
    await page.route(/\/api\/history\/search/, async route => {
        await route.fulfill({ json: [mockLogs[1]] });
    });

    await searchInput.fill('Fire');
    
    // Wait for search results in the log area
    const searchResultList = page.locator('.MuiBox-root .MuiList-root').filter({ hasText: 'Fire Dispatch' });
    await expect(searchResultList.getByText('Police Dispatch')).not.toBeVisible();
    await expect(searchResultList.getByText('Fire Dispatch')).toBeVisible();
  });

  test('playback requests audio file', async ({ page }) => {
      let audioRequested = false;
      await page.route(/\/audio\/.*/, async route => {
          audioRequested = true;
          await route.fulfill({ 
              status: 200, 
              contentType: 'audio/wav',
              body: Buffer.from('fake audio')
          });
      });

      // Find the play button for the first log row in the transmission log.
      const logItem = page.getByTestId('transmission-log').locator('.MuiListItem-root').first();
      const playButton = logItem.locator('button:has(svg[data-testid="PlayCircleOutlineIcon"])');
      await expect(playButton).toBeVisible();
      await playButton.click();

      // Check if audio was requested
      // Note: HTML5 Audio might not fully trigger in headless without interaction or specific codec, 
      // but the network request should happen.
      await expect.poll(() => audioRequested).toBeTruthy();
  });
});
