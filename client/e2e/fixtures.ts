/* eslint-disable react-hooks/rules-of-hooks */
import { test as base } from '@playwright/test';

// Mock Data
export const mockChannels = [
  { id: 1, frequency: 155.000, alphaTag: "Police Dispatch", description: "Main Dispatch", mode: "P25" },
  { id: 2, frequency: 156.000, alphaTag: "Fire Dispatch", description: "County Fire", mode: "FM" }
];

export const mockLogs = [
  { id: "log1", timestamp: "2024-01-01T12:00:00Z", frequency: 155.000, alphaTag: "Police Dispatch", duration: 5.5, audio_path: "test_audio.wav" },
  { id: "log2", timestamp: "2024-01-01T12:01:00Z", frequency: 156.000, alphaTag: "Fire Dispatch", duration: 3.2, audio_path: "test_audio_2.wav" }
];

export const mockTones = [
  { id: 1, name: "Station 1", frequencyA: 600, frequencyB: 800 }
];

// Extend the test object with custom fixtures if needed in the future
// For now, we'll just export a configured test object that applies common mocks
export const test = base.extend({
  page: async ({ page }, use) => {
    // Unregister Service Workers
    await page.evaluate(async () => {
        if ('serviceWorker' in navigator) {
            const registrations = await navigator.serviceWorker.getRegistrations();
            for (const registration of registrations) {
                await registration.unregister();
            }
        }
    });

    // Mock WebSocket
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
          // Store instance globally for tests to access
          // eslint-disable-next-line @typescript-eslint/no-explicit-any
          const global = window as any;
          if (!global.mockWebSockets) global.mockWebSockets = [];
          global.mockWebSockets.push(this);

          setTimeout(() => {
            const openEv = new Event('open');
            if (this.onopen) this.onopen(openEv);
            this.dispatchEvent(openEv);

            // Initial control state
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

    // Common API Mocks
    await page.route(/\/api\/channels/, async route => {
      await route.fulfill({ json: mockChannels });
    });

    await page.route(/\/api\/firetones/, async route => {
      await route.fulfill({ json: mockTones });
    });

    await page.route(/\/api\/history(\?.*)?$/, async route => {
       await route.fulfill({ json: mockLogs });
    });

    await page.route(/\/api\/history\/years/, async route => {
      await route.fulfill({ json: ["2024"] });
    });

    await page.route(/\/api\/system\/info/, async route => {
      await route.fulfill({ json: { Commit: "test-commit-hash", Version: "1.0.0" } });
    });

    await page.route(/\/api\/control/, async route => {
       await route.fulfill({ status: 200, body: 'OK' });
    });

    await use(page);
  },
});

export { expect } from '@playwright/test';
