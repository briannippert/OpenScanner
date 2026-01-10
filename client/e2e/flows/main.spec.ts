import { test, expect } from '@playwright/test';

// Mock data
const mockChannels = [
  { id: 1, frequency: 155.000, alphaTag: "Police Dispatch", description: "Main Dispatch", mode: "P25" },
  { id: 2, frequency: 156.000, alphaTag: "Fire Dispatch", description: "County Fire", mode: "FM" }
];

const mockLogs = [
  { id: "log1", timestamp: "2024-01-01T12:00:00Z", frequency: 155.000, alphaTag: "Police Dispatch", duration: 5.5 },
  { id: "log2", timestamp: "2024-01-01T12:01:00Z", frequency: 156.000, alphaTag: "Fire Dispatch", duration: 3.2 }
];

const mockTones = [
  { id: 1, name: "Station 1", frequencyA: 600, frequencyB: 800 }
];

test.beforeEach(async ({ page }) => {
  // Mock WebSocket before page loads
  await page.addInitScript(() => {
    class MockWebSocket extends EventTarget {
      url: string;
      readyState: number = 1; // OPEN
      static readonly OPEN = 1;
      onopen: ((ev: Event) => void) | null = null;
      onmessage: ((ev: MessageEvent) => void) | null = null;
      onclose: ((ev: CloseEvent) => void) | null = null;
      onerror: ((ev: Event) => void) | null = null;

      constructor(url: string) {
        super();
        this.url = url;
        setTimeout(() => {
          const openEv = new Event('open');
          if (this.onopen) this.onopen(openEv);
          this.dispatchEvent(openEv);

          // If it's the control socket, send initial state
          if (url.includes('/ws/control')) {
            const msgEv = new MessageEvent('message', {
              data: JSON.stringify({
                type: 'STATE_UPDATE',
                payload: { status: 'IDLE', signalStrength: 0, isHardwareConnected: true }
              })
            });
            if (this.onmessage) this.onmessage(msgEv);
            this.dispatchEvent(msgEv);
          }
        }, 50);
      }
      send(data: string) { console.log('WS Send:', data); }
      close() { this.readyState = 3; }
    }
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (window as any).WebSocket = MockWebSocket;
  });

  // Mock API routes
  await page.route('**/api/channels', async route => {
    await route.fulfill({ json: mockChannels });
  });

  await page.route('**/api/firetones', async route => {
    await route.fulfill({ json: mockTones });
  });

  await page.route('**/api/history', async route => {
    await route.fulfill({ json: mockLogs });
  });

  await page.route('**/api/history/years', async route => {
    await route.fulfill({ json: ["2024"] });
  });

  await page.route('**/api/control', async route => {
     await route.fulfill({ status: 200, body: 'OK' });
  });

  // Navigate to page
  await page.goto('/');
});

test.describe('App Layout', () => {
  test('renders main components', async ({ page }) => {
    // Header
    await expect(page.locator('header').getByText('OPENSCANNER')).toBeVisible();
    
    // Channel Control Header
    await expect(page.getByText('CHANNEL CONTROL')).toBeVisible();
    
    // Channel List
    await expect(page.getByRole('button', { name: /Police Dispatch/ })).toBeVisible();
    
    // Recent Activity
    await expect(page.getByText('Recent Activity')).toBeVisible();
  });

  test('responsive layout adapts', async ({ page }) => {
    // Desktop view
    await page.setViewportSize({ width: 1920, height: 1080 });
    await expect(page.getByText('CHANNEL CONTROL')).toBeVisible();
    
    // Mobile view
    await page.setViewportSize({ width: 375, height: 667 });
    await expect(page.locator('header').getByText('OPENSCANNER')).toBeVisible();
  });
});

test.describe('Channel Management', () => {
  test('opens channel manager', async ({ page }) => {
    // Find edit button near CHANNEL CONTROL
    await page.locator('button:has(svg[data-testid="EditIcon"])').click({ force: true });
    
    await expect(page.getByText('Manage Channels')).toBeVisible();
    await expect(page.getByRole('dialog').getByText('Police Dispatch')).toBeVisible();
  });

  test('can add new channel', async ({ page }) => {
    await page.route('**/api/channels', async route => {
       if (route.request().method() === 'POST') {
           const postData = route.request().postDataJSON();
           expect(postData.alphaTag).toBe('New Channel');
           await route.fulfill({ status: 201, json: { ...postData, id: 3 } });
       } else {
           await route.fulfill({ json: mockChannels });
       }
    });

    await page.locator('button:has(svg[data-testid="EditIcon"])').click({ force: true });
    
    // Click Add Channel (Fab with AddIcon)
    await page.locator('button:has(svg[data-testid="AddIcon"])').click();
    
    // Fill Form
    await page.getByLabel('Frequency (MHz)').fill('158.000');
    await page.getByLabel('Name').fill('New Channel');
    
    // Save
    await page.getByRole('button', { name: 'Save' }).click();
    
    await expect(page.getByText('Manage Channels')).toBeVisible();
  });
});

test.describe('Scanner Control', () => {
  test('clicking channel card triggers hold', async ({ page }) => {
    let holdRequestSent = false;
    await page.route('**/api/control', async route => {
        const data = route.request().postDataJSON();
        if (data.action === 'hold' && data.frequency === 155.000) {
            holdRequestSent = true;
        }
        await route.fulfill({ status: 200 });
    });

    // Click on "Police Dispatch" card
    await page.getByRole('button', { name: /Police Dispatch/ }).click({ force: true });
    
    // Verify API call
    await expect.poll(() => holdRequestSent).toBeTruthy();
  });
});

test.describe('Transmission Log', () => {
  test('displays logs and supports search', async ({ page }) => {
    // Check logs exist
    await expect(page.getByRole('button', { name: 'Recent Activity' })).toBeVisible();
    
    // Target the log list container specifically
    const logList = page.locator('.MuiCollapse-wrapperInner .MuiList-root');
    await expect(logList.getByText('Police Dispatch').first()).toBeVisible();
    
    // Test Search
    const searchInput = page.getByPlaceholder('Search logs...');
    
    // Mock search results
    await page.route('**/api/history/search?q=Fire', async route => {
        await route.fulfill({ json: [mockLogs[1]] });
    });

    await searchInput.fill('Fire');
    
    // Wait for search results in the log area
    // The view switches from Recent Activity list to Search results list
    const searchResultList = page.locator('.MuiBox-root .MuiList-root').filter({ hasText: 'Fire Dispatch' });
    await expect(searchResultList.getByText('Police Dispatch')).not.toBeVisible();
    await expect(searchResultList.getByText('Fire Dispatch')).toBeVisible();
  });
});
