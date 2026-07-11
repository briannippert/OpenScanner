import React, { useState, useEffect } from 'react';
import {
    Button, Switch,
    Box, CircularProgress, Alert, AlertTitle, Link, TextField,
    Typography, LinearProgress,
} from '@mui/material';
import SettingsIcon from '@mui/icons-material/Settings';
import SystemUpdateIcon from '@mui/icons-material/SystemUpdate';
import DeleteForeverIcon from '@mui/icons-material/DeleteForever';
import FormDialog from './common/FormDialog';
import { apiFetch } from './common/apiBase';

interface StorageInfo {
    recordingsBytes: number;
    recordingsCount: number;
    databaseBytes: number;
    diskFreeBytes: number;
    diskTotalBytes: number;
}

interface Props {
    open: boolean;
    onClose: () => void;
    onRecordingsDeleted?: () => void;
}

const formatBytes = (bytes: number): string => {
    if (!bytes || bytes < 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
    return `${(bytes / Math.pow(1024, i)).toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
};

// A titled, bordered settings section (card).
const Section: React.FC<{ title: string; children: React.ReactNode }> = ({ title, children }) => (
    <Box sx={{ border: '1px solid', borderColor: 'surface.border', borderRadius: 2, p: 2, mb: 2, bgcolor: 'surface.base' }}>
        <Typography variant="subtitle2" sx={{ fontWeight: 700, letterSpacing: 0.5, textTransform: 'uppercase', color: 'text.secondary', mb: 1.5 }}>
            {title}
        </Typography>
        {children}
    </Box>
);

// One settings row: label + description on the left, control on the right.
const Row: React.FC<{ label: string; description?: string; control: React.ReactNode }> = ({ label, description, control }) => (
    <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 2, py: 0.75 }}>
        <Box sx={{ minWidth: 0 }}>
            <Typography variant="body2" fontWeight={600}>{label}</Typography>
            {description && <Typography variant="caption" color="text.secondary">{description}</Typography>}
        </Box>
        <Box sx={{ flexShrink: 0 }}>{control}</Box>
    </Box>
);

const SettingsManager: React.FC<Props> = ({ open, onClose, onRecordingsDeleted }) => {
    const [settings, setSettings] = useState<Record<string, string>>({});
    const [systemInfo, setSystemInfo] = useState<Record<string, string>>({});
    const [updateInfo, setUpdateInfo] = useState<{ latestVersion: string, url: string, body?: string } | null>(null);
    const [loading, setLoading] = useState(false);
    const [storage, setStorage] = useState<StorageInfo | null>(null);
    const [confirmDelete, setConfirmDelete] = useState(false);
    const [deleting, setDeleting] = useState(false);

    useEffect(() => {
        if (open) {
            fetchSettings();
            fetchSystemInfo();
            fetchLatestVersion();
            fetchStorage();
        }
    }, [open]);

    const fetchStorage = async () => {
        try {
            const res = await apiFetch('/api/system/storage');
            if (res.ok) setStorage(await res.json());
        } catch (error) {
            console.error("Failed to fetch storage info", error);
        }
    };

    const handleDeleteAllRecordings = async () => {
        setDeleting(true);
        try {
            const res = await apiFetch('/api/history', { method: 'DELETE' });
            if (res.ok) {
                setConfirmDelete(false);
                onRecordingsDeleted?.();
                await fetchStorage();
            }
        } catch (error) {
            console.error("Failed to delete recordings", error);
        } finally {
            setDeleting(false);
        }
    };

    const fetchSettings = async () => {
        setLoading(true);
        try {
            const res = await apiFetch('/api/settings');
            if (res.ok) {
                const data = await res.json();
                setSettings(data);
            }
        } catch (error) {
            console.error("Failed to fetch settings", error);
        } finally {
            setLoading(false);
        }
    };

    const fetchSystemInfo = async () => {
        try {
            const res = await apiFetch('/api/system/info');
            if (res.ok) {
                const data = await res.json();
                setSystemInfo(data);
            }
        } catch (error) {
            console.error("Failed to fetch system info", error);
        }
    };

    const fetchLatestVersion = async () => {
        try {
            const res = await fetch('https://api.github.com/repos/briannippert/OpenScanner/releases/latest');
            if (res.ok) {
                const data = await res.json();
                setUpdateInfo({
                    latestVersion: data.tag_name,
                    url: data.html_url,
                    body: data.body
                });
            }
        } catch (error) {
            console.error("Failed to fetch latest version from GitHub", error);
        }
    };

    const isNewer = (current: string, latest: string) => {
        if (!current || !latest) return false;
        const c = current.split('+')[0].replace(/^v/, '').split('.').map(Number);
        const l = latest.replace(/^v/, '').split('.').map(Number);
        for (let i = 0; i < Math.max(c.length, l.length); i++) {
            const cv = c[i] || 0;
            const lv = l[i] || 0;
            if (lv > cv) return true;
            if (cv > lv) return false;
        }
        return false;
    };

    const updateAvailable = updateInfo && isNewer(systemInfo.Version, updateInfo.latestVersion);

    const handleToggle = async (key: string, currentValue: string) => {
        const newValue = currentValue === 'true' ? 'false' : 'true';
        
        // Optimistic update
        setSettings(prev => ({ ...prev, [key]: newValue }));

        try {
            await apiFetch(`/api/settings/${key}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(newValue) // Send plain string as JSON string
            });
        } catch (error) {
            console.error("Failed to update setting", error);
            // Revert
            setSettings(prev => ({ ...prev, [key]: currentValue }));
        }
    };

    const handleValueChange = async (key: string, newValue: string) => {
        setSettings(prev => ({ ...prev, [key]: newValue }));

        try {
            await apiFetch(`/api/settings/${key}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(newValue)
            });
        } catch (error) {
            console.error("Failed to update setting", error);
        }
    };

    // Define known settings with friendly names
    const knownSettings: { key: string; label: string; description: string }[] = [
        { 
            key: 'EnableTranscription', 
            label: 'AI Transcription', 
            description: 'Enable Whisper AI to transcribe audio. High CPU usage on Pi.' 
        }
    ];

    return (
        <FormDialog
            open={open}
            onClose={onClose}
            title="System Settings"
            icon={<SettingsIcon />}
            actions={<Button onClick={onClose} color="inherit">Close</Button>}
        >
                {updateAvailable && (
                    <Alert
                        severity="success"
                        icon={<SystemUpdateIcon />}
                        sx={{ mb: 2 }}
                        action={
                            <Button 
                                color="inherit" 
                                size="small" 
                                component={Link} 
                                href={updateInfo?.url} 
                                target="_blank"
                                rel="noopener"
                            >
                                VIEW
                            </Button>
                        }
                    >
                        <AlertTitle>Update Available: {updateInfo?.latestVersion}</AlertTitle>
                        A newer version of OpenScanner is available on GitHub.
                    </Alert>
                )}
                {loading ? (
                    <Box display="flex" justifyContent="center" p={4}>
                        <CircularProgress />
                    </Box>
                ) : (
                    <Box>
                        <Section title="Transcription">
                            {knownSettings.map((setting) => (
                                <Row
                                    key={setting.key}
                                    label={setting.label}
                                    description={setting.description}
                                    control={
                                        <Switch
                                            checked={settings[setting.key] === 'true'}
                                            onChange={() => handleToggle(setting.key, settings[setting.key] || 'false')}
                                        />
                                    }
                                />
                            ))}
                            {settings['EnableTranscription'] === 'true' && (
                                <Row
                                    label="Transcription Threads"
                                    description={`Number of recordings transcribed at once. With a large model, 1 is recommended (each run already uses all cores). CPU cores: ${systemInfo.CpuCores || 'Unknown'}`}
                                    control={
                                        <TextField
                                            type="number"
                                            size="small"
                                            variant="outlined"
                                            style={{ width: '80px', minWidth: '80px' }}
                                            value={settings['TranscriptionThreads'] || ''}
                                            inputProps={{ min: 1, max: 32 }}
                                            onChange={(e) => handleValueChange('TranscriptionThreads', e.target.value)}
                                        />
                                    }
                                />
                            )}
                        </Section>

                        <Section title="Storage">
                            {storage ? (
                                <Box>
                                    {storage.diskTotalBytes > 0 && (
                                        <Box sx={{ mb: 1.5 }}>
                                            <Box display="flex" justifyContent="space-between">
                                                <Typography variant="caption" color="text.secondary">Disk used</Typography>
                                                <Typography variant="caption" color="text.secondary">
                                                    {formatBytes(storage.diskTotalBytes - storage.diskFreeBytes)} / {formatBytes(storage.diskTotalBytes)}
                                                    {' '}({formatBytes(storage.diskFreeBytes)} free)
                                                </Typography>
                                            </Box>
                                            <LinearProgress
                                                variant="determinate"
                                                value={Math.min(100, ((storage.diskTotalBytes - storage.diskFreeBytes) / storage.diskTotalBytes) * 100)}
                                                sx={{ mt: 0.5, height: 6, borderRadius: 3 }}
                                            />
                                        </Box>
                                    )}
                                    <Box display="flex" justifyContent="space-between" sx={{ mb: 0.5 }}>
                                        <Typography variant="body2" color="text.secondary">Recordings ({storage.recordingsCount})</Typography>
                                        <Typography variant="body2">{formatBytes(storage.recordingsBytes)}</Typography>
                                    </Box>
                                    <Box display="flex" justifyContent="space-between">
                                        <Typography variant="body2" color="text.secondary">Database</Typography>
                                        <Typography variant="body2">{formatBytes(storage.databaseBytes)}</Typography>
                                    </Box>
                                    <Button
                                        variant="outlined"
                                        color="error"
                                        size="small"
                                        fullWidth
                                        startIcon={<DeleteForeverIcon />}
                                        onClick={() => setConfirmDelete(true)}
                                        disabled={storage.recordingsCount === 0}
                                        sx={{ mt: 1.5 }}
                                    >
                                        Delete All Recordings
                                    </Button>
                                </Box>
                            ) : (
                                <Typography variant="caption" color="text.secondary">Loading…</Typography>
                            )}
                        </Section>

                        {(systemInfo.Version || systemInfo.Commit) && (
                            <Section title="About">
                                {systemInfo.Version && <Row label="Software Version" control={<Typography variant="body2" color="text.secondary">{systemInfo.Version}</Typography>} />}
                                {systemInfo.Commit && (
                                    <Row
                                        label="Git Commit"
                                        control={<Typography variant="body2" color="text.secondary" sx={{ fontFamily: 'monospace', fontSize: '0.75rem' }}>{systemInfo.Commit}</Typography>}
                                    />
                                )}
                            </Section>
                        )}
                    </Box>
                )}

            <FormDialog
                open={confirmDelete}
                onClose={() => !deleting && setConfirmDelete(false)}
                title="Delete All Recordings?"
                icon={<DeleteForeverIcon />}
                maxWidth="xs"
                disableClose={deleting}
                actions={
                    <>
                        <Button onClick={() => setConfirmDelete(false)} disabled={deleting} color="inherit">Cancel</Button>
                        <Button
                            color="error"
                            variant="contained"
                            onClick={handleDeleteAllRecordings}
                            disabled={deleting}
                            startIcon={deleting ? <CircularProgress size={16} color="inherit" /> : <DeleteForeverIcon />}
                        >
                            {deleting ? 'Deleting…' : 'Delete All'}
                        </Button>
                    </>
                }
            >
                <Typography variant="body2">
                    This permanently deletes all recorded audio files and their transmission
                    history{storage ? ` (${storage.recordingsCount} recording${storage.recordingsCount === 1 ? '' : 's'}, ${formatBytes(storage.recordingsBytes)})` : ''}.
                    This cannot be undone.
                </Typography>
            </FormDialog>
        </FormDialog>
    );
};

export default SettingsManager;
