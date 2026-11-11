import { describe, it, expect } from 'vitest';
import { VIRIDIS_LUT, viridisRGB } from './ramp';

describe('VIRIDIS_LUT', () => {
  it('holds 256 rgb entries', () => {
    expect(VIRIDIS_LUT.length).toBe(256 * 3);
  });

  it('matches viridisRGB at every entry', () => {
    for (let i = 0; i < 256; i++) {
      const [r, g, b] = viridisRGB(i / 255);
      expect([VIRIDIS_LUT[i * 3], VIRIDIS_LUT[i * 3 + 1], VIRIDIS_LUT[i * 3 + 2]])
        .toEqual([Math.round(r), Math.round(g), Math.round(b)]);
    }
  });
});
