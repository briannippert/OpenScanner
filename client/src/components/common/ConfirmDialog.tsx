import React from 'react';
import { Button, Typography } from '@mui/material';
import WarningAmberIcon from '@mui/icons-material/WarningAmber';
import FormDialog from './FormDialog';

interface Props {
  open: boolean;
  title: string;
  /** What will happen, in a sentence. */
  children: React.ReactNode;
  confirmLabel?: string;
  onConfirm: () => void;
  onCancel: () => void;
}

/**
 * Asks before an action that cannot be undone. Cancel is the default (focused) button,
 * so a stray Enter or a double-click on the trigger does not confirm.
 */
const ConfirmDialog: React.FC<Props> = ({ open, title, children, confirmLabel = 'Delete', onConfirm, onCancel }) => (
  <FormDialog
    open={open}
    onClose={onCancel}
    title={title}
    icon={<WarningAmberIcon />}
    maxWidth="xs"
    actions={
      <>
        <Button onClick={onCancel} color="inherit" autoFocus>Cancel</Button>
        <Button onClick={onConfirm} color="error" variant="contained">{confirmLabel}</Button>
      </>
    }
  >
    <Typography variant="body2" component="div">{children}</Typography>
  </FormDialog>
);

export default ConfirmDialog;
