import React from 'react';
import {
  Dialog, DialogTitle, DialogContent, DialogActions, Box, Typography, IconButton,
} from '@mui/material';
import type { DialogProps } from '@mui/material';
import CloseIcon from '@mui/icons-material/Close';

interface Props {
  open: boolean;
  onClose: () => void;
  title: React.ReactNode;
  icon?: React.ReactNode;
  /** Rendered in the footer (buttons). */
  actions?: React.ReactNode;
  children: React.ReactNode;
  maxWidth?: DialogProps['maxWidth'];
  fullWidth?: boolean;
  /** Disable the built-in content padding (e.g. for full-bleed content). */
  disableContentPadding?: boolean;
  /** Prevent closing (e.g. while a destructive action is in flight). */
  disableClose?: boolean;
}

/**
 * Shared dialog shell: consistent titled header (icon + title + close button),
 * divided scrollable content, and a footer actions row. Replaces the repeated
 * Dialog/DialogTitle/DialogContent/DialogActions boilerplate across the managers.
 */
const FormDialog: React.FC<Props> = ({
  open,
  onClose,
  title,
  icon,
  actions,
  children,
  maxWidth = 'sm',
  fullWidth = true,
  disableContentPadding,
  disableClose,
}) => (
  <Dialog
    open={open}
    onClose={() => !disableClose && onClose()}
    maxWidth={maxWidth}
    fullWidth={fullWidth}
  >
    <DialogTitle sx={{ p: 0 }}>
      <Box
        sx={{
          display: 'flex',
          alignItems: 'center',
          gap: 1.25,
          px: 3,
          py: 2,
          borderBottom: '1px solid',
          borderColor: 'surface.border',
        }}
      >
        {icon && <Box sx={{ display: 'flex', color: 'primary.main' }}>{icon}</Box>}
        <Typography
          variant="h6"
          component="span"
          sx={{ flexGrow: 1, fontWeight: 700, letterSpacing: 0.4 }}
        >
          {title}
        </Typography>
        <IconButton aria-label="Close" onClick={onClose} disabled={disableClose} size="small" edge="end">
          <CloseIcon fontSize="small" />
        </IconButton>
      </Box>
    </DialogTitle>
    <DialogContent dividers sx={disableContentPadding ? { p: 0 } : undefined}>
      {children}
    </DialogContent>
    {actions && <DialogActions sx={{ px: 3, py: 2 }}>{actions}</DialogActions>}
  </Dialog>
);

export default FormDialog;
