import React from 'react';
import { Box, Paper, Typography } from '@mui/material';
import type { SxProps, Theme } from '@mui/material';

interface Props {
  title?: string;
  icon?: React.ReactNode;
  /** Optional actions rendered on the right of the header (e.g. an edit button). */
  action?: React.ReactNode;
  children: React.ReactNode;
  /** Applied to the scrollable body region. */
  bodySx?: SxProps<Theme>;
  sx?: SxProps<Theme>;
  /** Remove body padding (e.g. for lists that manage their own). */
  disableBodyPadding?: boolean;
}

/**
 * Titled panel shell with a consistent header row + padded, scrollable body.
 * Standardizes the ad-hoc `Paper + header Box` pattern used across the dashboard.
 */
const SectionCard: React.FC<Props> = ({
  title,
  icon,
  action,
  children,
  bodySx,
  sx,
  disableBodyPadding,
}) => (
  <Paper
    elevation={0}
    sx={{
      display: 'flex',
      flexDirection: 'column',
      minHeight: 0,
      backgroundColor: 'surface.surface',
      border: '1px solid',
      borderColor: 'surface.border',
      borderRadius: 2,
      overflow: 'hidden',
      ...sx,
    }}
  >
    {(title || action) && (
      <Box
        sx={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          gap: 1,
          px: 2,
          py: 1.25,
          borderBottom: '1px solid',
          borderColor: 'surface.border',
          flexShrink: 0,
        }}
      >
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, minWidth: 0 }}>
          {icon && <Box sx={{ display: 'flex', color: 'primary.main' }}>{icon}</Box>}
          {title && (
            <Typography
              variant="subtitle2"
              sx={{ fontWeight: 700, letterSpacing: 0.6, color: 'text.secondary', textTransform: 'uppercase' }}
              noWrap
            >
              {title}
            </Typography>
          )}
        </Box>
        {action}
      </Box>
    )}
    <Box sx={{ flexGrow: 1, minHeight: 0, overflowY: 'auto', p: disableBodyPadding ? 0 : 2, ...bodySx }}>
      {children}
    </Box>
  </Paper>
);

export default SectionCard;
