import React from 'react';
import { Box, Card, Grid, Typography, Button, IconButton, Tooltip } from '@mui/material';
import AssessmentIcon from '@mui/icons-material/Assessment';
import EditIcon from '@mui/icons-material/Edit';
import RadioIcon from '@mui/icons-material/Radio';
import { alpha } from '@mui/material/styles';
import type { Channel } from '../types';
import SectionCard from './common/SectionCard';
import EmptyState from './common/EmptyState';
import PanelSkeleton from './common/PanelSkeleton';

interface Props {
  channels: Channel[];
  manualHold?: number;
  loaded: boolean;
  onEdit: () => void;
  onToggleAvoid: (ch: Channel) => void;
  onHold: (ch: Channel) => void;
}

const EXPERIMENTAL_MODES = ['FM', 'AM', 'WFM'];
const MONO = '"Roboto Mono", ui-monospace, SFMono-Regular, Menlo, monospace';

const ChannelGrid: React.FC<Props> = ({ channels, manualHold, loaded, onEdit, onToggleAvoid, onHold }) => {
  const isHeld = (ch: Channel) => manualHold === ch.frequency;

  return (
    <SectionCard
      title="Channel Control"
      icon={<AssessmentIcon fontSize="small" />}
      action={
        <Tooltip title="Manage channels">
          <IconButton size="small" onClick={onEdit} aria-label="Manage channels">
            <EditIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      }
      sx={{ flexGrow: 1, minHeight: { xs: 300, md: 0 } }}
    >
      {!loaded ? (
        <PanelSkeleton rows={4} rowHeight={72} />
      ) : channels.length === 0 ? (
        <EmptyState
          icon={<RadioIcon />}
          title="No channels configured"
          hint="Add frequencies to start scanning. Use the edit button above to manage channels."
        />
      ) : (
        <Grid container spacing={1.5}>
          {channels.map((ch) => {
            const held = isHeld(ch);
            return (
              <Grid size={{ xs: 12, sm: 6, md: 12 }} key={ch.frequency}>
                <Card
                  sx={{
                    borderColor: held ? 'warning.main' : 'surface.border',
                    backgroundColor: 'surface.raised',
                    transition: (t) => `border-color ${t.transitions.duration.short}ms, background-color ${t.transitions.duration.short}ms`,
                    '&:hover': { backgroundColor: 'surface.overlay' },
                  }}
                >
                  <Box sx={{ p: 1.75 }}>
                    <Box display="flex" justifyContent="space-between" alignItems="flex-start" gap={1}>
                      <Box sx={{ minWidth: 0 }}>
                        <Typography variant="subtitle1" fontWeight={700} color={held ? 'warning.main' : 'text.primary'} noWrap>
                          {ch.alphaTag}
                        </Typography>
                        <Typography variant="caption" color="text.secondary" noWrap sx={{ display: 'block' }}>
                          {ch.description}
                        </Typography>
                      </Box>
                      <Typography variant="body2" sx={{ fontFamily: MONO, color: 'primary.main', flexShrink: 0 }}>
                        {ch.frequency}
                      </Typography>
                    </Box>
                    <Box mt={1.25} display="flex" alignItems="center" gap={1}>
                      <Box
                        component="span"
                        sx={{
                          px: 0.75, py: 0.25, borderRadius: 1, fontSize: '0.65rem', fontWeight: 700,
                          color: 'text.secondary', bgcolor: (t) => alpha(t.palette.surface.overlay, 0.9),
                          border: '1px solid', borderColor: 'surface.border',
                        }}
                      >
                        {EXPERIMENTAL_MODES.includes(ch.mode?.toUpperCase()) ? `${ch.mode} (EXP)` : ch.mode}
                      </Box>
                      <Box flexGrow={1} />
                      <Button
                        variant={ch.avoid ? 'contained' : 'outlined'}
                        color={ch.avoid ? 'error' : 'inherit'}
                        size="small"
                        onClick={() => onToggleAvoid(ch)}
                        aria-pressed={ch.avoid}
                        aria-label={`${ch.avoid ? 'Stop avoiding' : 'Avoid'} ${ch.alphaTag}`}
                        sx={{ minWidth: 'auto', px: 1, py: 0.25, fontSize: '0.7rem' }}
                      >
                        AVOID
                      </Button>
                      <Button
                        variant={held ? 'contained' : 'outlined'}
                        color={held ? 'warning' : 'inherit'}
                        size="small"
                        onClick={() => onHold(ch)}
                        aria-pressed={held}
                        aria-label={`${held ? 'Resume scanning from' : 'Hold'} ${ch.alphaTag}`}
                        sx={{ minWidth: 'auto', px: 1, py: 0.25, fontSize: '0.7rem' }}
                      >
                        HOLD
                      </Button>
                    </Box>
                  </Box>
                </Card>
              </Grid>
            );
          })}
        </Grid>
      )}
    </SectionCard>
  );
};

export default ChannelGrid;
