import { describe, it, expect } from 'vitest';
import { sameFrequency } from './frequency';

describe('sameFrequency', () => {
  it('matches identical and float-noise-different values', () => {
    expect(sameFrequency(155.5, 155.5)).toBe(true);
    expect(sameFrequency(0.1 + 0.2, 0.3)).toBe(true);
    expect(sameFrequency(154.445, 154.44500000000002)).toBe(true);
  });

  it('distinguishes adjacent narrowband channels', () => {
    expect(sameFrequency(155.5, 155.50625)).toBe(false);
  });

  it('is false when either side is missing', () => {
    expect(sameFrequency(undefined, 155.5)).toBe(false);
    expect(sameFrequency(155.5, null)).toBe(false);
    expect(sameFrequency(undefined, undefined)).toBe(false);
  });
});
