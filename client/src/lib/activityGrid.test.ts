import { describe, it, expect } from 'vitest';
import { toActivityGrid } from './activityGrid';

// Build the expectation from the same local-time view the grid uses, so the test
// holds in whatever time zone it runs in.
const cellOf = (iso: string) => {
  const d = new Date(iso);
  return [d.getDay(), d.getHours()] as const;
};

describe('toActivityGrid', () => {
  it('returns an empty 7×24 grid for no data', () => {
    const grid = toActivityGrid([]);
    expect(grid.counts).toHaveLength(7);
    expect(grid.counts.every(row => row.length === 24 && row.every(c => c === 0))).toBe(true);
    expect(grid.max).toBe(0);
    expect(grid.total).toBe(0);
  });

  it('adds up the same weekday and hour across different weeks', () => {
    // Two Tuesdays, same UTC hour, a week apart (January: no DST change between
    // them, which would legitimately move one to a different local hour).
    const grid = toActivityGrid([
      { hour: '2026-01-06T14:00:00Z', count: 4 },
      { hour: '2026-01-13T14:00:00Z', count: 6 },
      { hour: '2026-01-13T15:00:00Z', count: 1 },
    ]);
    const [day, hour] = cellOf('2026-01-13T14:00:00Z');
    expect(grid.counts[day][hour]).toBe(10);
    expect(grid.max).toBe(10);
    expect(grid.total).toBe(11);
  });

  it('ignores malformed buckets', () => {
    const grid = toActivityGrid([
      { hour: 'not a date', count: 5 },
      { hour: '2026-03-10T14:00:00Z', count: 0 },
      { hour: '2026-03-10T16:00:00Z', count: 2 },
    ]);
    expect(grid.total).toBe(2);
  });
});
