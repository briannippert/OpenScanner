import React, { useEffect, useRef, useState } from 'react';
import { Box, ButtonBase, Typography } from '@mui/material';
import NavigationIcon from '@mui/icons-material/Navigation';
import L, { heatLayer, type HeatLayer } from './leaflet';
import type { MapCall } from '../../types';
import { CALL_GROUPS, callTypeLabel, groupOf } from '../../lib/callTypes';

export type LayerMode = 'markers' | 'heat' | 'both';

interface Props {
  calls: MapCall[];
  layers: LayerMode;
  selectedId: string | null;
  /**
   * Calls that just arrived live, mapped to when they arrived (ms since epoch). Their
   * markers blip and show a short-lived callout card.
   */
  fresh: ReadonlyMap<string, number>;
  /** Changes when the map should re-fit to the calls (e.g. a new time range loaded). */
  fitKey: number;
  onSelect: (call: MapCall) => void;
  onPlay: (call: MapCall) => void;
}

const DEFAULT_CENTER: L.LatLngExpression = [39.8, -98.6]; // continental US
const DEFAULT_ZOOM = 4;

// Density ramp for the heat layer: cool and dim where calls are sparse, through magenta and
// orange to bright yellow where they pile up. Lightness rises along with the hue change, so
// the order still reads for colour-blind viewers (unlike a blue-green-red rainbow).
//
// The plugin bins points into screen cells, sums each cell, and draws it with opacity
// sum / max (never below HEAT_MIN_OPACITY); the accumulated opacity is then mapped onto
// this ramp. With the default max of 1 a single call is already fully "hot", so max is
// recomputed for each zoom level from the data (see heatMax) and a lone call sits at the
// cool end.
const HEAT_RADIUS = 18;
const HEAT_BLUR = 18;
const HEAT_MIN_OPACITY = 0.38;
const HEAT_STOPS: [number, string][] = [
  [0.2, '#5468f0'], [0.5, '#9a4be0'], [0.7, '#e5488f'], [0.86, '#ff8f3a'], [1, '#ffe94a'],
];
const HEAT_GRADIENT: Record<number, string> = Object.fromEntries(HEAT_STOPS);
const HEAT_LEGEND = `linear-gradient(90deg, ${HEAT_STOPS.map(([, c]) => c).join(', ')})`;

/**
 * The cell total that should read as fully hot at this zoom: the 90th percentile of the
 * busiest cells, so one address with dozens of calls does not flatten everywhere else to
 * the cool end. Mirrors the plugin's own binning (cells of half the drawn radius).
 */
function heatMax(points: { x: number; y: number }[]): number {
  const cell = (HEAT_RADIUS + HEAT_BLUR) / 2;
  const sums = new Map<string, number>();
  for (const p of points) {
    const key = `${Math.floor(p.x / cell)}:${Math.floor(p.y / cell)}`;
    sums.set(key, (sums.get(key) ?? 0) + 1);
  }
  const sorted = [...sums.values()].sort((a, b) => a - b);
  const p90 = sorted.length ? sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * 0.9))] : 1;
  // Never below 4: with only a handful of calls, one call must not count as "many".
  return Math.max(4, p90);
}

/** How long an arrival callout stays up (the fade is inside this window). */
export const CALLOUT_MS = 6000;

/**
 * Markers are rebuilt whenever any call changes, which would restart their CSS
 * animations. Each animation instead gets a negative delay equal to the call's age, so a
 * rebuilt marker resumes mid-animation rather than replaying from the start.
 */
const ageStyle = (arrivedAt: number | undefined) =>
  arrivedAt == null ? '' : `--age:${Math.max(0, (Date.now() - arrivedAt) / 1000).toFixed(2)}s;`;

const locationOf = (call: MapCall) => call.matchedAddress || call.address || call.placeName || '';

