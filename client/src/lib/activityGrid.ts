/** Transmissions in one UTC hour, as returned by GET /api/history/activity. */
export interface ActivityBucket {
  /** Start of the hour, UTC (ISO 8601). */
  hour: string;
  count: number;
}

export interface ActivityGrid {
  /** counts[dayOfWeek][hourOfDay], Sunday = 0, in the viewer's local time. */
  counts: number[][];
  /** Largest cell, for scaling the color ramp. */
  max: number;
  /** Sum of all cells. */
  total: number;
}

/**
 * Folds hourly UTC buckets into a day-of-week × hour-of-day grid in local time.
 * The server buckets in UTC precisely so this conversion can happen here, in
 * whatever time zone the browser is in.
 */
export function toActivityGrid(buckets: ActivityBucket[]): ActivityGrid {
  const counts: number[][] = Array.from({ length: 7 }, () => new Array<number>(24).fill(0));
  let max = 0;
  let total = 0;
  for (const { hour, count } of buckets) {
    const d = new Date(hour);
    if (isNaN(d.getTime()) || !(count > 0)) continue;
    const cell = (counts[d.getDay()][d.getHours()] += count);
    if (cell > max) max = cell;
    total += count;
  }
  return { counts, max, total };
}
