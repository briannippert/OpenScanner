/**
 * Per-channel radio-alias label helpers. A "channel" is the (alphaTag, frequency)
 * pair, matching how transmissions are keyed. `nameFor` resolves a display Name for
 * a SRC/TG on a channel, or undefined when unmapped.
 */
export type NameFor = (
  kind: 'SRC' | 'TG',
  value: number | null | undefined,
  alphaTag: string,
  frequency: number,
) => string | undefined;

/** Display a source ID: mapped name, else the raw number, else '?'. */
export const srcLabel = (
  nameFor: NameFor | null | undefined,
  value: number | null | undefined,
  alphaTag: string,
  frequency: number,
): string => (value == null ? '?' : nameFor?.('SRC', value, alphaTag, frequency) ?? String(value));

/** Display a talkgroup: mapped name, else the raw number, else '?'. */
export const tgLabel = (
  nameFor: NameFor | null | undefined,
  value: number | null | undefined,
  alphaTag: string,
  frequency: number,
): string => (value == null ? '?' : nameFor?.('TG', value, alphaTag, frequency) ?? String(value));

/**
 * Map each numeric token in a speaker chain (e.g. "101 → 102") as a SRC on the
 * channel, leaving any non-numeric separators intact.
 */
export const chainLabel = (
  nameFor: NameFor | null | undefined,
  chain: string,
  alphaTag: string,
  frequency: number,
): string =>
  chain
    .split(/\s*(?:→|->)\s*/)
    .map(tok => (/^\d+$/.test(tok) ? srcLabel(nameFor, Number(tok), alphaTag, frequency) : tok))
    .join(' → ');
