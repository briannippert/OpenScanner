/**
 * Shared color ramps for the canvas visualizers (VU meter, audio spectrogram,
 * RF spectrum/waterfall) and the activity heatmap — so every magnitude surface
 * reads as one system instead of each canvas inventing its own colors.
 *
 * `viridis` is a perceptually-uniform, colorblind-safe sequential ramp used for
 * spectral magnitude. `green` is a single-hue ramp (dark → signal green) used
 * where the scanner-green identity should dominate (VU meter, heatmap).
 */

type RGB = [number, number, number];

const lerp = (a: number, b: number, t: number) => a + (b - a) * t;

function sample(stops: RGB[], t: number): RGB {
  const x = Math.max(0, Math.min(1, t)) * (stops.length - 1);
  const i = Math.floor(x);
  const f = x - i;
  if (i >= stops.length - 1) return stops[stops.length - 1];
  const a = stops[i];
  const b = stops[i + 1];
  return [lerp(a[0], b[0], f), lerp(a[1], b[1], f), lerp(a[2], b[2], f)];
}

// Viridis control points (perceptually uniform, CVD-safe).
const VIRIDIS: RGB[] = [
  [68, 1, 84], [72, 40, 120], [62, 74, 137], [49, 104, 142],
  [38, 130, 142], [31, 158, 137], [53, 183, 121], [110, 206, 88],
  [181, 222, 43], [253, 231, 37],
];

// Dark base → signal green (single hue magnitude).
const GREEN: RGB[] = [
  [8, 16, 12], [16, 60, 36], [24, 110, 62], [40, 180, 100], [57, 255, 136],
];

const toCss = ([r, g, b]: RGB, alpha = 1) =>
  `rgba(${Math.round(r)},${Math.round(g)},${Math.round(b)},${alpha})`;

/** Perceptual sequential ramp for spectral magnitude. t in [0,1]. */
export const viridis = (t: number, alpha = 1) => toCss(sample(VIRIDIS, t), alpha);

/** Single-hue (scanner green) magnitude ramp. t in [0,1]. */
export const green = (t: number, alpha = 1) => toCss(sample(GREEN, t), alpha);

/** Raw RGB tuple (for putImageData pixel writes). */
export const viridisRGB = (t: number): RGB => sample(VIRIDIS, t);
export const greenRGB = (t: number): RGB => sample(GREEN, t);
