import React from 'react';
import { Chip } from '@mui/material';
import type { ChipProps } from '@mui/material';
import { alpha, useTheme } from '@mui/material/styles';

export type StatusTone = 'live' | 'info' | 'warn' | 'error' | 'success' | 'muted';

interface Props {
  label: string;
  tone?: StatusTone;
  icon?: React.ReactElement;
  size?: ChipProps['size'];
  variant?: 'filled' | 'outlined';
  onClick?: ChipProps['onClick'];
  sx?: ChipProps['sx'];
}

/**
 * Semantic status chip driven from theme tokens.
 *
 * Always pairs color with a text label (and optional icon) so status is never
 * conveyed by color alone — fixing the color-only a11y gap of the old raw Chips.
 */
const StatusChip: React.FC<Props> = ({
  label,
  tone = 'muted',
  icon,
  size = 'small',
  variant = 'outlined',
  onClick,
  sx,
}) => {
  const theme = useTheme();
  const colorMap: Record<StatusTone, string> = {
    live: theme.palette.primary.main,
    info: theme.palette.statusColors.info,
    warn: theme.palette.statusColors.warn,
    error: theme.palette.statusColors.error,
    success: theme.palette.statusColors.success,
    muted: theme.palette.statusColors.muted,
  };
  const c = colorMap[tone];

  return (
    <Chip
      label={label}
      icon={icon}
      size={size}
      onClick={onClick}
      variant={variant}
      sx={{
        fontWeight: 700,
        letterSpacing: 0.4,
        color: c,
        borderColor: variant === 'outlined' ? alpha(c, 0.5) : 'transparent',
        backgroundColor: variant === 'filled' ? alpha(c, 0.16) : 'transparent',
        '& .MuiChip-icon': { color: c },
        ...(onClick ? { cursor: 'pointer', '&:hover': { backgroundColor: alpha(c, 0.14) } } : {}),
        ...sx,
      }}
    />
  );
};

export default StatusChip;
