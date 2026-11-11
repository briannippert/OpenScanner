/**
 * Whether two frequencies in MHz name the same channel.
 *
 * Frequencies round-trip through SQLite REAL columns and JSON, so the held frequency the
 * server reports is not guaranteed to be bit-identical to the channel's. 0.1 kHz is far
 * below any real channel spacing.
 */
export const sameFrequency = (a: number | undefined | null, b: number | undefined | null): boolean =>
  a != null && b != null && Math.abs(a - b) < 0.0001;
