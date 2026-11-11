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
 * weeks, in the viewer's local time. Shown on the statistics page; the caller
 * supplies the heading. Sequential single-hue (scanner green) magnitude ramp, per the dataviz
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

  if (buckets === null) return <Typography variant="caption" color="text.secondary">Loading…</Typography>;
  if (total === 0) return <Typography variant="caption" color="text.secondary">No transmissions in the last 4 weeks.</Typography>;

  return (
    <Box>
      <Box sx={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: 0.5, alignItems: 'center' }}>
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
                        // Fixed height: square cells get enormous in a wide panel.
                        height: { xs: 14, sm: 20 },
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
