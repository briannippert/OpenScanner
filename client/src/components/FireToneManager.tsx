import React, { useState } from 'react';
import {
    Button, List, ListItem, ListItemText, IconButton,
    TextField, Box, Typography,
} from '@mui/material';
import DeleteIcon from '@mui/icons-material/Delete';
import EditIcon from '@mui/icons-material/Edit';
import AddIcon from '@mui/icons-material/Add';
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive';
import type { FireToneSet } from '../types';
import FormDialog from './common/FormDialog';
import EmptyState from './common/EmptyState';

interface Props {
    open: boolean;
    onClose: () => void;
    tones: FireToneSet[];
    onSave: (tone: FireToneSet) => void;
    onDelete: (id: number) => void;
}

const FORM_ID = 'firetone-form';
const MONO = '"Roboto Mono", ui-monospace, SFMono-Regular, Menlo, monospace';

const FireToneManager: React.FC<Props> = ({ open, onClose, tones, onSave, onDelete }) => {
    const [editing, setEditing] = useState<FireToneSet | null>(null);
    const [isFormOpen, setIsFormOpen] = useState(false);

    const handleEdit = (tone: FireToneSet) => {
        setEditing(tone);
        setIsFormOpen(true);
    };

    const handleAdd = () => {
        setEditing({ name: '', frequencyA: 0, frequencyB: 0, description: '' } as FireToneSet);
        setIsFormOpen(true);
    };

    const handleFormClose = () => {
        setIsFormOpen(false);
        setEditing(null);
    };

    const handleFormSave = (e: React.FormEvent) => {
        e.preventDefault();
        if (editing) {
            onSave(editing);
            handleFormClose();
        }
    };

    const handleChange = (field: keyof FireToneSet, value: string | number) => {
        if (!editing) return;
        setEditing({ ...editing, [field]: value });
    };

    if (isFormOpen && editing) {
        return (
            <FormDialog
                open
                onClose={handleFormClose}
                title={editing.id ? 'Edit Tone Set' : 'Add Tone Set'}
                icon={<NotificationsActiveIcon />}
                actions={
                    <>
                        <Button onClick={handleFormClose} color="inherit">Cancel</Button>
                        <Button type="submit" form={FORM_ID} variant="contained" color="primary">Save</Button>
                    </>
                }
            >
                <Box component="form" id={FORM_ID} onSubmit={handleFormSave} display="flex" flexDirection="column" gap={2} mt={1}>
                    <TextField label="Name" value={editing.name} onChange={e => handleChange('name', e.target.value)} required autoFocus />
                    <Box display="flex" gap={2}>
                        <TextField
                            label="Tone A (Hz)"
                            type="number"
                            inputProps={{ step: '0.1' }}
                            value={editing.frequencyA}
                            onChange={e => handleChange('frequencyA', parseFloat(e.target.value))}
                            required
                            sx={{ flex: 1 }}
                        />
                        <TextField
                            label="Tone B (Hz)"
                            type="number"
                            inputProps={{ step: '0.1' }}
                            value={editing.frequencyB || ''}
                            onChange={e => handleChange('frequencyB', parseFloat(e.target.value) || 0)}
                            sx={{ flex: 1 }}
                            helperText="Leave blank for a single (long) tone"
                        />
                    </Box>
                    <TextField label="Description" value={editing.description || ''} onChange={e => handleChange('description', e.target.value)} />
                </Box>
            </FormDialog>
        );
    }

    return (
        <FormDialog
            open={open}
            onClose={onClose}
            title="Manage Fire Tone-Outs"
            icon={<NotificationsActiveIcon />}
            maxWidth="md"
            actions={
                <>
                    <Button startIcon={<AddIcon />} variant="contained" color="primary" onClick={handleAdd}>Add Tone Set</Button>
                    <Box flexGrow={1} />
                    <Button onClick={onClose} color="inherit">Close</Button>
                </>
            }
        >
            {tones.length === 0 ? (
                <EmptyState icon={<NotificationsActiveIcon />} title="No tone sets defined" hint="Add a fire tone-out to get alerts when it is detected." />
            ) : (
                <List>
                    {tones.map((tone) => (
                        <ListItem key={tone.id} divider>
                            <ListItemText
                                primary={
                                    <Box display="flex" alignItems="center" gap={2}>
                                        <Typography variant="subtitle1" fontWeight="bold">{tone.name}</Typography>
                                        <Typography variant="body2" color="error" sx={{ fontFamily: MONO }}>
                                            {tone.frequencyB > 0
                                                ? `${tone.frequencyA} / ${tone.frequencyB} Hz`
                                                : `${tone.frequencyA} Hz (single tone)`}
                                        </Typography>
                                    </Box>
                                }
                                secondary={tone.description}
                            />
                            <Box>
                                <IconButton onClick={() => handleEdit(tone)} color="info" aria-label={`Edit ${tone.name}`}>
                                    <EditIcon />
                                </IconButton>
                                <IconButton onClick={() => tone.id && onDelete(tone.id)} color="error" aria-label={`Delete ${tone.name}`}>
                                    <DeleteIcon />
                                </IconButton>
                            </Box>
                        </ListItem>
                    ))}
                </List>
            )}
        </FormDialog>
    );
};

export default FireToneManager;