/** Compass bearing in degrees (0 = north) from one point to another, for the off-screen arrow. */
function bearing(from: L.LatLng, to: L.LatLng): number {
  const rad = Math.PI / 180;
  const y = Math.sin((to.lng - from.lng) * rad) * Math.cos(to.lat * rad);
  const x = Math.cos(from.lat * rad) * Math.sin(to.lat * rad) - Math.sin(from.lat * rad) * Math.cos(to.lat * rad) * Math.cos((to.lng - from.lng) * rad);
  return (Math.atan2(y, x) / rad + 360) % 360;
}

/** Arrival callout: call type, summary and address. Built as DOM; the text is untrusted. */
function calloutFor(call: MapCall, arrivedAt: number): HTMLElement {
  const root = document.createElement('div');
  root.className = 'os-callout__body';
  root.setAttribute('style', `${ageStyle(arrivedAt)}--marker-color:${CALL_GROUPS[groupOf(call.callType)].color}`);
  const line = (className: string, text: string | null | undefined) => {
    if (!text) return;
    const el = document.createElement('div');
    el.className = className;
    el.textContent = text;
    root.appendChild(el);
  };
  line('os-callout__type', callTypeLabel(call.callType) + (call.incidentStatus ? ` · ${call.incidentStatus.replace(/_/g, ' ')}` : ''));
  line('os-callout__summary', call.summary);
  line('os-callout__address', locationOf(call));
  return root;
}

interface EdgeToast {
  call: MapCall;
  /** Bearing from the map centre, for the arrow. */
  heading: number;
}

const fmtTime = (iso: string) => {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
};

/** Marker icon: color and shape both encode the call group, so color is never the only cue. */
function iconFor(call: MapCall, selected: boolean, arrivedAt: number | undefined): L.DivIcon {
  const group = CALL_GROUPS[groupOf(call.callType)];
  const size = selected ? 20 : group.shape === 'dot' ? 12 : 16;
  const classes = ['os-marker', `os-marker--${group.shape}`];
  if (selected) classes.push('os-marker--selected');
  // The pop wrapper carries the arrival blip (scale-in + ripple rings), so the marker's own
  // transform (the diamond's rotation) is left alone, as is the transform Leaflet puts on
  // the icon element to position it.
  const pop = arrivedAt != null ? 'os-marker-pop os-marker-pop--fresh' : 'os-marker-pop';
  return L.divIcon({
    className: 'os-marker-wrap',
    html: `<span class="${pop}" style="${ageStyle(arrivedAt)}--marker-color:${group.color};width:${size}px;height:${size}px">`
      + `<span class="${classes.join(' ')}" style="width:${size}px;height:${size}px"></span></span>`,
    iconSize: [size, size],
    iconAnchor: [size / 2, size / 2],
    popupAnchor: [0, -size / 2],
  });
}

/**
 * Popup content built as DOM nodes, never HTML strings: summaries and transcripts come
 * from an LLM and the radio, so they are treated as untrusted text.
 */
function popupFor(call: MapCall, onPlay: (call: MapCall) => void): HTMLElement {
  const root = document.createElement('div');
  root.className = 'os-popup';
  const add = (tag: string, className: string, text?: string | null) => {
    if (!text) return null;
    const el = document.createElement(tag);
    el.className = className;
    el.textContent = text;
    root.appendChild(el);
    return el;
  };
  const group = CALL_GROUPS[groupOf(call.callType)];
  const type = add('div', 'os-popup__type', `${callTypeLabel(call.callType)}${call.incidentStatus ? ` · ${call.incidentStatus.replace(/_/g, ' ')}` : ''}`);
  if (type) type.style.setProperty('--marker-color', group.color);
  add('div', 'os-popup__summary', call.summary);
  add('div', 'os-popup__address', call.matchedAddress || call.address || call.placeName);
  add('div', 'os-popup__meta', `${fmtTime(call.timestamp)} · ${call.alphaTag || `${call.frequency.toFixed(4)} MHz`}`);
  add('div', 'os-popup__transcript', call.transcription ? `“${call.transcription}”` : null);
  if (call.audio_path) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'os-popup__play';
    btn.textContent = '▶ Play recording';
    btn.addEventListener('click', () => onPlay(call));
    root.appendChild(btn);
  }
  return root;
}

