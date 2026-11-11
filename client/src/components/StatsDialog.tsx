import React, { useEffect, useMemo, useState } from 'react';
import { Box, Typography, Tooltip, Alert } from '@mui/material';
import InsightsIcon from '@mui/icons-material/Insights';
import FormDialog from './common/FormDialog';
import SectionCard from './common/SectionCard';
import SegmentedControl from './common/SegmentedControl';
import EmptyState from './common/EmptyState';
import ActivityHeatmap from './ActivityHeatmap';
import { apiJson } from './common/apiBase';
import { accent, surface } from '../theme/tokens';
import type { ActivityBucket } from '../lib/activityGrid';
import type { NameFor } from '../lib/aliasLabels';
import {
  formatCount, formatDuration, niceMax, toTimeSeries,
  type SeriesPoint, type TransmissionStats,
} from '../lib/stats';

interface Props {
  open: boolean;
  onClose: () => void;
  /** Resolves per-channel display names for talkgroups and source radios. */
  nameFor?: NameFor;
}

type WindowKey = '1' | '7' | '28';
const WINDOWS: { value: WindowKey; label: string; phrase: string }[] = [
  { value: '1', label: '24 hours', phrase: 'the last 24 hours' },
  { value: '7', label: '7 days', phrase: 'the last 7 days' },
  { value: '28', label: '4 weeks', phrase: 'the last 4 weeks' },
];

const MONO = '"Roboto Mono", ui-monospace, SFMono-Regular, Menlo, monospace';
const plural = (n: number, word: string) => `${n.toLocaleString('en-US')} ${word}${n === 1 ? '' : 's'}`;

/* ------------------------------------------------------------------------- */
/* Stat tile                                                                  */
/* ------------------------------------------------------------------------- */

const StatTile: React.FC<{ label: string; value: string; detail?: string }> = ({ label, value, detail }) => (
  <Box sx={{ p: 2, bgcolor: 'surface.surface', border: '1px solid', borderColor: 'surface.border', borderRadius: 2, minWidth: 0 }}>
    <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }} noWrap>{label}</Typography>
    <Typography sx={{ fontSize: { xs: '1.25rem', sm: '1.6rem' }, fontWeight: 600, lineHeight: 1.25, color: 'text.primary' }} noWrap>{value}</Typography>
    <Typography variant="caption" sx={{ color: 'text.disabled', display: 'block', minHeight: '1.4em' }} noWrap>{detail ?? ''}</Typography>
  </Box>
);

/* ------------------------------------------------------------------------- */
/* Column chart: transmissions over time                                      */
/* ------------------------------------------------------------------------- */

const CHART_HEIGHT = 160;

const TimeChart: React.FC<{ points: SeriesPoint[]; hourly: boolean }> = ({ points, hourly }) => {
  const max = Math.max(0, ...points.map(p => p.count));
  const top = niceMax(max);
  const peak = points.findIndex(p => p.count === max);

  const slotLabel = (p: SeriesPoint) => hourly
    ? `${p.start.toLocaleDateString([], { weekday: 'short' })} ${String(p.start.getHours()).padStart(2, '0')}:00`
    : p.start.toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' });
  const tickLabel = (p: SeriesPoint) => hourly
    ? String(p.start.getHours()).padStart(2, '0')
    : p.start.toLocaleDateString([], { month: 'short', day: 'numeric' });
  // Label about four slots along the axis (few enough to fit a phone), always
  // including the last one.
  const every = Math.max(1, Math.ceil(points.length / 4));
  const showTick = (i: number) => (points.length - 1 - i) % every === 0;

  return (
    <Box
      role="img"
      aria-label={`Transmissions per ${hourly ? 'hour' : 'day'}. Peak ${plural(max, 'transmission')} on ${peak >= 0 ? slotLabel(points[peak]) : 'no day'}.`}
      sx={{ display: 'grid', gridTemplateColumns: 'auto 1fr', columnGap: 1 }}
    >
      {/* Y axis: top, middle, zero */}
      <Box sx={{ height: CHART_HEIGHT, display: 'flex', flexDirection: 'column', justifyContent: 'space-between', alignItems: 'flex-end' }}>
        {[top, top / 2, 0].map(v => (
          <Typography key={v} variant="caption" sx={{ color: 'text.disabled', fontSize: '0.62rem', lineHeight: 1, fontFamily: MONO, fontVariantNumeric: 'tabular-nums' }}>
            {formatCount(v)}
          </Typography>
        ))}
      </Box>

      <Box sx={{ position: 'relative', height: CHART_HEIGHT }}>
        {/* Recessive gridlines; the baseline is the bottom one. */}
        {[0, 50, 100].map(pct => (
          <Box key={pct} sx={{ position: 'absolute', left: 0, right: 0, top: `${pct}%`, borderTop: '1px solid', borderColor: 'surface.border' }} />
        ))}
        <Box sx={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'stretch', gap: '2px' }}>
          {points.map((p, i) => (
            <Tooltip key={p.start.getTime()} title={`${slotLabel(p)} — ${plural(p.count, 'transmission')}`} arrow placement="top">
              {/* The whole slot is the hover target, not just the bar. */}
              <Box sx={{
                flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', justifyContent: 'flex-end', alignItems: 'center',
                '&:hover > .bar': { bgcolor: accent.bright },
                '&:hover': { bgcolor: 'rgba(255,255,255,0.03)' },
              }}>
                {i === peak && max > 0 && (
                  <Typography variant="caption" sx={{ color: 'text.secondary', fontSize: '0.62rem', lineHeight: 1.3, fontFamily: MONO }}>
                    {formatCount(p.count)}
                  </Typography>
                )}
                <Box
                  className="bar"
                  sx={{
                    width: '100%', maxWidth: 24,
                    height: `${(p.count / top) * 100}%`,
                    // Keep a non-zero slot visible even when it is tiny next to the peak.
                    minHeight: p.count > 0 ? 2 : 0,
                    bgcolor: accent.main,
                    borderRadius: '4px 4px 0 0',
                  }}
                />
              </Box>
            </Tooltip>
          ))}
        </Box>
      </Box>

      {/* X axis */}
      <Box />
      <Box sx={{ display: 'flex', gap: '2px', mt: 0.5 }}>
        {points.map((p, i) => (
          // A label is wider than its slot, so it overhangs both sides — except the
          // last, which is pinned to the right edge so it never leaves the chart.
          <Box key={p.start.getTime()} sx={{ flex: 1, minWidth: 0, display: 'flex', justifyContent: i === points.length - 1 ? 'flex-end' : 'center' }}>
            {showTick(i) && (
              <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.62rem', whiteSpace: 'nowrap', fontFamily: MONO }}>
                {tickLabel(p)}
              </Typography>
            )}
          </Box>
        ))}
      </Box>
    </Box>
  );
};

