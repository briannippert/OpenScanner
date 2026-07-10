import React, { useMemo } from 'react';
import { Box, Typography, Tooltip } from '@mui/material';
import type { CallLog } from '../types';
import { green } from '../viz/ramp';
import { surface } from '../theme/tokens';

interface Props {
  logs: CallLog[];
}

const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const HOUR_TICKS = [0, 6, 12, 18];

/**
 * Recent-activity heatmap: transmissions by day-of-week × hour-of-day.
 * Sequential single-hue (scanner green) magnitude ramp, per the dataviz method.
 */
const ActivityHeatmap: React.FC<Props> = ({ logs }) => {
  const { grid, max } = useMemo(() => {
    const g: number[][] = Array.from({ length: 7 }, () => new Array(24).fill(0));
    let m = 0;
    for (const log of logs) {
      const d = new Date(log.timestamp.endsWith('Z') ? log.timestamp : log.timestamp + 'Z');
      if (isNaN(d.getTime())) continue;
      const day = d.getDay();
      const hour = d.getHours();
      g[day][hour] += 1;
      if (g[day][hour] > m) m = g[day][hour];
    }
    return { grid: g, max: m };
  }, [logs]);

  return (
    <Box sx={{ p: 2, borderBottom: '1px solid', borderColor: 'surface.border' }}>
      <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 700, letterSpacing: 0.6, textTransform: 'uppercase' }}>
        Recent Activity Pattern
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
