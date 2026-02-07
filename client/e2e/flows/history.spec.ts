import { test, expect, mockLogs } from '../fixtures';

test.describe('Transmission Log', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
  });

  test('displays logs', async ({ page }) => {
    // Check logs exist
    await expect(page.getByRole('button', { name: 'Recent Activity' })).toBeVisible();
    
    // Target the log list container specifically
    const logList = page.locator('.MuiCollapse-wrapperInner .MuiList-root');
    await expect(logList.getByText('Police Dispatch').first()).toBeVisible();
  });

  test('supports search', async ({ page }) => {
    // Test Search
    const searchInput = page.getByPlaceholder('Search logs...');
    
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

      // Find the play button for the first log
      // Assuming there is a play button/icon in the list item
      const logItem = page.locator('.MuiCollapse-wrapperInner .MuiList-root .MuiListItem-root').first();
      // Click the item or a specific play button. Often list items are clickable for playback or have an icon.
      // Let's assume clicking the item expands or plays, or look for a play icon.
      // If `TransmissionLog.tsx` is standard, maybe clicking the item plays.
      
      // Let's try clicking a "Play" icon if visible, otherwise the item.
      const playButton = logItem.locator('button:has(svg[data-testid="PlayCircleOutlineIcon"])');
      await expect(playButton).toBeVisible();
      await playButton.click();

      // Check if audio was requested
      // Note: HTML5 Audio might not fully trigger in headless without interaction or specific codec, 
      // but the network request should happen.
      await expect.poll(() => audioRequested).toBeTruthy();
  });
});
