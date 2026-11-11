import { lazy } from 'react';

/**
 * The dialogs are rarely opened and make up a large share of the app's code, so they are
 * split out of the main bundle and fetched separately.
 *
 * They are still fetched soon after startup (see preloadDialogs) rather than on first
 * open. That keeps the first open instant, and it matters for correctness too: the
 * server replaces its hashed asset files when it self-updates, so a tab that waited
 * hours before first asking for a chunk would ask for a file that no longer exists.
 */

const RELOAD_KEY = 'openscanner.chunkReloadAt';

/**
 * Run a dynamic import. If it fails, the likeliest cause is a tab left open across a
 * server update, so reload once to pick up the new build instead of crashing the app.
 */
function load<T>(loader: () => Promise<T>): Promise<T> {
  return loader().catch((err: unknown) => {
    let last = 0;
    try { last = Number(sessionStorage.getItem(RELOAD_KEY)) || 0; } catch { /* storage unavailable */ }
    if (Date.now() - last < 60_000) throw err; // already reloaded recently: don't loop
    try { sessionStorage.setItem(RELOAD_KEY, String(Date.now())); } catch { /* storage unavailable */ }
    window.location.reload();
    return new Promise<T>(() => { /* never settles: the page is reloading */ });
  });
}

const importers = {
  ChannelManager: () => import('./components/ChannelManager'),
  FireToneManager: () => import('./components/FireToneManager'),
  SettingsManager: () => import('./components/SettingsManager'),
  AliasManager: () => import('./components/AliasManager'),
  UpdateManager: () => import('./components/UpdateManager'),
  CommandPalette: () => import('./components/CommandPalette'),
  RfDebugDialog: () => import('./components/RfDebugDialog'),
  SystemDebugDialog: () => import('./components/SystemDebugDialog'),
  StatsDialog: () => import('./components/StatsDialog'),
};

export const ChannelManager = lazy(() => load(importers.ChannelManager));
export const FireToneManager = lazy(() => load(importers.FireToneManager));
export const SettingsManager = lazy(() => load(importers.SettingsManager));
export const AliasManager = lazy(() => load(importers.AliasManager));
export const UpdateManager = lazy(() => load(importers.UpdateManager));
export const CommandPalette = lazy(() => load(importers.CommandPalette));
export const RfDebugDialog = lazy(() => load(importers.RfDebugDialog));
export const SystemDebugDialog = lazy(() => load(importers.SystemDebugDialog));
export const StatsDialog = lazy(() => load(importers.StatsDialog));

/**
 * Fetch every dialog chunk in the background. Goes around load() on purpose: a failed
 * background fetch must not reload the page. First use retries and handles the failure.
 */
export function preloadDialogs() {
  void Promise.allSettled(Object.values(importers).map(importer => importer()));
}
