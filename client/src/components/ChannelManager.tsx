import React, { memo, useEffect, useMemo, useState } from 'react';
import {
    Button, List, ListItem, ListItemText, IconButton,
    TextField, Box, Typography, MenuItem, Select, FormControl, InputLabel,
    Switch, FormControlLabel, FormGroup,
} from '@mui/material';
import DeleteIcon from '@mui/icons-material/Delete';
import EditIcon from '@mui/icons-material/Edit';
import AddIcon from '@mui/icons-material/Add';
import RadioIcon from '@mui/icons-material/Radio';
import SettingsInputAntennaIcon from '@mui/icons-material/SettingsInputAntenna';
import type { Channel, RadiosResponse } from '../types';
import { useRadios } from '../hooks/useRadios';
import StatusChip from './common/StatusChip';
import FormDialog from './common/FormDialog';
import ConfirmDialog from './common/ConfirmDialog';
import EmptyState from './common/EmptyState';

interface Props {
    open: boolean;
    onClose: () => void;
    channels: Channel[];
    onSave: (channel: Channel) => void;
    onDelete: (id: number) => void;
}

const FORM_ID = 'channel-form';
const MONO = '"Roboto Mono", ui-monospace, SFMono-Regular, Menlo, monospace';
const AUTO = '__auto__';

/** Where the plan puts each channel: SDR label and role, and whether it was auto-placed or left out. */
const placementByChannel = (radios: RadiosResponse | null) => {
    const map = new Map<number, { label: string; role: string; autoPlaced: boolean; unscanned: boolean }>();
    if (!radios) return map;
    const auto = new Set(radios.plan.autoPlacedChannelIds);
    for (const d of radios.plan.devices)
        for (const id of d.channelIds)
            map.set(id, { label: d.label, role: d.role, autoPlaced: auto.has(id), unscanned: false });
    for (const id of radios.plan.unscannedChannelIds)
        map.set(id, { label: '', role: 'Idle', autoPlaced: false, unscanned: true });
    return map;
};

/**
 * Which SDR carries a channel. In Manual mode it is a picker that pins the channel (Auto
 * leaves it to the planner); in Auto mode it just shows where the planner put it.
 */
const SdrPlacement: React.FC<{
    channel: Channel;
    radios: RadiosResponse;
    pin: string | null;
    placement?: { label: string; role: string; autoPlaced: boolean; unscanned: boolean };
    onPin: (deviceId: string | null) => void;
}> = ({ channel, radios, pin, placement, onPin }) => {
    const role = placement?.role === 'FastScan' ? 'fast' : placement?.role === 'Hop' ? 'hop' : null;
    const marker = placement?.unscanned
        ? <StatusChip label="NOT SCANNED" tone="error" sx={{ fontSize: 10 }} />
        : placement?.autoPlaced && radios.mode === 'manual'
            ? <StatusChip label={`AUTO → ${placement.label}`} tone="info" sx={{ fontSize: 10 }} />
            : null;

    if (radios.mode === 'auto') {
        if (channel.avoid || !placement) return null;
        return placement.unscanned
            ? marker
            : <StatusChip label={`${placement.label}${role ? ` · ${role}` : ''}`} tone="muted" sx={{ fontSize: 10 }} />;
    }

    return (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            {marker}
            <Select
                size="small"
                value={pin ?? AUTO}
                onChange={e => onPin(e.target.value === AUTO ? null : String(e.target.value))}
                inputProps={{ 'aria-label': `SDR for ${channel.alphaTag}` }}
                sx={{ minWidth: 120 }}
            >
                <MenuItem value={AUTO}>Auto</MenuItem>
                {radios.devices.map(d => (
                    <MenuItem key={d.id} value={d.id} disabled={!d.attached || !d.enabled}>
                        {d.label}{!d.attached ? ' (unplugged)' : !d.enabled ? ' (disabled)' : ''}
                    </MenuItem>
                ))}
                {/* A pin to a dongle that has been forgotten still shows, rather than reading as Auto. */}
                {pin && !radios.devices.some(d => d.id === pin) && <MenuItem value={pin} disabled>{pin}</MenuItem>}
            </Select>
        </Box>
    );
};

