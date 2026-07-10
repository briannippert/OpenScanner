import React from 'react';
import { Box, Skeleton } from '@mui/material';

interface Props {
  /** Number of skeleton rows to render. */
  rows?: number;
  /** Height of each row block. */
  rowHeight?: number;
  gap?: number;
}

/** Loading placeholder: a stack of rounded skeleton blocks. */
const PanelSkeleton: React.FC<Props> = ({ rows = 4, rowHeight = 64, gap = 1.5 }) => (
  <Box sx={{ display: 'flex', flexDirection: 'column', gap }}>
    {Array.from({ length: rows }).map((_, i) => (
      <Skeleton
        key={i}
        variant="rounded"
        height={rowHeight}
        animation="wave"
        sx={{ bgcolor: 'surface.raised', borderRadius: 1.5 }}
      />
    ))}
  </Box>
);

export default PanelSkeleton;
