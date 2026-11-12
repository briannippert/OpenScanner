import type { Page, Route } from '@playwright/test';
import { test, expect } from '../fixtures';

/** Two dongles and a plan over the fixture channels (ids 1 and 2). */
const radios = (mode: 'auto' | 'manual') => ({
  mode,
  devices: [
    { id: 'sn:ROOF', label: 'Roof', serial: 'ROOF', product: 'Blog V4', attached: true, index: 0, serialIsUnique: true,
      enabled: true, ppm: 1.5, gainAnalog: 40, gainDigital: 28, gainMixed: 30, role: 'FastScan' },
    { id: 'sn:ATTIC', label: 'Attic', serial: 'ATTIC', product: 'RTL2838UHIDIR', attached: true, index: 1, serialIsUnique: true,
      enabled: true, ppm: 0, gainAnalog: 40, gainDigital: 30, gainMixed: 35, role: 'FastScan' },
  ],
  plan: {
    totalChannels: 2, fastScanned: 2, scanned: 2,
    devices: [
      { deviceId: 'sn:ROOF', label: 'Roof', role: 'FastScan', gainClass: 'Digital', gain: 28, centerFrequency: 155.25, spanMHz: 0, frequencies: [155.0], channelIds: [1] },
      { deviceId: 'sn:ATTIC', label: 'Attic', role: 'FastScan', gainClass: 'Analog', gain: 40, centerFrequency: 156.25, spanMHz: 0, frequencies: [156.0], channelIds: [2] },
    ],
    autoPlacedChannelIds: mode === 'manual' ? [2] : [],
    unscannedChannelIds: [],
  },
  warnings: [],
});

/** Serves /api/radios from a mutable mode and records every write. */
const mockRadios = async (page: Page, initial: 'auto' | 'manual') => {
  let mode = initial;
  const writes: { method: string; url: string; body: unknown }[] = [];
  await page.route(/\/api\/radios/, async (route: Route) => {
    const req = route.request();
    if (req.method() === 'GET') return route.fulfill({ json: radios(mode) });
    const body = req.postDataJSON();
    writes.push({ method: req.method(), url: req.url(), body });
    if (req.url().endsWith('/mode')) mode = body.mode;
    return route.fulfill({ json: radios(mode) });
  });
  return writes;
};

test.describe('Multiple SDRs', () => {
  test('Settings lists each SDR and switches assignment mode', async ({ page }) => {
    const writes = await mockRadios(page, 'auto');
    await page.goto('/');
    await page.getByRole('button', { name: 'Settings' }).click();

    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText('Radios', { exact: true })).toBeVisible();
    await expect(dialog.getByTestId('plan-summary')).toHaveText('2 of 2 channels fast-scanned across 2 SDRs');
    await expect(dialog.getByTestId('radio-sn:ROOF').getByLabel('SDR name')).toHaveValue('Roof');
    await expect(dialog.getByTestId('radio-sn:ROOF').getByLabel('Digital dB')).toHaveValue('28');

    await dialog.getByRole('button', { name: 'Manual' }).click();
    await expect.poll(() => writes.find(w => w.url.endsWith('/api/radios/mode'))?.body).toEqual({ mode: 'manual' });
    await expect(dialog.getByText(/Manual uses the SDR you pick/)).toBeVisible();
  });

  test('a gain edit is saved for that SDR when the field is left', async ({ page }) => {
    const writes = await mockRadios(page, 'auto');
    await page.goto('/');
    await page.getByRole('button', { name: 'Settings' }).click();

    const gain = page.getByTestId('radio-sn:ATTIC').getByLabel('Analog dB');
    await gain.fill('35');
    await gain.press('Enter');

    await expect.poll(() => writes.find(w => w.url.endsWith('/api/radios/sn%3AATTIC'))?.body).toEqual({ gainAnalog: 35 });
  });

  test('Manage Channels pins a channel to an SDR in Manual mode', async ({ page }) => {
    const writes = await mockRadios(page, 'manual');
    await page.goto('/');
    await page.locator('button:has(svg[data-testid="EditIcon"])').click({ force: true });

    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText('AUTO → Attic')).toBeVisible();

    await dialog.getByRole('combobox', { name: 'SDR for Police Dispatch' }).click();
    await page.getByRole('option', { name: 'Attic' }).click();

    await expect.poll(() => writes.find(w => w.url.endsWith('/api/radios/assignments'))?.body)
      .toEqual({ channelIds: [1], deviceId: 'sn:ATTIC' });
  });
});
