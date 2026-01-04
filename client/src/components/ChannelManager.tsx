import React, { useState } from 'react';
import { 
    Dialog, DialogTitle, DialogContent, DialogActions, 
    Button, List, ListItem, ListItemText, IconButton, 
    TextField, Box, Fab, Typography, MenuItem, Select, FormControl, InputLabel
} from '@mui/material';
import DeleteIcon from '@mui/icons-material/Delete';
import EditIcon from '@mui/icons-material/Edit';
import AddIcon from '@mui/icons-material/Add';
import type { Channel } from '../types';

interface Props {
    open: boolean;
    onClose: () => void;
    channels: Channel[];
    onSave: (channel: Channel) => void;
    onDelete: (id: number) => void;
}

const ChannelManager: React.FC<Props> = ({ open, onClose, channels, onSave, onDelete }) => {
    const [editing, setEditing] = useState<Channel | null>(null);
    const [isFormOpen, setIsFormOpen] = useState(false);

    const handleEdit = (channel: Channel) => {
        setEditing(channel);
        setIsFormOpen(true);
    };

    const handleAdd = () => {
        setEditing({
            frequency: 0,
            alphaTag: '',
            description: '',
            license: '',
            type: 'RM',
            tone: '',
            mode: 'P25',
            tag: ''
        } as Channel);
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

    const handleChange = (field: keyof Channel, value: string | number) => {
        if (!editing) return;
        setEditing({ ...editing, [field]: value });
    };

    if (isFormOpen && editing) {
        return (
            <Dialog open={true} onClose={handleFormClose} maxWidth="sm" fullWidth>
                <form onSubmit={handleFormSave}>
                    <DialogTitle>{editing.id ? 'Edit Channel' : 'Add Channel'}</DialogTitle>
                    <DialogContent>
                        <Box display="flex" flexDirection="column" gap={2} mt={1}>
                            <TextField 
                                label="Name" 
                                value={editing.alphaTag} 
                                onChange={e => handleChange('alphaTag', e.target.value)} 
                                required autoFocus
                            />
                            <TextField 
                                label="Frequency (MHz)" 
                                type="number" 
                                inputProps={{ step: "0.0001" }}
                                value={editing.frequency} 
                                onChange={e => handleChange('frequency', parseFloat(e.target.value))} 
                                required 
                            />
                            <TextField 
                                label="Description" 
                                value={editing.description} 
                                onChange={e => handleChange('description', e.target.value)} 
                            />
                            <Box display="flex" gap={2}>
                                <FormControl sx={{ flex: 1 }}>
                                    <InputLabel>Mode</InputLabel>
                                    <Select
                                        value={editing.mode}
                                        label="Mode"
                                        onChange={e => handleChange('mode', e.target.value)}
                                    >
                                        <MenuItem value="P25">P25 (Digital)</MenuItem>
                                        <MenuItem value="FM">FM (EXPERIMENTAL)</MenuItem>
                                        <MenuItem value="NFM">NFM (EXPERIMENTAL)</MenuItem>
                                        <MenuItem value="WFM">WFM (EXPERIMENTAL)</MenuItem>
                                        <MenuItem value="AM">AM (EXPERIMENTAL)</MenuItem>
                                    </Select>
                                </FormControl>
                                <TextField 
                                    label="Tone" 
                                    value={editing.tone} 
                                    onChange={e => handleChange('tone', e.target.value)} 
                                    sx={{ flex: 1 }}
                                    placeholder="e.g. 100.0"
                                />
                            </Box>
                            <Box display="flex" gap={2}>
                                <TextField 
                                    label="License" 
                                    value={editing.license} 
                                    onChange={e => handleChange('license', e.target.value)} 
                                    sx={{ flex: 1 }}
                                />
                                <TextField 
                                    label="Tag" 
                                    value={editing.tag} 
                                    onChange={e => handleChange('tag', e.target.value)} 
                                    sx={{ flex: 1 }}
                                />
                            </Box>
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
                Manage Channels
                <Fab size="small" color="primary" onClick={handleAdd}>
                    <AddIcon />
                </Fab>
            </DialogTitle>
            <DialogContent dividers>
                <List>
                    {channels.map((ch) => (
                        <ListItem key={ch.id || ch.frequency} divider>
                            <ListItemText 
                                primary={
                                    <Box display="flex" alignItems="center" gap={2}>
                                        <Typography variant="subtitle1" fontWeight="bold">{ch.alphaTag}</Typography>
                                        <Typography variant="body2" color="primary" fontFamily="monospace">{ch.frequency.toFixed(4)}</Typography>
                                    </Box>
                                }
                                secondary={ch.description}
                            />
                            <Box>
                                <IconButton onClick={() => handleEdit(ch)} color="info">
                                    <EditIcon />
                                </IconButton>
                                <IconButton onClick={() => ch.id && onDelete(ch.id)} color="error">
                                    <DeleteIcon />
                                </IconButton>
                            </Box>
                        </ListItem>
                    ))}
                    {channels.length === 0 && (
                        <Typography variant="body2" color="textSecondary" align="center" py={4}>
                            No channels found.
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

export default ChannelManager;
