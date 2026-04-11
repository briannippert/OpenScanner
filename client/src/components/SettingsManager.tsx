import React, { useState, useEffect, useCallback } from 'react';
import { 
    Dialog, DialogTitle, DialogContent, DialogActions, 
    Button, List, ListItem, ListItemText, ListItemSecondaryAction, Switch,
    Box, CircularProgress, Alert, AlertTitle, Link,
    TextField, ToggleButton, ToggleButtonGroup, Typography, Chip,
    Divider, Paper
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
    const [connectionStatus, setConnectionStatus] = useState<'idle' | 'testing' | 'ok' | 'error'>('idle');
    const [remoteServerInfo, setRemoteServerInfo] = useState<{
        model?: string;
        binaryFound?: boolean;
        modelFound?: boolean;
        acceleration?: string;
        cpu?: string;
        gpu?: string;
        gpuMemoryMb?: number;
        diarizationAvailable?: boolean;
    } | null>(null);
    const [connectionError, setConnectionError] = useState<string>('');

    const getBackendUrl = () => {
        const isDev = window.location.port === '5173';
        const port = isDev ? '5212' : window.location.port || '80';
        const protocol = window.location.protocol;
        const backendHost = window.location.hostname;
        const portSuffix = (port === '80' || port === '') ? '' : `:${port}`;
        return `${protocol}//${backendHost}${portSuffix}`;
    };

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
            const res = await fetch(`${getBackendUrl()}/api/settings`);
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
            const res = await fetch(`${getBackendUrl()}/api/system/info`);
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
            await fetch(`${getBackendUrl()}/api/settings/${key}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(newValue)
            });
        } catch (error) {
            console.error("Failed to update setting", error);
            setSettings(prev => ({ ...prev, [key]: currentValue }));
        }
    };

    const updateSetting = async (key: string, value: string) => {
        setSettings(prev => ({ ...prev, [key]: value }));
        try {
            await fetch(`${getBackendUrl()}/api/settings/${key}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(value)
            });
        } catch (error) {
            console.error("Failed to update setting", error);
        }
    };

    const testRemoteConnection = useCallback(async (url?: string) => {
        const serverUrl = url ?? settings['TranscriptionServerUrl'];
        if (!serverUrl) return;

        setConnectionStatus('testing');
        setRemoteServerInfo(null);
        setConnectionError('');
        try {
            const res = await fetch(`${serverUrl.replace(/\/$/, '')}/health`, { signal: AbortSignal.timeout(5000) });
            if (res.ok) {
                const data = await res.json();
                setConnectionStatus(data.status === 'ok' ? 'ok' : 'error');
                setRemoteServerInfo({
                    model: data.model,
                    binaryFound: data.binaryFound,
                    modelFound: data.modelFound,
                    acceleration: data.acceleration,
                    cpu: data.cpu,
                    gpu: data.gpu,
                    gpuMemoryMb: data.gpuMemoryMb,
                    diarizationAvailable: data.diarizationAvailable,
                });
                if (data.status !== 'ok') {
                    setConnectionError('Server is reachable but reports an error. Check whisper.cpp installation on the remote machine.');
                }
            } else {
                setConnectionStatus('error');
                setConnectionError(`Server returned HTTP ${res.status}`);
            }
        } catch (err) {
            setConnectionStatus('error');
            setConnectionError(err instanceof TypeError ? 'Could not connect. Check the URL and ensure the server is running.' : String(err));
        }
    }, [settings]);

    // Define known settings with friendly names
    const knownSettings: { key: string; label: string; description: string }[] = [
        { 
            key: 'EnableTranscription', 
            label: 'AI Transcription', 
            description: 'Enable Whisper AI speech-to-text for recorded transmissions.' 
        }
    ];

    const isRemote = settings['TranscriptionMode'] === 'remote';
    const transcriptionEnabled = settings['EnableTranscription'] === 'true';

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

                        {transcriptionEnabled && (
                            <ListItem sx={{ flexDirection: 'column', alignItems: 'flex-start', gap: 1.5, py: 2 }}>
                                <Typography variant="body2" fontWeight="bold">Transcription Mode</Typography>
                                <ToggleButtonGroup
                                    value={settings['TranscriptionMode'] || 'local'}
                                    exclusive
                                    size="small"
                                    onChange={(_e, val) => {
                                        if (val) {
                                            updateSetting('TranscriptionMode', val);
                                            setConnectionStatus('idle');
                                            setRemoteServerInfo(null);
                                            setConnectionError('');
                                        }
                                    }}
                                >
                                    <ToggleButton value="local">Local (whisper.cpp)</ToggleButton>
                                    <ToggleButton value="remote">Remote Server</ToggleButton>
                                </ToggleButtonGroup>

                                {isRemote && (
                                    <>
                                    <Paper variant="outlined" sx={{ width: '100%', p: 2 }}>
                                        <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
                                            Connect to an external machine running the OpenScanner WhisperServer
                                            to offload transcription from this device.
                                        </Typography>

                                        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, width: '100%' }}>
                                            <TextField
                                                size="small"
                                                fullWidth
                                                label="Server URL"
                                                placeholder="http://192.168.1.100:8090"
                                                value={settings['TranscriptionServerUrl'] || ''}
                                                onChange={(e) => setSettings(prev => ({ ...prev, TranscriptionServerUrl: e.target.value }))}
                                                onBlur={() => updateSetting('TranscriptionServerUrl', settings['TranscriptionServerUrl'] || '')}
                                                onKeyDown={(e) => {
                                                    if (e.key === 'Enter') {
                                                        updateSetting('TranscriptionServerUrl', settings['TranscriptionServerUrl'] || '');
                                                        testRemoteConnection(settings['TranscriptionServerUrl']);
                                                    }
                                                }}
                                            />
                                            <Button
                                                variant="outlined"
                                                size="small"
                                                onClick={() => testRemoteConnection()}
                                                disabled={connectionStatus === 'testing' || !settings['TranscriptionServerUrl']}
                                                sx={{ whiteSpace: 'nowrap', minWidth: 80 }}
                                            >
                                                {connectionStatus === 'testing' ? (
                                                    <CircularProgress size={18} />
                                                ) : 'Test'}
                                            </Button>
                                        </Box>

                                        {connectionStatus === 'ok' && remoteServerInfo && (
                                            <Alert severity="success" sx={{ mt: 1.5 }} variant="outlined">
                                                <AlertTitle>Connected</AlertTitle>
                                                <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.5 }}>
                                                    <Typography variant="body2">
                                                        Model: <strong>{remoteServerInfo.model}</strong>
                                                    </Typography>
                                                    <Typography variant="body2">
                                                        Acceleration: <strong>{remoteServerInfo.acceleration ?? 'CPU'}</strong>
                                                        {remoteServerInfo.gpu && ` (${remoteServerInfo.gpu}${remoteServerInfo.gpuMemoryMb ? ` - ${remoteServerInfo.gpuMemoryMb} MB` : ''})`}
                                                        {!remoteServerInfo.gpu && remoteServerInfo.cpu && ` (${remoteServerInfo.cpu})`}
                                                    </Typography>
                                                    <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap' }}>
                                                        <Chip
                                                            label={remoteServerInfo.binaryFound ? 'whisper-cli found' : 'whisper-cli missing'}
                                                            color={remoteServerInfo.binaryFound ? 'success' : 'error'}
                                                            size="small"
                                                            variant="outlined"
                                                        />
                                                        <Chip
                                                            label={remoteServerInfo.modelFound ? 'Model loaded' : 'Model missing'}
                                                            color={remoteServerInfo.modelFound ? 'success' : 'error'}
                                                            size="small"
                                                            variant="outlined"
                                                        />
                                                        <Chip
                                                            label={remoteServerInfo.diarizationAvailable ? 'Diarization ready' : 'Diarization unavailable'}
                                                            color={remoteServerInfo.diarizationAvailable ? 'success' : 'default'}
                                                            size="small"
                                                            variant="outlined"
                                                        />
                                                    </Box>
                                                </Box>
                                            </Alert>
                                        )}

                                        {connectionStatus === 'error' && (
                                            <Alert severity="error" sx={{ mt: 1.5 }} variant="outlined">
                                                <AlertTitle>Connection Failed</AlertTitle>
                                                <Typography variant="body2">
                                                    {connectionError || 'Could not reach the remote server.'}
                                                </Typography>
                                            </Alert>
                                        )}
                                    </Paper>

                                    <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', width: '100%', mt: 1 }}>
                                        <Box>
                                            <Typography variant="body2" fontWeight="bold">Speaker Diarization</Typography>
                                            <Typography variant="caption" color="text.secondary">
                                                Identify different speakers in transmissions using WhisperX.
                                                Requires WhisperX and a HuggingFace token on the remote server.
                                            </Typography>
                                        </Box>
                                        <Switch
                                            edge="end"
                                            checked={settings['EnableDiarization'] === 'true'}
                                            onChange={() => handleToggle('EnableDiarization', settings['EnableDiarization'] || 'false')}
                                        />
                                    </Box>
                                    </>
                                )}
                            </ListItem>
                        )}
                        {transcriptionEnabled && <Divider sx={{ my: 1 }} />}
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
