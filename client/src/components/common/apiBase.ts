/**
 * Single source of truth for the backend base URL.
 *
 * In dev the client runs on Vite (:5173) while the API runs on :5212; in
 * production they share an origin. Previously this logic was re-inlined in
 * several places (notably 4× inside SettingsManager) — import from here instead.
 */
export const apiBase = (): string => {
  const isDev = window.location.port === '5173';
  const port = isDev ? '5212' : window.location.port || '80';
  const protocol = window.location.protocol;
  const backendHost = window.location.hostname;
  const portSuffix = port === '80' || port === '' ? '' : `:${port}`;
  return `${protocol}//${backendHost}${portSuffix}`;
};

/** Fetch against the backend base URL. `path` should start with `/`. */
export const apiFetch = (path: string, init?: RequestInit): Promise<Response> =>
  fetch(`${apiBase()}${path}`, init);

/** Fetch + parse JSON, returning `null` on any network/parse/HTTP error. */
export const apiJson = async <T>(path: string, init?: RequestInit): Promise<T | null> => {
  try {
    const res = await apiFetch(path, init);
    if (!res.ok) return null;
    return (await res.json()) as T;
  } catch {
    return null;
  }
};
