import { describe, it, expect } from 'vitest';
import {
  CALL_TYPES, GROUP_ORDER, callTypeLabel, filterMapCalls, groupIncidents, groupOf, mapCallFromLog, typesInGroup,
} from './callTypes';
import type { CallLog, MapCall } from '../types';

const call = (id: string, callType: string, service: string, timestamp = '2026-11-11T12:00:00Z'): MapCall => ({
  id, timestamp, frequency: 154.4, alphaTag: 'Salem FD', callType, service, lat: 42.78, lon: -71.23,
});

describe('callTypes', () => {
  it('puts every call type in exactly one group', () => {
    const grouped = GROUP_ORDER.flatMap(typesInGroup);
    expect(grouped.sort()).toEqual([...CALL_TYPES].sort());
  });

  it('groups unknown and missing types as other', () => {
    expect(groupOf('fire')).toBe('fire');
    expect(groupOf('theft')).toBe('police');
    expect(groupOf('rescue')).toBe('medical');
    expect(groupOf('alien_landing')).toBe('other');
    expect(groupOf(null)).toBe('other');
  });

  it('labels call types', () => {
    expect(callTypeLabel('traffic_collision')).toBe('Collision');
    expect(callTypeLabel('fire')).toBe('Fire');
    expect(callTypeLabel('new_type')).toBe('New type');
    expect(callTypeLabel(undefined)).toBe('Unknown');
  });

  it('filters by type, service and time', () => {
    const calls = [
      call('a', 'fire', 'fire', '2026-11-11T12:00:00Z'),
      call('b', 'theft', 'police', '2026-11-11T11:00:00Z'),
      call('c', 'medical', 'fire', '2026-11-10T12:00:00Z'),
    ];
    const all = { callTypes: new Set<string>(), service: 'all' as const, sinceMs: null };
    expect(filterMapCalls(calls, all).map(c => c.id)).toEqual(['a', 'b', 'c']);
    expect(filterMapCalls(calls, { ...all, callTypes: new Set(['theft', 'medical']) }).map(c => c.id)).toEqual(['b', 'c']);
    expect(filterMapCalls(calls, { ...all, service: 'fire' }).map(c => c.id)).toEqual(['a', 'c']);
    expect(filterMapCalls(calls, { ...all, sinceMs: Date.parse('2026-11-11T00:00:00Z') }).map(c => c.id)).toEqual(['a', 'b']);
  });

  it('turns a live log with a located incident into a map call', () => {
    const log: CallLog = {
      id: 'x', timestamp: '2026-11-11T12:00:00Z', frequency: 154.4, alphaTag: 'Salem FD', description: '',
      audio_path: 'x.raw', duration: 3, transcription: 'Engine 2 respond',
      insight: { transmissionId: 'x', analyzedAt: '', isIncident: true, callType: 'fire', service: 'fire', lat: 42.7, lon: -71.2 },
    };
    expect(mapCallFromLog(log)).toMatchObject({ id: 'x', callType: 'fire', lat: 42.7, lon: -71.2, audio_path: 'x.raw' });
    expect(mapCallFromLog({ ...log, insight: { ...log.insight!, lat: null } })).toBeNull();
    expect(mapCallFromLog({ ...log, insight: { ...log.insight!, isIncident: false } })).toBeNull();
    expect(mapCallFromLog({ ...log, insight: undefined })).toBeNull();
  });

  it('carries the incident id from a live log', () => {
    const log: CallLog = {
      id: 'y', timestamp: '2026-11-11T12:00:00Z', frequency: 154.4, alphaTag: 'Salem FD', description: '',
      insight: { transmissionId: 'y', analyzedAt: '', isIncident: true, callType: 'fire', lat: 42.7, lon: -71.2, incidentId: 'x' },
    };
    expect(mapCallFromLog(log)?.incidentId).toBe('x');
  });

  it('collapses calls about one incident into one, described by its newest call', () => {
    const calls: MapCall[] = [
      { ...call('d1', 'fire', 'fire', '2026-11-11T12:00:00Z'), incidentId: 'd1', incidentStatus: 'dispatched' },
      { ...call('d2', 'fire', 'fire', '2026-11-11T12:06:00Z'), incidentId: 'd1', incidentStatus: 'on_scene' },
      { ...call('pd', 'theft', 'police', '2026-11-11T12:03:00Z'), incidentId: 'pd' },
      // Not grouped yet (no incident id): its own incident.
      call('solo', 'medical', 'fire', '2026-11-11T11:00:00Z'),
    ];
    const incidents = groupIncidents(calls);
    expect(incidents.map(i => i.id)).toEqual(['d1', 'pd', 'solo']);
    expect(incidents[0].calls.map(c => c.id)).toEqual(['d2', 'd1']);
    expect(incidents[0].incidentStatus).toBe('on_scene');
    expect(incidents[0].firstSeen).toBe('2026-11-11T12:00:00Z');
    expect(incidents[2].calls).toHaveLength(1);
  });
});
