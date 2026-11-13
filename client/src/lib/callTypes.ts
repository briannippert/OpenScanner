import type { MapCall, TransmissionInsight } from '../types';

/** Call types the remote WhisperServer's /transmission endpoint can return. */
export const CALL_TYPES = [
  'fire', 'alarm', 'hazard',
  'medical', 'rescue', 'welfare_check',
  'traffic_stop', 'traffic_collision', 'assault', 'domestic_disturbance', 'theft',
  'burglary', 'suspicious_activity', 'disturbance',
  'public_assistance', 'other',
] as const;

export type CallType = (typeof CALL_TYPES)[number];

/**
 * Color groups for the map. On a scatter-style map every pair of colors can sit side
 * by side, and the validated palette only keeps three hues distinguishable all-pairs
 * (including for colorblind readers), so call types fold into three hue groups plus a
 * neutral "Other". Each group also has its own marker shape, so color is never the
 * only cue. Colors are the dark-mode categorical steps, checked against the dark map.
 */
export type CallGroup = 'fire' | 'medical' | 'police' | 'other';

export interface CallGroupInfo {
  label: string;
  color: string;
  /** Marker shape, the secondary (non-color) encoding. */
  shape: 'square' | 'diamond' | 'circle' | 'dot';
}

export const CALL_GROUPS: Record<CallGroup, CallGroupInfo> = {
  fire: { label: 'Fire & hazard', color: '#d95926', shape: 'square' },
  medical: { label: 'Medical & rescue', color: '#199e70', shape: 'diamond' },
  police: { label: 'Police & traffic', color: '#3987e5', shape: 'circle' },
  other: { label: 'Service & other', color: '#8b949e', shape: 'dot' },
};

export const GROUP_ORDER: CallGroup[] = ['fire', 'medical', 'police', 'other'];

const GROUP_OF: Record<CallType, CallGroup> = {
  fire: 'fire', alarm: 'fire', hazard: 'fire',
  medical: 'medical', rescue: 'medical', welfare_check: 'medical',
  traffic_stop: 'police', traffic_collision: 'police', assault: 'police',
  domestic_disturbance: 'police', theft: 'police', burglary: 'police',
  suspicious_activity: 'police', disturbance: 'police',
  public_assistance: 'other', other: 'other',
};

const LABELS: Partial<Record<CallType, string>> = {
  welfare_check: 'Welfare check',
  traffic_stop: 'Traffic stop',
  traffic_collision: 'Collision',
  domestic_disturbance: 'Domestic',
  suspicious_activity: 'Suspicious activity',
  public_assistance: 'Public assist',
};

/** The group a call type belongs to; unknown types fall into "other". */
export const groupOf = (callType?: string | null): CallGroup =>
  (callType && (GROUP_OF as Record<string, CallGroup>)[callType]) || 'other';

/** Human label for a call type, e.g. traffic_collision → "Collision". */
export function callTypeLabel(callType?: string | null): string {
  if (!callType) return 'Unknown';
  const known = (LABELS as Record<string, string>)[callType];
  if (known) return known;
  const words = callType.replace(/_/g, ' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** Call types in a group, in display order. */
export const typesInGroup = (group: CallGroup): CallType[] => CALL_TYPES.filter(t => GROUP_OF[t] === group);

export type ServiceFilter = 'all' | 'police' | 'fire';

export interface MapFilter {
  /** Call types to show; empty = all. */
  callTypes: ReadonlySet<string>;
  service: ServiceFilter;
  /** Earliest timestamp to include (ms since epoch); null = no lower bound. */
  sinceMs: number | null;
}

/** Whether a call passes the map's filters. Pure, so live calls and fetched calls filter alike. */
export function matchesFilter(call: MapCall, filter: MapFilter): boolean {
  if (filter.callTypes.size > 0 && !filter.callTypes.has(call.callType ?? 'other')) return false;
  if (filter.service !== 'all' && call.service !== filter.service) return false;
  if (filter.sinceMs != null) {
    const t = Date.parse(call.timestamp);
    if (!Number.isNaN(t) && t < filter.sinceMs) return false;
  }
  return true;
}

export const filterMapCalls = (calls: MapCall[], filter: MapFilter): MapCall[] =>
  calls.filter(c => matchesFilter(c, filter));

/** Builds a map call from a live log entry, or null when it has no incident location. */
export function mapCallFromLog(log: {
  id: string; timestamp: string; frequency: number; alphaTag: string; audio_path?: string;
  duration?: number; transcription?: string; insight?: TransmissionInsight | null;
}): MapCall | null {
  const i = log.insight;
  if (!i || !i.isIncident || i.lat == null || i.lon == null) return null;
  return {
    id: log.id,
    timestamp: log.timestamp,
    frequency: log.frequency,
    alphaTag: log.alphaTag,
    audio_path: log.audio_path,
    duration: log.duration,
    transcription: log.transcription,
    callType: i.callType,
    service: i.service,
    summary: i.summary,
    incidentStatus: i.incidentStatus,
    address: i.address,
    placeName: i.placeName,
    matchedAddress: i.matchedAddress,
    lat: i.lat,
    lon: i.lon,
    incidentId: i.incidentId,
  };
}

/** The key that groups calls about one incident: its incident id, else the call's own id. */
export const incidentKeyOf = (call: Pick<MapCall, 'id' | 'incidentId'>): string => call.incidentId || call.id;

/**
 * One map marker: every call about the same incident (same service, same spot, close in
 * time, as grouped by the server). The fields of the newest call (type, status, summary,
 * position) describe the incident, while `id` is the stable incident key, so selection
 * and highlighting survive new calls joining.
 */
export interface MapIncident extends MapCall {
  /** The incident key (see incidentKeyOf). */
  id: string;
  /** Every call in the incident, newest first. calls[0] is the one described. */
  calls: MapCall[];
  /** Timestamp of the first call. */
  firstSeen: string;
}

const timeOf = (call: MapCall) => {
  const t = Date.parse(call.timestamp);
  return Number.isNaN(t) ? 0 : t;
};

/** Collapses calls into incidents, newest incident (by latest call) first. */
export function groupIncidents(calls: MapCall[]): MapIncident[] {
  const byKey = new Map<string, MapCall[]>();
  for (const call of calls) {
    const key = incidentKeyOf(call);
    const list = byKey.get(key);
    if (list) list.push(call); else byKey.set(key, [call]);
  }
  const incidents: MapIncident[] = [];
  for (const [key, list] of byKey) {
    list.sort((a, b) => timeOf(b) - timeOf(a));
    const latest = list[0];
    incidents.push({ ...latest, id: key, calls: list, firstSeen: list[list.length - 1].timestamp });
  }
  return incidents.sort((a, b) => timeOf(b.calls[0]) - timeOf(a.calls[0]));
}