const ChannelManager: React.FC<Props> = ({ open, onClose, channels, onSave, onDelete }) => {
    const [editing, setEditing] = useState<Channel | null>(null);
    const [isFormOpen, setIsFormOpen] = useState(false);
    // Kept after the confirm closes so its text does not blank out during the fade.
    const [deleteTarget, setDeleteTarget] = useState<Channel | null>(null);
    const [confirmOpen, setConfirmOpen] = useState(false);

    // SDR placement is only worth showing once there is more than one dongle to choose from.
    const { radios, assign, refresh: refreshRadios, error: sdrError } = useRadios(open);
    // An edit, delete or Avoid toggle re-plans on the server; re-read once the list updates.
    useEffect(() => {
        if (!open) return;
        const id = setTimeout(() => void refreshRadios(), 400);
        return () => clearTimeout(id);
    }, [open, channels, refreshRadios]);
    const showSdrs = !!radios && radios.devices.length > 1;
    const placement = useMemo(() => placementByChannel(radios), [radios]);
    // Pins set here, until the channel list passed in catches up with the server.
    const [pins, setPins] = useState<Record<number, string | null>>({});
    const pinOf = (ch: Channel) => (ch.id != null && ch.id in pins ? pins[ch.id] : ch.sdrDeviceId ?? null);
    const handlePin = async (ch: Channel, deviceId: string | null) => {
        if (ch.id == null) return;
        if (await assign([ch.id], deviceId)) setPins(p => ({ ...p, [ch.id!]: deviceId }));
    };

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
            tag: '',
            avoid: false,
            dmrSlot: undefined,
            dmrColorCode: undefined,
            dmrTalkgroup: undefined,
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

    const handleChange = (field: keyof Channel, value: string | number | boolean) => {
        if (!editing) return;
        const coerced = value === '' ? undefined : value;
        setEditing({ ...editing, [field]: coerced });
    };

    if (isFormOpen && editing) {
        return (
            <FormDialog
                open
                onClose={handleFormClose}
                title={editing.id ? 'Edit Channel' : 'Add Channel'}
                icon={<SettingsInputAntennaIcon />}
                actions={
                    <>
                        <Button onClick={handleFormClose} color="inherit">Cancel</Button>
                        <Button type="submit" form={FORM_ID} variant="contained" color="primary">Save</Button>
                    </>
                }
            >
                <Box component="form" id={FORM_ID} onSubmit={handleFormSave} display="flex" flexDirection="column" gap={2} mt={1}>
                    <TextField label="Name" value={editing.alphaTag} onChange={e => handleChange('alphaTag', e.target.value)} required autoFocus />
                    <TextField
                        label="Frequency (MHz)"
                        type="number"
                        inputProps={{ step: '0.0001' }}
                        value={editing.frequency}
                        onChange={e => handleChange('frequency', parseFloat(e.target.value))}
                        required
                    />
                    <TextField label="Description" value={editing.description} onChange={e => handleChange('description', e.target.value)} />
                    <Box display="flex" gap={2}>
                        <FormControl sx={{ flex: 1 }}>
                            <InputLabel>Mode</InputLabel>
                            <Select value={editing.mode} label="Mode" onChange={e => handleChange('mode', e.target.value)}>
                                <MenuItem value="P25">P25 (Digital)</MenuItem>
                                <MenuItem value="DMR">DMR (Digital)</MenuItem>
                                <MenuItem value="NFM">NFM (Analog)</MenuItem>
                            </Select>
                        </FormControl>
                        <TextField label="Tone" value={editing.tone} onChange={e => handleChange('tone', e.target.value)} sx={{ flex: 1 }} placeholder="e.g. 100.0" />
                    </Box>
                    {editing.mode === 'DMR' && (
                        <Box display="flex" gap={2}>
                            <FormControl sx={{ flex: 1 }}>
                                <InputLabel>Slot</InputLabel>
                                <Select
                                    value={editing.dmrSlot?.toString() ?? ''}
                                    label="Slot"
                                    onChange={e => handleChange('dmrSlot', e.target.value === '' ? '' : Number(e.target.value))}
                                >
                                    <MenuItem value="">Any</MenuItem>
                                    <MenuItem value="1">Slot 1</MenuItem>
                                    <MenuItem value="2">Slot 2</MenuItem>
                                </Select>
                            </FormControl>
                            <TextField
                                label="Color Code (0–15)"
                                type="number"
                                inputProps={{ min: 0, max: 15 }}
                                value={editing.dmrColorCode ?? ''}
                                onChange={e => handleChange('dmrColorCode', e.target.value === '' ? '' : Number(e.target.value))}
                                sx={{ flex: 1 }}
                                placeholder="0–15"
                            />
                            <TextField
                                label="Talkgroup ID"
                                type="number"
                                inputProps={{ min: 0 }}
                                value={editing.dmrTalkgroup ?? ''}
                                onChange={e => handleChange('dmrTalkgroup', e.target.value === '' ? '' : Number(e.target.value))}
                                sx={{ flex: 1 }}
                                placeholder="e.g. 1234"
                            />
                        </Box>
                    )}
                    <Box display="flex" gap={2}>
                        <TextField label="License" value={editing.license} onChange={e => handleChange('license', e.target.value)} sx={{ flex: 1 }} />
                        <TextField label="Tag" value={editing.tag} onChange={e => handleChange('tag', e.target.value)} sx={{ flex: 1 }} />
                    </Box>
                    <FormGroup>
                        <FormControlLabel
                            control={<Switch checked={editing.avoid} onChange={e => handleChange('avoid', e.target.checked)} />}
                            label="Avoid Channel"
                        />
                    </FormGroup>
                </Box>
            </FormDialog>
        );
    }

    return (
        <FormDialog
            open={open}
            onClose={onClose}
            title="Manage Channels"
            icon={<RadioIcon />}
            maxWidth="md"
            actions={
                <>
                    <Button startIcon={<AddIcon />} variant="contained" color="primary" onClick={handleAdd}>Add Channel</Button>
                    <Box flexGrow={1} />
                    <Button onClick={onClose} color="inherit">Close</Button>
                </>
            }
        >
            {showSdrs && sdrError && <Typography color="error" variant="body2" sx={{ mb: 1 }}>{sdrError}</Typography>}
            {channels.length === 0 ? (
                <EmptyState icon={<RadioIcon />} title="No channels yet" hint="Add a frequency to start scanning." />
            ) : (
                <List>
                    {channels.map((ch) => (
                        <ListItem key={ch.id || ch.frequency} divider>
                            <ListItemText
                                primary={
                                    <Box display="flex" alignItems="center" gap={2}>
                                        <Typography variant="subtitle1" fontWeight="bold">{ch.alphaTag}</Typography>
                                        <Typography variant="body2" color="primary" sx={{ fontFamily: MONO }}>{ch.frequency.toFixed(4)}</Typography>
                                    </Box>
                                }
                                secondary={ch.description}
                            />
                            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                                {showSdrs && radios && (
                                    <SdrPlacement
                                        channel={ch}
                                        radios={radios}
                                        pin={pinOf(ch)}
                                        placement={ch.id != null ? placement.get(ch.id) : undefined}
                                        onPin={deviceId => void handlePin(ch, deviceId)}
                                    />
                                )}
                                <FormControlLabel
                                    control={<Switch checked={ch.avoid} onChange={() => onSave({ ...ch, avoid: !ch.avoid })} name="avoid" color="warning" />}
                                    label="Avoid"
                                />
                                <IconButton onClick={() => handleEdit(ch)} color="info" aria-label={`Edit ${ch.alphaTag}`}>
                                    <EditIcon />
                                </IconButton>
                                <IconButton onClick={() => { setDeleteTarget(ch); setConfirmOpen(true); }} color="error" aria-label={`Delete ${ch.alphaTag}`}>
                                    <DeleteIcon />
                                </IconButton>
                            </Box>
                        </ListItem>
                    ))}
                </List>
            )}
            <ConfirmDialog
                open={confirmOpen}
                title="Delete channel?"
                onConfirm={() => { setConfirmOpen(false); if (deleteTarget?.id) onDelete(deleteTarget.id); }}
                onCancel={() => setConfirmOpen(false)}
            >
                This removes <strong>{deleteTarget?.alphaTag || `${deleteTarget?.frequency} MHz`}</strong> from
                the scan list. Recordings already made on it are kept.
            </ConfirmDialog>
        </FormDialog>
    );
};

// Memoized: the app re-renders on every scanner state update, which rarely changes these props.
export default memo(ChannelManager);
