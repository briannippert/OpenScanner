import { describe, it, expect } from 'vitest';
import { formatCount, formatDuration, niceMax, toTimeSeries } from './stats';

describe('formatDuration', () => {
  it.each([
    [0, '0s'], [42, '42s'], [65, '1m 05s'], [3599, '59m 59s'],
    [3600, '1h 00m'], [11_520, '3h 12m'], [187_200, '2d 4h'],
  ])('%d seconds → %s', (seconds, expected) => {
    expect(formatDuration(seconds)).toBe(expected);
  });
});

describe('formatCount', () => {
  it.each([
    [0, '0'], [1284, '1,284'], [9999, '9,999'], [12_940, '12.9K'], [250_000, '250K'], [1_200_000, '1.2M'],
  ])('%d → %s', (n, expected) => {
    expect(formatCount(n)).toBe(expected);
  });
});

describe('niceMax', () => {
  it.each([[0, 1], [1, 1], [3, 5], [7, 10], [42, 50], [130, 200], [999, 1000]])('%d → %d', (max, expected) => {
    expect(niceMax(max)).toBe(expected);
  });
});

describe('toTimeSeries', () => {
  // Local-time constructors throughout, so the test holds in any time zone.
  const now = new Date(2026, 0, 14, 15, 30);
  const hourBucket = (y: number, m: number, d: number, h: number, count: number) =>
    ({ hour: new Date(y, m, d, h).toISOString(), count });

  it('gives one slot per day, oldest first, ending today, with empty days kept', () => {
    const series = toTimeSeries([
      hourBucket(2026, 0, 14, 9, 3),
      hourBucket(2026, 0, 14, 13, 2),
      hourBucket(2026, 0, 12, 23, 7),
    ], 7, now);

    // Today plus the seven days before it: the window starts partway through the first.
    expect(series).toHaveLength(8);
    expect(series[0].start).toEqual(new Date(2026, 0, 7));
    expect(series[7].start).toEqual(new Date(2026, 0, 14));
    expect(series.map(p => p.count)).toEqual([0, 0, 0, 0, 0, 7, 0, 5]);
  });

  it('gives hourly slots for a one-day window', () => {
    const series = toTimeSeries([
      hourBucket(2026, 0, 14, 15, 4),
      hourBucket(2026, 0, 13, 16, 1),
    ], 1, now);

    expect(series).toHaveLength(25);
    expect(series[24].start).toEqual(new Date(2026, 0, 14, 15));
    expect(series[24].count).toBe(4);
    expect(series[0].start).toEqual(new Date(2026, 0, 13, 15));
    expect(series[1].count).toBe(1);
  });

  it('drops buckets outside the window', () => {
    const series = toTimeSeries([hourBucket(2025, 11, 1, 0, 50)], 7, now);
    expect(series.reduce((n, p) => n + p.count, 0)).toBe(0);
  });
});
