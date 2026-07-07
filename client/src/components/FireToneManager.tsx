import React, { useState } from 'react';
import { 
    Dialog, DialogTitle, DialogContent, DialogActions, 
    Button, List, ListItem, ListItemText, IconButton, 
    TextField, Box, Fab, Typography
} from '@mui/material';
import DeleteIcon from '@mui/icons-material/Delete';
import EditIcon from '@mui/icons-material/Edit';
import AddIcon from '@mui/icons-material/Add';
import type { FireToneSet } from '../types';

interface Props {
    open: boolean;
    onClose: () => void;
    tones: FireToneSet[];
    onSave: (tone: FireToneSet) => void;
    onDelete: (id: number) => void;
}

const FireToneManager: React.FC<Props> = ({ open, onClose, tones, onSave, onDelete }) => {
    const [editing, setEditing] = useState<FireToneSet | null>(null);
    const [isFormOpen, setIsFormOpen] = useState(false);

    const handleEdit = (tone: FireToneSet) => {
        setEditing(tone);
        setIsFormOpen(true);
    };

    const handleAdd = () => {
        setEditing({
            name: '',
            frequencyA: 0,
            frequencyB: 0,
            description: ''
        } as FireToneSet);
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
            <Dialog open={true} onClose={handleFormClose} maxWidth="sm" fullWidth>
                <form onSubmit={handleFormSave}>
                    <DialogTitle>{editing.id ? 'Edit Tone Set' : 'Add Tone Set'}</DialogTitle>
                    <DialogContent>
                        <Box display="flex" flexDirection="column" gap={2} mt={1}>
                            <TextField 
                                label="Name" 
                                value={editing.name} 
                                onChange={e => handleChange('name', e.target.value)} 
                                required autoFocus
                            />
                            <Box display="flex" gap={2}>
                                <TextField
                                    label="Tone A (Hz)"
                                    type="number"
                                    inputProps={{ step: "0.1" }}
                                    value={editing.frequencyA}
                                    onChange={e => handleChange('frequencyA', parseFloat(e.target.value))}
                                    required
                                    sx={{ flex: 1 }}
                                />
                                <TextField
                                    label="Tone B (Hz)"
                                    type="number"
                                    inputProps={{ step: "0.1" }}
                                    value={editing.frequencyB || ''}
                                    onChange={e => handleChange('frequencyB', parseFloat(e.target.value) || 0)}
                                    sx={{ flex: 1 }}
                                    helperText="Leave blank for a single (long) tone"
                                />
                            </Box>
                            <TextField 
                                label="Description" 
                                value={editing.description || ''} 
                                onChange={e => handleChange('description', e.target.value)} 
                            />
                        </Box>
                    </DialogContent>
                    <DialogActions>
                        <Button onClick={handleFormClose}>Cancel</Button>
                        <Button type="submit" variant="contained" color="primary">Save</Button>
                    </DialogActions>
                </form>
            </Dialog>
        );
    }

    return (
        <Dialog open={open} onClose={onClose} maxWidth="md" fullWidth>
            <DialogTitle display="flex" justifyContent="space-between" alignItems="center">
                Manage Fire Tone Outs
                <Fab size="small" color="primary" onClick={handleAdd}>
                    <AddIcon />
                </Fab>
            </DialogTitle>
            <DialogContent dividers>
                <List>
                    {tones.map((tone) => (
                        <ListItem key={tone.id} divider>
                            <ListItemText 
                                primary={
                                    <Box display="flex" alignItems="center" gap={2}>
                                        <Typography variant="subtitle1" fontWeight="bold">{tone.name}</Typography>
                                        <Typography variant="body2" color="error" fontFamily="monospace">
                                            {tone.frequencyB > 0
                                                ? `${tone.frequencyA} / ${tone.frequencyB} Hz`
                                                : `${tone.frequencyA} Hz (single tone)`}
                                        </Typography>
                                    </Box>
                                }
                                secondary={tone.description}
                            />
                            <Box>
                                <IconButton onClick={() => handleEdit(tone)} color="info">
                                    <EditIcon />
                                </IconButton>
                                <IconButton onClick={() => tone.id && onDelete(tone.id)} color="error">
                                    <DeleteIcon />
                                </IconButton>
                            </Box>
                        </ListItem>
                    ))}
                    {tones.length === 0 && (
                        <Typography variant="body2" color="textSecondary" align="center" py={4}>
                            No fire tone sets defined.
                        </Typography>
                    )}
                </List>
            </DialogContent>
            <DialogActions>
                <Button onClick={onClose}>Close</Button>
            </DialogActions>
        </Dialog>
    );
};

export default FireToneManager;
