/**
 * Leaflet with the heat-map plugin, loaded only by the map page so neither ends up in the
 * main bundle.
 */
import L from './leafletGlobal';
import 'leaflet.heat';
import 'leaflet/dist/leaflet.css';

export type HeatPoint = [number, number, number?];

export interface HeatLayerOptions {
  minOpacity?: number;
  maxZoom?: number;
  max?: number;
  radius?: number;
  blur?: number;
  gradient?: Record<number, string>;
}

export interface HeatLayer extends L.Layer {
  setLatLngs(points: HeatPoint[]): this;
  setOptions(options: HeatLayerOptions): this;
}

/** Typed access to the plugin's factory, which it attaches to the global `L`. */
export const heatLayer = (points: HeatPoint[], options: HeatLayerOptions): HeatLayer =>
  (L as unknown as { heatLayer: (p: HeatPoint[], o: HeatLayerOptions) => HeatLayer }).heatLayer(points, options);

export default L;
