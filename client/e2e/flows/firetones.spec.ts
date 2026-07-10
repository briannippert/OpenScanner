import { test, expect, mockTones } from '../fixtures';

test.describe('Fire Tone Management', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    // Open Fire Tone Manager (button with NotificationsActiveIcon)
    // Note: It's hidden on xs screens according to styles, so ensure viewport is large enough
    await page.setViewportSize({ width: 1024, height: 768 });
    await page.getByRole('button', { name: 'Fire Tone Outs' }).click();
    await expect(page.getByText('Manage Fire Tone-Outs')).toBeVisible();
  });

  test('lists existing tones', async ({ page }) => {
    for (const tone of mockTones) {
        await expect(page.getByRole('dialog').getByText(tone.name)).toBeVisible();
    }
  });

  test('can add new tone', async ({ page }) => {
    const newToneName = 'New Fire Tone';
    
    await page.route(/\/api\/firetones/, async route => {
       if (route.request().method() === 'POST') {
           const postData = route.request().postDataJSON();
           expect(postData.name).toBe(newToneName);
           await route.fulfill({ status: 201, json: { ...postData, id: 99 } });
       } else {
           await route.fulfill({ json: mockTones });
       }
    });

    // Click Add Tone (Fab with AddIcon)
    await page.locator('button:has(svg[data-testid="AddIcon"])').click();
    
    // Fill Form (Assuming fields match FireToneManager implementation)
    // Need to verify field labels. Usually "Name", "Tone A (Hz)", "Tone B (Hz)"
    await page.getByLabel('Name').fill(newToneName);
    await page.getByLabel('Tone A (Hz)').fill('600');
    await page.getByLabel('Tone B (Hz)').fill('900');
    
    // Save
    await page.getByRole('button', { name: 'Save' }).click();
    
    await expect(page.getByText('Manage Fire Tone-Outs')).toBeVisible();
  });

  test('can edit existing tone', async ({ page }) => {
    const toneToEdit = mockTones[0];
    const updatedName = 'Updated Station 1';

    await page.route(/\/api\/firetones\/1/, async route => {
        if (route.request().method() === 'PUT') {
            const data = route.request().postDataJSON();
            expect(data.name).toBe(updatedName);
            await route.fulfill({ status: 200, json: { ...data } });
        }
    });

    // Click item to edit
    const listItem = page.getByRole('listitem').filter({ hasText: toneToEdit.name });
    await listItem.locator('button:has(svg[data-testid="EditIcon"])').click();
    
    // Edit
    await page.getByLabel('Name').fill(updatedName);
    await page.getByRole('button', { name: 'Save' }).click();
    
    await expect(page.getByText('Manage Fire Tone-Outs')).toBeVisible();
  });

  test('can delete tone', async ({ page }) => {
    const toneToDelete = mockTones[0];
    let deleteRequested = false;

    await page.route(/\/api\/firetones\/1/, async route => {
        if (route.request().method() === 'DELETE') {
            deleteRequested = true;
            await route.fulfill({ status: 200 });
        }
    });

    // Click item to edit
    const listItem = page.getByRole('listitem').filter({ hasText: toneToDelete.name });
    
    // Delete
    await listItem.locator('button:has(svg[data-testid="DeleteIcon"])').click();
    
    await expect.poll(() => deleteRequested).toBeTruthy();
  });
});
