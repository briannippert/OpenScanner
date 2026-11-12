import L from 'leaflet';

// leaflet.heat is a plain script that extends the global `L`. ES modules evaluate their
// imports in order, so importing this file before 'leaflet.heat' publishes Leaflet first.
(globalThis as unknown as { L: typeof L }).L = L;

export default L;
