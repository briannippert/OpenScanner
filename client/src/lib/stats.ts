import type { ActivityBucket } from './activityGrid';

/** Response of GET /api/history/stats. */
export interface TransmissionStats {
  days: number;
  summary: {
    transmissions: number;
    airtimeSeconds: number;
    longestSeconds: number;
    transcribed: number;
    toneOuts: number;
    activeChannels: number;
  };
  channels: { alphaTag: string; frequency: number; count: number; airtimeSeconds: number }[];
  talkgroups: RadioIdStat[];
  sources: RadioIdStat[];
  tones: { name: string; count: number }[];
}

export interface RadioIdStat {
  alphaTag: string;
  frequency: number;
  id: number;
  count: number;
  airtimeSeconds: number;
}

/** A duration for a stat tile or bar label: 42s, 4m 05s, 3h 12m, 2d 4h. */
export function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.round(totalSeconds));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${String(s % 60).padStart(2, '0')}s`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ${String(m % 60).padStart(2, '0')}m`;
  return `${Math.floor(h / 24)}d ${h % 24}h`;
}

/** Compact count for a stat tile: 1,284 in full, then 12.9K, 1.2M. */
export function formatCount(n: number): string {
  if (n < 10_000) return Math.round(n).toLocaleString('en-US');
  if (n < 1_000_000) return `${(n / 1000).toFixed(n < 100_000 ? 1 : 0)}K`;
  return `${(n / 1_000_000).toFixed(1)}M`;
}

export interface SeriesPoint {
  /** Start of the slot, local time. */
  start: Date;
  count: number;
}

/**
 * Turns hourly UTC buckets into an evenly spaced series ending now, with a slot
 * for every period — including the empty ones, so quiet days show as gaps rather
 * than vanishing. One slot per local hour for a 1-day window, per local day
 * otherwise; the first and last slots are partial.
 */
export function toTimeSeries(buckets: ActivityBucket[], days: number, now: Date = new Date()): SeriesPoint[] {
  const hourly = days <= 1;
  const slotStart = (d: Date) => hourly
    ? new Date(d.getFullYear(), d.getMonth(), d.getDate(), d.getHours())
    : new Date(d.getFullYear(), d.getMonth(), d.getDate());
  const step = (d: Date, n: number) => hourly
    ? new Date(d.getFullYear(), d.getMonth(), d.getDate(), d.getHours() + n)
    : new Date(d.getFullYear(), d.getMonth(), d.getDate() + n);

  // One more slot than whole periods: the window is measured back from *now*, so
  // it starts partway through its first hour or day. Dropping that partial slot
  // would make the chart add up to less than the totals shown beside it.
  const slots = (hourly ? 24 : days) + 1;
  const last = slotStart(now);
  const points: SeriesPoint[] = [];
  const index = new Map<number, SeriesPoint>();
  for (let i = slots - 1; i >= 0; i--) {
    const p = { start: step(last, -i), count: 0 };
    points.push(p);
    index.set(p.start.getTime(), p);
  }
  for (const { hour, count } of buckets) {
    const d = new Date(hour);
    if (isNaN(d.getTime()) || !(count > 0)) continue;
    const p = index.get(slotStart(d).getTime());
    if (p) p.count += count;
  }
  return points;
}

/** A round axis maximum at or above `max`: 1, 2, 5 × a power of ten. */
export function niceMax(max: number): number {
  if (max <= 0) return 1;
  const pow = Math.pow(10, Math.floor(Math.log10(max)));
  const f = max / pow;
  return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * pow;
}
