import React from 'react';
import { Box, ButtonBase, Typography } from '@mui/material';
import { alpha } from '@mui/material/styles';

export interface Segment<T extends string> {
  value: T;
  label: string;
  icon?: React.ReactNode;
  count?: number;
}

interface Props<T extends string> {
  segments: Segment<T>[];
  value: T;
  onChange: (value: T) => void;
  size?: 'small' | 'medium';
  fullWidth?: boolean;
  'aria-label'?: string;
}

/** Pill-style segmented switch — a modern replacement for stacked toggles/tabs. */
function SegmentedControl<T extends string>({ segments, value, onChange, size = 'medium', fullWidth, ...rest }: Props<T>) {
  return (
    <Box
      role="tablist"
      aria-label={rest['aria-label']}
      sx={{
        display: 'inline-flex',
        width: fullWidth ? '100%' : 'auto',
        p: 0.5,
        gap: 0.5,
        bgcolor: 'surface.base',
        border: '1px solid',
        borderColor: 'surface.border',
        borderRadius: 999,
      }}
    >
      {segments.map((seg) => {
        const selected = seg.value === value;
        return (
          <ButtonBase
            key={seg.value}
            role="tab"
            aria-selected={selected}
            onClick={() => onChange(seg.value)}
            sx={{
              flex: fullWidth ? 1 : 'initial',
              gap: 0.75,
              px: size === 'small' ? 1.25 : 2,
              py: size === 'small' ? 0.5 : 0.75,
              borderRadius: 999,
              color: selected ? 'primary.contrastText' : 'text.secondary',
              bgcolor: selected ? 'primary.main' : 'transparent',
              transition: (t) => `background-color ${t.transitions.duration.short}ms, color ${t.transitions.duration.short}ms`,
              '&:hover': { bgcolor: selected ? 'primary.main' : (t) => alpha(t.palette.primary.main, 0.1) },
            }}
          >
            {seg.icon && <Box sx={{ display: 'flex', '& svg': { fontSize: 18 } }}>{seg.icon}</Box>}
            <Typography variant="button" sx={{ fontSize: size === 'small' ? '0.72rem' : '0.8rem', lineHeight: 1 }}>
              {seg.label}
            </Typography>
            {seg.count != null && (
              <Typography variant="caption" sx={{ opacity: 0.7, fontFamily: (t) => t.typography.mono.fontFamily }}>
                {seg.count}
              </Typography>
            )}
          </ButtonBase>
        );
      })}
    </Box>
  );
}

export default SegmentedControl;
