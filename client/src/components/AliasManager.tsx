import React, { useEffect, useMemo, useRef, useState } from 'react';
import {
    Box, Typography, TextField, Select, MenuItem, List, ListItem, Divider, Chip,
    Button, FormControl, InputLabel, Alert,
} from '@mui/material';
import BadgeIcon from '@mui/icons-material/Badge';
import FileDownloadIcon from '@mui/icons-material/FileDownload';
import FileUploadIcon from '@mui/icons-material/FileUpload';
import type { RadioAlias, AliasCandidate } from '../types';
import FormDialog from './common/FormDialog';
import EmptyState from './common/EmptyState';

interface Props {
    open: boolean;
    onClose: () => void;
    candidates: AliasCandidate[];
    aliases: RadioAlias[];
    onSave: (alias: RadioAlias) => void;
    onDelete: (id: number) => void;
    /** Additive import (fills blanks, never overwrites); returns the count added. */
    onImport: (list: RadioAlias[]) => Promise<number>;
    /** Called when the dialog opens, to (re)load discovered candidates. */
    onOpened: () => void;
}

const isImportableAlias = (a: unknown): a is RadioAlias => {
    if (typeof a !== 'object' || a === null) return false;
    const o = a as Record<string, unknown>;
    return (o.kind === 'SRC' || o.kind === 'TG')
        && typeof o.value === 'number'
        && typeof o.name === 'string' && o.name.trim().length > 0
        && typeof o.alphaTag === 'string'
        && typeof o.frequency === 'number';
};

const MONO = '"Roboto Mono", ui-monospace, SFMono-Regular, Menlo, monospace';
const chKey = (alphaTag: string, frequency: number) => `${alphaTag}|${frequency.toFixed(4)}`;

const shortDate = (ts?: string): string => {
    if (!ts) return '';
    // SQLite UTC "yyyy-MM-dd HH:mm:ss" — mark as UTC then show locale date.
    const d = new Date(ts.includes('T') ? ts : ts.replace(' ', 'T') + 'Z');
    return isNaN(d.getTime()) ? '' : d.toLocaleDateString([], { month: 'short', day: 'numeric' });
};

