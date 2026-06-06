import React, { useState, useEffect } from 'react';
import { 
    Dialog, DialogTitle, DialogContent, DialogActions, 
    Button, List, ListItem, ListItemText, ListItemSecondaryAction, Switch,
    Box, CircularProgress, Alert, AlertTitle, Link, TextField
} from '@mui/material';
import SystemUpdateIcon from '@mui/icons-material/SystemUpdate';

interface Props {
    open: boolean;
    onClose: () => void;
}

const SettingsManager: React.FC<Props> = ({ open, onClose }) => {
    const [settings, setSettings] = useState<Record<string, string>>({});
    const [systemInfo, setSystemInfo] = useState<Record<string, string>>({});
    const [updateInfo, setUpdateInfo] = useState<{ latestVersion: string, url: string, body?: string } | null>(null);
    const [loading, setLoading] = useState(false);

    useEffect(() => {
        if (open) {
            fetchSettings();
            fetchSystemInfo();
            fetchLatestVersion();
        }
    }, [open]);

    const fetchSettings = async () => {
        setLoading(true);
        try {
            const isDev = window.location.port === '5173';
            const port = isDev ? '5212' : window.location.port || '80';
            const protocol = window.location.protocol;
            const backendHost = window.location.hostname;
            const portSuffix = (port === '80' || port === '') ? '' : `:${port}`;
            
            const res = await fetch(`${protocol}//${backendHost}${portSuffix}/api/settings`);
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
            const isDev = window.location.port === '5173';
            const port = isDev ? '5212' : window.location.port || '80';
            const protocol = window.location.protocol;
            const backendHost = window.location.hostname;
            const portSuffix = (port === '80' || port === '') ? '' : `:${port}`;
            
            const res = await fetch(`${protocol}//${backendHost}${portSuffix}/api/system/info`);
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
            const isDev = window.location.port === '5173';
            const port = isDev ? '5212' : window.location.port || '80';
            const protocol = window.location.protocol;
            const backendHost = window.location.hostname;
            const portSuffix = (port === '80' || port === '') ? '' : `:${port}`;

            await fetch(`${protocol}//${backendHost}${portSuffix}/api/settings/${key}`, {
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
            const isDev = window.location.port === '5173';
            const port = isDev ? '5212' : window.location.port || '80';
            const protocol = window.location.protocol;
            const backendHost = window.location.hostname;
            const portSuffix = (port === '80' || port === '') ? '' : `:${port}`;

            await fetch(`${protocol}//${backendHost}${portSuffix}/api/settings/${key}`, {
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
        <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
            <DialogTitle>System Settings</DialogTitle>
            <DialogContent dividers>
                {updateAvailable && (
                    <Alert 
                        severity="success" 
                        icon={<SystemUpdateIcon />}
                        sx={{ mb: 2, border: '1px solid #4caf50' }}
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
                    <List>
                        {knownSettings.map((setting) => (
                            <ListItem key={setting.key}>
                                <ListItemText 
                                    primary={setting.label}
                                    secondary={setting.description}
                                />
                                <ListItemSecondaryAction>
                                    <Switch 
                                        edge="end" 
                                        checked={settings[setting.key] === 'true'}
                                        onChange={() => handleToggle(setting.key, settings[setting.key] || 'false')}
                                    />
                                </ListItemSecondaryAction>
                            </ListItem>
                        ))}
                        {settings['EnableTranscription'] === 'true' && (
                            <ListItem key="TranscriptionThreads" sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                                <ListItemText 
                                    primary="Transcription Threads"
                                    secondary={`Number of parallel threads for AI transcription. CPU cores: ${systemInfo.CpuCores || 'Unknown'}`}
                                    sx={{ mr: 2 }}
                                />
                                <TextField
                                    type="number"
                                    size="small"
                                    variant="outlined"
                                    style={{ width: '80px', minWidth: '80px' }}
                                    value={settings['TranscriptionThreads'] || ''}
                                    inputProps={{ min: 1, max: 32 }}
                                    onChange={(e) => handleValueChange('TranscriptionThreads', e.target.value)}
                                />
                            </ListItem>
                        )}
                        {systemInfo.Commit && (
                            <ListItem>
                                <ListItemText 
                                    primary="Git Commit"
                                    secondary={systemInfo.Commit}
                                    secondaryTypographyProps={{ style: { fontFamily: 'monospace', fontSize: '0.75rem' } }}
                                />
                            </ListItem>
                        )}
                        {systemInfo.Version && (
                            <ListItem>
                                <ListItemText 
                                    primary="Software Version"
                                    secondary={systemInfo.Version}
                                />
                            </ListItem>
                        )}
                    </List>
                )}
            </DialogContent>
            <DialogActions>
                <Button onClick={onClose}>Close</Button>
            </DialogActions>
        </Dialog>
    );
};

export default SettingsManager;
