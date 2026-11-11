import React, { useEffect, useMemo, useState } from 'react';
import { Box, Typography, Tooltip } from '@mui/material';
import { green } from '../viz/ramp';
import { surface } from '../theme/tokens';
import { toActivityGrid, type ActivityBucket } from '../lib/activityGrid';

const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const HOUR_TICKS = [0, 6, 12, 18];
/** Four full weeks, so every weekday is represented equally. */
const WINDOW_DAYS = 28;

/**
 * Activity heatmap: transmissions by day-of-week × hour-of-day over the last four
 * weeks. Sequential single-hue (scanner green) magnitude ramp, per the dataviz
 * method.
 *
 * The counts come from the server, aggregated over the whole window. (They used
 * to be derived from the in-memory "recent" list, which is capped at 100
 * transmissions — on a busy scanner that is a few hours, so most of the grid was
 * always empty.)
 */
const ActivityHeatmap: React.FC = () => {
  const [buckets, setBuckets] = useState<ActivityBucket[] | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetch(`/api/history/activity?days=${WINDOW_DAYS}`)
      .then(r => r.json())
      .then((data: unknown) => { if (!cancelled) setBuckets(Array.isArray(data) ? data as ActivityBucket[] : []); })
      .catch(err => { console.error('Failed to fetch activity:', err); if (!cancelled) setBuckets([]); });
    return () => { cancelled = true; };
  }, []);

  const { counts: grid, max, total } = useMemo(() => toActivityGrid(buckets ?? []), [buckets]);

  // Nothing recorded in the window (or still loading): no empty grid.
  if (total === 0) return null;

  return (
    <Box sx={{ p: 2, borderBottom: '1px solid', borderColor: 'surface.border' }}>
      <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 700, letterSpacing: 0.6, textTransform: 'uppercase' }}>
        Activity Pattern · Last 4 Weeks
      </Typography>
      <Box sx={{ mt: 1, display: 'grid', gridTemplateColumns: 'auto 1fr', gap: 0.5, alignItems: 'center' }}>
        {DAYS.map((label, day) => (
          <React.Fragment key={label}>
            <Typography variant="caption" sx={{ color: 'text.disabled', pr: 1, fontSize: '0.62rem', textAlign: 'right' }}>
              {label}
            </Typography>
            <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(24, 1fr)', gap: '2px' }}>
              {grid[day].map((count, hour) => {
                const t = max > 0 ? count / max : 0;
                return (
                  <Tooltip key={hour} title={`${DAYS[day]} ${hour}:00 — ${count} transmission${count === 1 ? '' : 's'}`} arrow>
                    <Box
                      sx={{
                        aspectRatio: '1 / 1',
                        borderRadius: '2px',
                        bgcolor: count === 0 ? surface.raised : green(0.25 + t * 0.75),
                      }}
                    />
                  </Tooltip>
                );
              })}
            </Box>
          </React.Fragment>
        ))}
        {/* Hour axis */}
        <Box />
        <Box sx={{ position: 'relative', height: 14, mt: 0.25 }}>
          {HOUR_TICKS.map(h => (
            <Typography
              key={h}
              variant="caption"
              sx={{ position: 'absolute', left: `${(h / 24) * 100}%`, color: 'text.disabled', fontSize: '0.6rem', fontFamily: (t) => t.typography.mono.fontFamily }}
            >
              {h.toString().padStart(2, '0')}
            </Typography>
          ))}
        </Box>
      </Box>
    </Box>
  );
};

export default ActivityHeatmap;