const AliasManager: React.FC<Props> = ({ open, onClose, candidates, aliases, onSave, onDelete, onImport, onOpened }) => {
    const [selKey, setSelKey] = useState('');
    const [drafts, setDrafts] = useState<Record<string, string>>({});
    const [message, setMessage] = useState<string | null>(null);
    const fileInputRef = useRef<HTMLInputElement>(null);

    // Reload discovered candidates whenever the dialog opens (side effect only).
    useEffect(() => { if (open) onOpened(); }, [open, onOpened]);

    const handleExport = () => {
        const data = aliases.map(({ kind, value, name, alphaTag, frequency }) => ({ kind, value, name, alphaTag, frequency }));
        const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'openscanner-aliases.json';
        a.click();
        URL.revokeObjectURL(url);
    };

    const handleImportFile = async (e: React.ChangeEvent<HTMLInputElement>) => {
        const file = e.target.files?.[0];
        e.target.value = ''; // let the same file be re-selected later
        if (!file) return;
        try {
            const parsed: unknown = JSON.parse(await file.text());
            const list = ((Array.isArray(parsed) ? parsed : []) as unknown[]).filter(isImportableAlias);
            if (list.length === 0) { setMessage('No valid aliases found in that file.'); return; }
            const added = await onImport(list);
            setMessage(`Imported ${added} new alias${added === 1 ? '' : 'es'}; existing names were kept.`);
        } catch {
            setMessage('Could not read that file as JSON.');
        }
    };

    // Channels = union of channels that have candidates and/or existing aliases.
    const channels = useMemo(() => {
        const map = new Map<string, { alphaTag: string; frequency: number }>();
        for (const c of candidates) map.set(chKey(c.alphaTag, c.frequency), { alphaTag: c.alphaTag, frequency: c.frequency });
        for (const a of aliases) map.set(chKey(a.alphaTag, a.frequency), { alphaTag: a.alphaTag, frequency: a.frequency });
        return [...map.values()].sort((x, y) => x.alphaTag.localeCompare(y.alphaTag) || x.frequency - y.frequency);
    }, [candidates, aliases]);

    // Effective selection without setState-in-effect: fall back to the first channel.
    const effectiveKey = channels.some(c => chKey(c.alphaTag, c.frequency) === selKey)
        ? selKey
        : (channels[0] ? chKey(channels[0].alphaTag, channels[0].frequency) : '');
    const selected = channels.find(c => chKey(c.alphaTag, c.frequency) === effectiveKey);

    // Merge discovered candidates with existing aliases into rows for one kind.
    const rowsFor = (kind: 'SRC' | 'TG') => {
        if (!selected) return [] as { value: number; count?: number; lastSeen?: string; alias?: RadioAlias }[];
        const values = new Map<number, { count?: number; lastSeen?: string; alias?: RadioAlias }>();
        for (const c of candidates) {
            if (c.kind === kind && chKey(c.alphaTag, c.frequency) === effectiveKey)
                values.set(c.value, { count: c.count, lastSeen: c.lastSeen });
        }
        for (const a of aliases) {
            if (a.kind === kind && chKey(a.alphaTag, a.frequency) === effectiveKey)
                values.set(a.value, { ...(values.get(a.value) || {}), alias: a });
        }
        return [...values.entries()]
            .map(([value, info]) => ({ value, ...info }))
            .sort((x, y) => (y.count ?? 0) - (x.count ?? 0) || x.value - y.value);
    };

    const commit = (kind: 'SRC' | 'TG', value: number, alias?: RadioAlias) => {
        if (!selected) return;
        const key = `${effectiveKey}|${kind}|${value}`;
        const name = (drafts[key] ?? alias?.name ?? '').trim();
        if (name) onSave({ id: alias?.id, kind, value, name, alphaTag: selected.alphaTag, frequency: selected.frequency });
        else if (alias?.id) onDelete(alias.id);
    };

    const renderList = (kind: 'SRC' | 'TG') => {
        const rows = rowsFor(kind);
        if (rows.length === 0) {
            return <Typography variant="caption" color="text.secondary" sx={{ px: 1 }}>None seen in the last 7 days.</Typography>;
        }
        return (
            <List dense disablePadding>
                {rows.map(({ value, count, lastSeen, alias }) => {
                    const key = `${effectiveKey}|${kind}|${value}`;
                    return (
                        <ListItem key={key} divider sx={{ gap: 1 }}>
                            <Box sx={{ minWidth: 96 }}>
                                <Typography sx={{ fontFamily: MONO, fontWeight: 700 }}>{value}</Typography>
                                <Typography variant="caption" color="text.secondary">
                                    {count != null ? `${count}×` : 'aliased'}{lastSeen ? ` · ${shortDate(lastSeen)}` : ''}
                                </Typography>
                            </Box>
                            <TextField
                                size="small"
                                placeholder="Display name"
                                fullWidth
                                value={drafts[key] ?? alias?.name ?? ''}
                                onChange={e => setDrafts(d => ({ ...d, [key]: e.target.value }))}
                                onBlur={() => commit(kind, value, alias)}
                                onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
                            />
                        </ListItem>
                    );
                })}
            </List>
        );
    };

    return (
        <FormDialog
            open={open}
            onClose={onClose}
            title="Radio Aliases"
            icon={<BadgeIcon />}
            maxWidth="md"
            actions={
                <>
                    <Button startIcon={<FileDownloadIcon />} onClick={handleExport} disabled={aliases.length === 0}>Export</Button>
                    <Button startIcon={<FileUploadIcon />} onClick={() => fileInputRef.current?.click()}>Import</Button>
                    <Box flexGrow={1} />
                    <Button onClick={onClose} color="inherit">Close</Button>
                </>
            }
        >
            <input
                ref={fileInputRef}
                type="file"
                accept="application/json,.json"
                hidden
                onChange={handleImportFile}
            />
            {message && (
                <Alert severity="info" onClose={() => setMessage(null)} sx={{ mb: 2 }}>{message}</Alert>
            )}
            {channels.length === 0 ? (
                <EmptyState icon={<BadgeIcon />} title="No radio IDs seen yet" hint="SRC and TG values from the last 7 days will appear here to name." />
            ) : (
                <Box display="flex" flexDirection="column" gap={2}>
                    <FormControl size="small" fullWidth>
                        <InputLabel id="alias-channel-label">Channel</InputLabel>
                        <Select
                            labelId="alias-channel-label"
                            label="Channel"
                            value={effectiveKey}
                            onChange={e => setSelKey(e.target.value)}
                        >
                            {channels.map(c => (
                                <MenuItem key={chKey(c.alphaTag, c.frequency)} value={chKey(c.alphaTag, c.frequency)}>
                                    {c.alphaTag || '(unnamed)'} — {c.frequency.toFixed(4)} MHz
                                </MenuItem>
                            ))}
                        </Select>
                    </FormControl>

                    <Box>
                        <Divider textAlign="left" sx={{ mb: 1 }}>
                            <Chip label="Source IDs (SRC)" size="small" color="warning" variant="outlined" />
                        </Divider>
                        {renderList('SRC')}
                    </Box>

                    <Box>
                        <Divider textAlign="left" sx={{ mb: 1 }}>
                            <Chip label="Talkgroups (TG)" size="small" color="info" variant="outlined" />
                        </Divider>
                        {renderList('TG')}
                    </Box>

                    <Typography variant="caption" color="text.secondary">
                        Names are saved per channel and appear in the log and live display. Clear a name to remove it.
                    </Typography>
                </Box>
            )}
        </FormDialog>
    );
};

export default AliasManager;
