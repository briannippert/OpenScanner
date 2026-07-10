import React from 'react';
import { Box, Typography } from '@mui/material';

interface Props {
  icon?: React.ReactNode;
  title: string;
  hint?: string;
  /** Compact variant for small panels (e.g. inline lists). */
  dense?: boolean;
}

/**
 * Designed empty state: centered icon + title + optional hint.
 * Replaces the scattered low-contrast one-line "No …" strings.
 */
const EmptyState: React.FC<Props> = ({ icon, title, hint, dense }) => (
  <Box
    sx={{
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'center',
      justifyContent: 'center',
      textAlign: 'center',
      gap: dense ? 0.75 : 1.25,
      py: dense ? 3 : 6,
      px: 2,
      color: 'text.secondary',
    }}
  >
    {icon && (
      <Box sx={{ color: 'text.disabled', '& svg': { fontSize: dense ? 28 : 40 }, display: 'flex' }}>
        {icon}
      </Box>
    )}
    <Typography variant={dense ? 'body2' : 'subtitle1'} sx={{ fontWeight: 600, color: 'text.primary' }}>
      {title}
    </Typography>
    {hint && (
      <Typography variant="caption" sx={{ maxWidth: 320, color: 'text.secondary' }}>
        {hint}
      </Typography>
    )}
  </Box>
);

export default EmptyState;