/* ------------------------------------------------------------------------- */
/* Ranked horizontal bars                                                     */
/* ------------------------------------------------------------------------- */

interface RankedRow {
  key: string;
  name: string;
  /** Smaller text under the name (e.g. the channel a talkgroup was heard on). */
  sub?: string;
  count: number;
  /** Extra figure shown after the count (e.g. airtime). */
  detail?: string;
}

const RankedBars: React.FC<{ rows: RankedRow[]; empty: string }> = ({ rows, empty }) => {
  if (rows.length === 0) return <Typography variant="caption" color="text.secondary">{empty}</Typography>;
  const max = Math.max(...rows.map(r => r.count));
  return (
    <Box component="ol" sx={{ listStyle: 'none', m: 0, p: 0, display: 'flex', flexDirection: 'column', gap: 1.25 }}>
      {rows.map(r => (
        <Tooltip
          key={r.key}
          title={`${r.name}${r.sub ? ` · ${r.sub}` : ''} — ${plural(r.count, 'transmission')}${r.detail ? `, ${r.detail}` : ''}`}
          arrow placement="top-start"
        >
          <Box component="li" sx={{ minWidth: 0, '&:hover .bar': { bgcolor: accent.bright } }}>
            <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1, mb: 0.5 }}>
              <Typography variant="body2" sx={{ color: 'text.primary', minWidth: 0, flexShrink: 1 }} noWrap>{r.name}</Typography>
              {/* Gives up its width long before the name does. */}
              {r.sub && <Typography variant="caption" sx={{ color: 'text.disabled', minWidth: 0, flexShrink: 1000 }} noWrap>{r.sub}</Typography>}
              <Box sx={{ flexGrow: 1 }} />
              <Typography variant="caption" sx={{ color: 'text.secondary', whiteSpace: 'nowrap', fontFamily: MONO, fontVariantNumeric: 'tabular-nums' }}>
                {r.count.toLocaleString('en-US')}{r.detail ? ` · ${r.detail}` : ''}
              </Typography>
            </Box>
            <Box sx={{ height: 8, bgcolor: surface.raised, borderRadius: '0 4px 4px 0' }}>
              <Box className="bar" sx={{ height: '100%', width: `${(r.count / max) * 100}%`, minWidth: 2, bgcolor: accent.main, borderRadius: '0 4px 4px 0' }} />
            </Box>
          </Box>
        </Tooltip>
      ))}
    </Box>
  );
};

/* ------------------------------------------------------------------------- */
/* The page                                                                   */
/* ------------------------------------------------------------------------- */

const channelName = (alphaTag: string, frequency: number) => alphaTag || `${frequency} MHz`;