/**
 * The Leaflet map: OpenStreetMap tiles (darkened to match the app), one marker per
 * incident and an optional heat layer. Leaflet is driven imperatively from props.
 */
const CallMap: React.FC<Props> = ({ calls, layers, selectedId, fresh, fitKey, onSelect, onPlay }) => {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const mapRef = useRef<L.Map | null>(null);
  const markerLayerRef = useRef<L.LayerGroup | null>(null);
  const heatRef = useRef<HeatLayer | null>(null);
  const markersRef = useRef(new Map<string, L.Marker>());
  // Latest callbacks, so markers built earlier always call the current handlers.
  const onSelectRef = useRef(onSelect);
  const onPlayRef = useRef(onPlay);
  useEffect(() => { onSelectRef.current = onSelect; onPlayRef.current = onPlay; }, [onSelect, onPlay]);

  // Create the map once.
  useEffect(() => {
    if (!containerRef.current || mapRef.current) return;
    const map = L.map(containerRef.current, { center: DEFAULT_CENTER, zoom: DEFAULT_ZOOM, zoomControl: true, preferCanvas: false });
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      className: 'os-dark-tiles',
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    }).addTo(map);
    markerLayerRef.current = L.layerGroup().addTo(map);
    heatRef.current = // maxZoom 0 turns off the plugin's own per-zoom scaling; heatMax does that job from the data.
    heatRef.current = heatLayer([], { radius: HEAT_RADIUS, blur: HEAT_BLUR, minOpacity: HEAT_MIN_OPACITY, maxZoom: 0, max: 4, gradient: HEAT_GRADIENT });
    mapRef.current = map;
    const markers = markersRef.current;

    // The container's size can settle after mount (flex layout, drawer); keep tiles in sync.
    const ro = new ResizeObserver(() => map.invalidateSize());
    ro.observe(containerRef.current);
    return () => {
      ro.disconnect();
      map.remove();
      mapRef.current = null;
      markers.clear();
    };
  }, []);

  // Arrivals that land outside the visible area (or while only the heat layer is shown)
  // get a toast at the edge of the map instead of a callout no one would see.
  const [edgeToasts, setEdgeToasts] = useState<EdgeToast[]>([]);
  const announcedRef = useRef(new Set<string>());
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    for (const [id, arrivedAt] of fresh) {
      if (announcedRef.current.has(id)) continue;
      announcedRef.current.add(id);
      const call = calls.find(c => c.id === id);
      if (!call) continue;
      const point = L.latLng(call.lat, call.lon);
      if (layers !== 'heat' && map.getBounds().contains(point)) continue;
      const toast: EdgeToast = { call, heading: bearing(map.getCenter(), point) };
      setEdgeToasts(prev => [...prev.filter(t => t.call.id !== id), toast].slice(-3));
      const remaining = Math.max(0, CALLOUT_MS - (Date.now() - arrivedAt));
      setTimeout(() => setEdgeToasts(prev => prev.filter(t => t !== toast)), remaining);
    }
    // Forget arrivals that are no longer fresh, so the set doesn't grow forever.
    for (const id of announcedRef.current) if (!fresh.has(id)) announcedRef.current.delete(id);
  }, [fresh, calls, layers]);

  // Markers: rebuild the set when the calls or highlight state change.
  useEffect(() => {
    const group = markerLayerRef.current;
    if (!group) return;
    // Rebuilding closes popups; keep the selected call's popup open across live updates.
    const reopen = selectedId ? markersRef.current.get(selectedId)?.isPopupOpen() ?? false : false;
    group.clearLayers();
    markersRef.current.clear();
    if (layers === 'heat') return;
    // Oldest first so the newest markers draw on top.
    for (let i = calls.length - 1; i >= 0; i--) {
      const call = calls[i];
      const arrivedAt = fresh.get(call.id);
      const marker = L.marker([call.lat, call.lon], {
        icon: iconFor(call, call.id === selectedId, arrivedAt),
        keyboard: true,
        title: `${callTypeLabel(call.callType)} – ${call.matchedAddress || call.address || ''}`,
        zIndexOffset: call.id === selectedId ? 1000 : 0,
      });
      marker.bindPopup(() => popupFor(call, c => onPlayRef.current(c)), { maxWidth: 300, minWidth: 220 });
      marker.on('click', () => onSelectRef.current(call));
      // A new arrival gets a callout card; the selected call already has its popup open.
      if (arrivedAt != null && call.id !== selectedId && Date.now() - arrivedAt < CALLOUT_MS) {
        marker.bindTooltip(calloutFor(call, arrivedAt), {
          permanent: true,
          interactive: true,
          direction: 'top',
          offset: [0, -10],
          className: 'os-callout',
        });
        marker.on('tooltipopen', (e) => {
          const el = (e as L.TooltipEvent).tooltip.getElement();
          el?.addEventListener('click', () => onSelectRef.current(call), { once: true });
        });
      }
      marker.addTo(group);
      markersRef.current.set(call.id, marker);
    }
    if (reopen && selectedId) markersRef.current.get(selectedId)?.openPopup();
  }, [calls, layers, selectedId, fresh]);

  // Heat layer.
  useEffect(() => {
    const map = mapRef.current;
    const heat = heatRef.current;
    if (!map || !heat) return;
    if (layers === 'markers') {
      if (map.hasLayer(heat)) map.removeLayer(heat);
      return;
    }
    heat.setLatLngs(calls.map(c => [c.lat, c.lon, 1]));
    if (!map.hasLayer(heat)) heat.addTo(map);
    // Rescale for the zoom level: zooming out merges calls into fewer, fuller cells.
    const rescale = () => {
      const zoom = map.getZoom();
      heat.setOptions({ max: heatMax(calls.map(c => map.project([c.lat, c.lon], zoom))) });
    };
    rescale();
    map.on('zoomend', rescale);
    return () => { map.off('zoomend', rescale); };
  }, [calls, layers]);

  // Fit to the data when asked (new time range, first load).
  const callsRef = useRef(calls);
  useEffect(() => { callsRef.current = calls; }, [calls]);
  useEffect(() => {
    const map = mapRef.current;
    const current = callsRef.current;
    if (!map || current.length === 0) return;
    const bounds = L.latLngBounds(current.map(c => [c.lat, c.lon] as [number, number]));
    map.fitBounds(bounds, { padding: [40, 40], maxZoom: 15, animate: false });
  }, [fitKey]);

  // Bring the selected call into view and open its popup.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !selectedId) return;
    const call = callsRef.current.find(c => c.id === selectedId);
    if (!call) return;
    map.flyTo([call.lat, call.lon], Math.max(map.getZoom(), 15), { duration: 0.6 });
    markersRef.current.get(selectedId)?.openPopup();
  }, [selectedId]);

  return (
    <>
    <Box
      ref={containerRef}
      data-testid="call-map"
      sx={{
        position: 'absolute',
        inset: 0,
        bgcolor: '#1f2328',
        '& .os-dark-tiles': { filter: 'invert(1) hue-rotate(180deg) brightness(0.92) contrast(0.88) saturate(0.6)' },
        '& .leaflet-control-attribution': { bgcolor: 'rgba(17,19,23,0.8)', color: 'text.secondary', '& a': { color: 'info.main' } },
        '& .leaflet-bar a': { bgcolor: 'surface.raised', color: 'text.primary', borderColor: 'surface.border' },
        '& .os-marker-wrap': { background: 'none', border: 'none' },
        '& .os-marker': {
          display: 'block',
          boxSizing: 'border-box',
          bgcolor: 'var(--marker-color)',
          // 2px ring in the map's surface color separates overlapping markers.
          border: '2px solid #1f2328',
          boxShadow: '0 0 0 1px rgba(255,255,255,0.35)',
          cursor: 'pointer',
        },
        '& .os-marker--circle, & .os-marker--dot': { borderRadius: '50%' },
        '& .os-marker--square': { borderRadius: '3px' },
        '& .os-marker--diamond': { borderRadius: '2px', transform: 'rotate(45deg) scale(0.85)' },
        '& .os-marker--selected': { boxShadow: '0 0 0 2px #e7ecf2' },
        // Arrival blip: the marker pops in with an overshoot, then two rings ripple out.
        '& .os-marker-pop': { position: 'relative', display: 'block' },
        '& .os-marker-pop--fresh': {
          animation: 'osPopIn 450ms cubic-bezier(0.34, 1.56, 0.64, 1) both',
          animationDelay: 'calc(var(--age, 0s) * -1)',
        },
        '& .os-marker-pop--fresh::before, & .os-marker-pop--fresh::after': {
          content: '""',
          position: 'absolute',
          inset: 0,
          borderRadius: '50%',
          border: '2px solid var(--marker-color)',
          pointerEvents: 'none',
          opacity: 0,
          animation: 'osRipple 1.6s ease-out 3 both',
        },
        '& .os-marker-pop--fresh::before': { animationDelay: 'calc(var(--age, 0s) * -1)' },
        '& .os-marker-pop--fresh::after': { animationDelay: 'calc(0.5s - var(--age, 0s))' },
        '@keyframes osPopIn': {
          '0%': { transform: 'scale(0)' },
          '100%': { transform: 'scale(1)' },
        },
        '@keyframes osRipple': {
          '0%': { transform: 'scale(1)', opacity: 0.9 },
          '100%': { transform: 'scale(4)', opacity: 0 },
        },
        // Arrival callout card (a Leaflet tooltip). Rises in, holds, then fades out.
        '& .leaflet-tooltip.os-callout': {
          p: 0,
          bgcolor: 'transparent',
          border: 'none',
          boxShadow: 'none',
          cursor: 'pointer',
          '&::before': { display: 'none' },
        },
        '& .os-callout__body': {
          minWidth: 180,
          maxWidth: 260,
          whiteSpace: 'normal',
          px: 1.25,
          py: 0.75,
          borderRadius: 1.5,
          bgcolor: 'surface.raised',
          color: 'text.primary',
          border: '1px solid',
          borderColor: 'surface.borderStrong',
          borderLeft: '3px solid var(--marker-color)',
          boxShadow: '0 6px 20px rgba(0,0,0,0.5)',
          fontSize: 12,
          lineHeight: 1.35,
          animation: `osCallout ${CALLOUT_MS}ms ease both`,
          animationDelay: 'calc(var(--age, 0s) * -1)',
        },
        '& .os-callout__type': { fontWeight: 700, textTransform: 'capitalize' },
        '& .os-callout__summary': { color: 'text.primary' },
        '& .os-callout__address': { color: 'text.secondary', fontSize: 11 },
        '@keyframes osCallout': {
          '0%': { opacity: 0, transform: 'translateY(6px) scale(0.96)' },
          '6%': { opacity: 1, transform: 'translateY(0) scale(1)' },
          '85%': { opacity: 1, transform: 'translateY(0) scale(1)' },
          '100%': { opacity: 0, transform: 'translateY(-4px)', visibility: 'hidden' },
        },
        '@media (prefers-reduced-motion: reduce)': {
          '& .os-marker-pop--fresh, & .os-marker-pop--fresh::before, & .os-marker-pop--fresh::after': { animation: 'none' },
          '& .os-callout__body': { animationName: 'osCalloutFade' },
        },
        '@keyframes osCalloutFade': {
          '0%, 90%': { opacity: 1 },
          '100%': { opacity: 0, visibility: 'hidden' },
        },
        '& .leaflet-popup-content-wrapper, & .leaflet-popup-tip': { bgcolor: 'surface.raised', color: 'text.primary' },
        '& .os-popup': { display: 'flex', flexDirection: 'column', gap: '4px', fontSize: 13 },
        '& .os-popup__type': {
          fontWeight: 700, textTransform: 'capitalize',
          '&::before': { content: '""', display: 'inline-block', width: 8, height: 8, borderRadius: '50%', bgcolor: 'var(--marker-color)', mr: 0.75 },
        },
        '& .os-popup__address': { color: 'text.primary' },
        '& .os-popup__meta': { color: 'text.secondary', fontSize: 12 },
        '& .os-popup__transcript': { color: 'text.secondary', fontStyle: 'italic', fontSize: 12, maxHeight: 96, overflowY: 'auto' },
        '& .os-popup__play': {
          mt: 0.5, alignSelf: 'flex-start', cursor: 'pointer', font: 'inherit', fontWeight: 600,
          color: 'primary.main', bgcolor: 'transparent', border: '1px solid', borderColor: 'primary.dark',
          borderRadius: 999, px: 1.25, py: 0.25,
        },
      }}
    />
    {layers !== 'markers' && calls.length > 0 && (
      <Box
        aria-label="Heat map colours: purple-blue is fewer calls, yellow is more"
        sx={{
          position: 'absolute', zIndex: 600, top: 10, right: 10, px: 1, py: 0.5, borderRadius: 1.5,
          bgcolor: 'surface.raised', border: '1px solid', borderColor: 'surface.borderStrong',
          display: 'flex', alignItems: 'center', gap: 0.75, pointerEvents: 'none',
        }}
      >
        <Typography variant="caption" color="text.secondary">Fewer</Typography>
        <Box sx={{ width: 72, height: 8, borderRadius: 999, background: HEAT_LEGEND }} />
        <Typography variant="caption" color="text.secondary">More calls</Typography>
      </Box>
    )}
    {edgeToasts.length > 0 && (
      <Box
        aria-live="polite"
        sx={{ position: 'absolute', zIndex: 600, left: 12, bottom: 28, display: 'flex', flexDirection: 'column', gap: 1, maxWidth: 'calc(100% - 24px)' }}
      >
        {edgeToasts.map(({ call, heading }) => (
          <ButtonBase
            key={call.id}
            onClick={() => { setEdgeToasts(prev => prev.filter(t => t.call.id !== call.id)); onSelect(call); }}
            sx={{
              display: 'flex', alignItems: 'center', gap: 1, px: 1.25, py: 0.75, textAlign: 'left',
              justifyContent: 'flex-start', borderRadius: 1.5, bgcolor: 'surface.raised', color: 'text.primary',
              border: '1px solid', borderColor: 'surface.borderStrong',
              borderLeft: `3px solid ${CALL_GROUPS[groupOf(call.callType)].color}`,
              boxShadow: '0 6px 20px rgba(0,0,0,0.5)',
              animation: `osToast ${CALLOUT_MS}ms ease both`,
              '@keyframes osToast': {
                '0%': { opacity: 0, transform: 'translateX(-8px)' },
                '5%, 88%': { opacity: 1, transform: 'translateX(0)' },
                '100%': { opacity: 0 },
              },
              '@media (prefers-reduced-motion: reduce)': { animation: 'none' },
            }}
          >
            <NavigationIcon fontSize="small" sx={{ color: 'text.secondary', transform: `rotate(${heading}deg)`, flexShrink: 0 }} />
            <Box sx={{ minWidth: 0 }}>
              <Typography variant="caption" sx={{ display: 'block', fontWeight: 700 }} noWrap>
                New: {callTypeLabel(call.callType)}{call.summary ? ` · ${call.summary}` : ''}
              </Typography>
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }} noWrap>
                {locationOf(call) || 'Location on map'} · tap to view
              </Typography>
            </Box>
          </ButtonBase>
        ))}
      </Box>
    )}
    </>
  );
};

export default CallMap;