const StatsDialog: React.FC<Props> = ({ open, onClose, nameFor }) => {
  const [windowKey, setWindowKey] = useState<WindowKey>('7');
  const [stats, setStats] = useState<TransmissionStats | null>(null);
  const [buckets, setBuckets] = useState<ActivityBucket[] | null>(null);
  const [failed, setFailed] = useState(false);
  const days = Number(windowKey);

  // Load when opened and whenever the window changes. Previous figures stay on
  // screen while the new ones load, so switching windows does not flash empty.
  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    void Promise.all([
      apiJson<TransmissionStats>(`/api/history/stats?days=${days}`),
      apiJson<ActivityBucket[]>(`/api/history/activity?days=${days}`),
    ]).then(([s, b]) => {
      if (cancelled) return;
      setFailed(s === null);
      if (s) setStats(s);
      setBuckets(b ?? []);
    });
    return () => { cancelled = true; };
  }, [open, days]);

  // Only show figures that belong to the selected window.
  const current = stats && stats.days === days ? stats : null;
  const series = useMemo(() => (current && buckets ? toTimeSeries(buckets, days) : []), [current, buckets, days]);
  const phrase = WINDOWS.find(w => w.value === windowKey)!.phrase;
  const sum = current?.summary;

  const idRows = (rows: TransmissionStats['talkgroups'], kind: 'TG' | 'SRC'): RankedRow[] => rows.map(r => ({
    key: `${r.alphaTag}|${r.frequency}|${r.id}`,
    name: nameFor?.(kind, r.id, r.alphaTag, r.frequency) ?? String(r.id),
    sub: channelName(r.alphaTag, r.frequency),
    count: r.count,
    detail: formatDuration(r.airtimeSeconds),
  }));

  return (
    <FormDialog open={open} onClose={onClose} title="Statistics" icon={<InsightsIcon />} maxWidth="lg">
      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
        {/* Filters: one row, above everything they scope. */}
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, flexWrap: 'wrap' }}>
          <SegmentedControl<WindowKey>
            aria-label="Time range"
            size="small"
            segments={WINDOWS.map(({ value, label }) => ({ value, label }))}
            value={windowKey}
            onChange={setWindowKey}
          />
          <Typography variant="caption" color="text.secondary">Times are shown in your local time zone.</Typography>
        </Box>

        {failed && <Alert severity="error">Could not load statistics from the server.</Alert>}

        {!current ? (
          !failed && <Typography variant="body2" color="text.secondary" sx={{ py: 6, textAlign: 'center' }}>Loading…</Typography>
        ) : sum!.transmissions === 0 ? (
          <EmptyState icon={<InsightsIcon />} title="Nothing recorded yet" hint={`No transmissions in ${phrase}.`} />
        ) : (
          <>
            <Box sx={{ display: 'grid', gap: 1.5, gridTemplateColumns: { xs: 'repeat(2, 1fr)', sm: 'repeat(3, 1fr)', lg: 'repeat(6, 1fr)' } }}>
              <StatTile label="Transmissions" value={formatCount(sum!.transmissions)} detail={days === 1 ? `${formatCount(sum!.transmissions / 24)} per hour` : `${formatCount(sum!.transmissions / days)} per day`} />
              <StatTile label="Airtime" value={formatDuration(sum!.airtimeSeconds)} detail={`${((sum!.airtimeSeconds / (days * 86400)) * 100).toFixed(1)}% of the time`} />
              <StatTile label="Average length" value={formatDuration(sum!.airtimeSeconds / sum!.transmissions)} detail={`longest ${formatDuration(sum!.longestSeconds)}`} />
              <StatTile label="Active channels" value={formatCount(sum!.activeChannels)} />
              <StatTile label="Transcribed" value={`${Math.round((sum!.transcribed / sum!.transmissions) * 100)}%`} detail={`${formatCount(sum!.transcribed)} of ${formatCount(sum!.transmissions)}`} />
              <StatTile label="Fire tone-outs" value={formatCount(sum!.toneOuts)} />
            </Box>

            <SectionCard title={days === 1 ? 'Transmissions per hour' : 'Transmissions per day'}>
              <TimeChart points={series} hourly={days === 1} />
            </SectionCard>

            <Box sx={{ display: 'grid', gap: 2, gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, alignItems: 'start' }}>
              <SectionCard title="Busiest channels">
                <RankedBars
                  empty="No channel activity."
                  rows={current.channels.map(c => ({
                    key: `${c.alphaTag}|${c.frequency}`,
                    name: channelName(c.alphaTag, c.frequency),
                    sub: c.alphaTag ? `${c.frequency} MHz` : undefined,
                    count: c.count,
                    detail: formatDuration(c.airtimeSeconds),
                  }))}
                />
              </SectionCard>
              <SectionCard title="Fire tone-outs">
                <RankedBars
                  empty={`No tone-outs in ${phrase}.`}
                  rows={current.tones.map(t => ({ key: t.name, name: t.name, count: t.count }))}
                />
              </SectionCard>
              <SectionCard title="Busiest talkgroups">
                <RankedBars empty="No talkgroup IDs were received." rows={idRows(current.talkgroups, 'TG')} />
              </SectionCard>
              <SectionCard title="Most active radios">
                <RankedBars empty="No radio IDs were received." rows={idRows(current.sources, 'SRC')} />
              </SectionCard>
            </Box>
          </>
        )}

        {/* Always four weeks, whatever the range above: a weekday-by-hour pattern
            needs several of each weekday to mean anything. */}
        <SectionCard title="Weekly pattern · last 4 weeks">
          <ActivityHeatmap />
        </SectionCard>
      </Box>
    </FormDialog>
  );
};

export default StatsDialog;
