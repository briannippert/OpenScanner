import React, { useState, useEffect, useCallback } from 'react';
import { 
    Dialog, DialogTitle, DialogContent, DialogActions, 
    Button, Switch,
    Box, CircularProgress, Alert, AlertTitle, Link,
    TextField, ToggleButton, ToggleButtonGroup, Typography, Chip,
    Paper, Stack
} from '@mui/material';
import SystemUpdateIcon from '@mui/icons-material/SystemUpdate';
import RecordVoiceOverIcon from '@mui/icons-material/RecordVoiceOver';
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined';

interface Props {
    open: boolean;
    onClose: () => void;
}

const SectionHeader: React.FC<{ icon: React.ReactNode; title: string }> = ({ icon, title }) => (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 1.5 }}>
        <Box sx={{ color: 'primary.main', display: 'flex' }}>{icon}</Box>
        <Typography variant="subtitle2" fontWeight="bold" sx={{ textTransform: 'uppercase', letterSpacing: 0.5 }}>
            {title}
        </Typography>
    </Box>
);

const SettingRow: React.FC<{
    label: string;
    description: string;
    checked: boolean;
    disabled?: boolean;
    onChange: () => void;
}> = ({ label, description, checked, disabled, onChange }) => (
    <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', py: 0.5 }}>
        <Box sx={{ pr: 2 }}>
            <Typography variant="body2" fontWeight="medium" sx={{ color: disabled ? 'text.disabled' : 'text.primary' }}>
                {label}
            </Typography>
            <Typography variant="caption" color="text.secondary">{description}</Typography>
        </Box>
        <Switch
            edge="end"
            checked={checked}
            disabled={disabled}
            onChange={onChange}
            size="small"
        />
    </Box>
);

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

    const fetchSettings = useCallback(async (): Promise<Record<string, string> | null> => {
        setLoading(true);
        try {
            const res = await fetch(`${getBackendUrl()}/api/settings`);
            if (res.ok) {
                const data = await res.json();
                setSettings(data);
                return data;
            }
        } catch (error) {
            console.error("Failed to fetch settings", error);
        } finally {
            setLoading(false);
        }
        return null;
    }, []);

    const fetchSystemInfo = useCallback(async () => {
        try {
            const res = await fetch(`${getBackendUrl()}/api/system/info`);
            if (res.ok) {
                const data = await res.json();
                setSystemInfo(data);
            }
        } catch (error) {
            console.error("Failed to fetch system info", error);
        }
    }, []);

    const fetchLatestVersion = useCallback(async () => {
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
    }, []);

    const updateSetting = useCallback(async (key: string, value: string) => {
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
    }, []);

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
                if (data.status === 'ok') {
                    updateSetting('TranscriptionServerUrl', serverUrl);
                }
                if (!data.diarizationAvailable && settings['EnableDiarization'] === 'true') {
                    updateSetting('EnableDiarization', 'false');
                }
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
    }, [settings, updateSetting]);

    useEffect(() => {
        if (open) {
            fetchSettings().then((data) => {
                if (data && data['TranscriptionMode'] === 'remote' && data['TranscriptionServerUrl']) {
                    testRemoteConnection(data['TranscriptionServerUrl']);
                }
            });
            fetchSystemInfo();
            fetchLatestVersion();
        }
    }, [open, fetchSettings, fetchSystemInfo, fetchLatestVersion, testRemoteConnection]);

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

    const isRemote = settings['TranscriptionMode'] === 'remote';
    const transcriptionEnabled = settings['EnableTranscription'] === 'true';

    return (
        <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
            <DialogTitle sx={{ pb: 1 }}>Settings</DialogTitle>
            <DialogContent dividers sx={{ p: 2 }}>
                {loading ? (
                    <Box display="flex" justifyContent="center" p={4}>
                        <CircularProgress />
                    </Box>
                ) : (
                    <Stack spacing={2}>

                        {/* Update Banner */}
                        {updateAvailable && (
                            <Alert 
                                severity="success" 
                                icon={<SystemUpdateIcon />}
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

                        {/* Transcription Section */}
                        <Paper variant="outlined" sx={{ p: 2 }}>
                            <SectionHeader icon={<RecordVoiceOverIcon fontSize="small" />} title="Transcription" />

                            <SettingRow
                                label="AI Transcription"
                                description="Enable Whisper AI speech-to-text for recorded transmissions."
                                checked={transcriptionEnabled}
                                onChange={() => handleToggle('EnableTranscription', settings['EnableTranscription'] || 'false')}
                            />

                            {transcriptionEnabled && (
                                <Stack spacing={2} sx={{ mt: 1.5, pt: 1.5, borderTop: '1px solid', borderColor: 'divider' }}>
                                    <Box>
                                        <Typography variant="body2" fontWeight="medium" sx={{ mb: 0.5 }}>Mode</Typography>
                                        <ToggleButtonGroup
                                            value={settings['TranscriptionMode'] || 'local'}
                                            exclusive
                                            size="small"
                                            fullWidth
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
                                    </Box>

                                    {isRemote && (
                                        <Stack spacing={1.5}>
                                            {/* Server URL */}
                                            <Box>
                                                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
                                                    Connect to a machine running the OpenScanner WhisperServer.
                                                </Typography>
                                                <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                                                    <TextField
                                                        size="small"
                                                        fullWidth
                                                        label="Server URL"
                                                        placeholder="http://192.168.1.100:8090"
                                                        value={settings['TranscriptionServerUrl'] || ''}
                                                        onChange={(e) => setSettings(prev => ({ ...prev, TranscriptionServerUrl: e.target.value }))}
                                                        onBlur={() => testRemoteConnection(settings['TranscriptionServerUrl'])}
                                                        onKeyDown={(e) => {
                                                            if (e.key === 'Enter') {
                                                                testRemoteConnection(settings['TranscriptionServerUrl']);
                                                            }
                                                        }}
                                                    />
                                                    <Button
                                                        variant="outlined"
                                                        size="small"
                                                        onClick={() => testRemoteConnection()}
                                                        disabled={connectionStatus === 'testing' || !settings['TranscriptionServerUrl']}
                                                        sx={{ whiteSpace: 'nowrap', minWidth: 80, height: 40 }}
                                                    >
                                                        {connectionStatus === 'testing' ? (
                                                            <CircularProgress size={18} />
                                                        ) : 'Test'}
                                                    </Button>
                                                </Box>
                                            </Box>

                                            {/* Connection Result */}
                                            {connectionStatus === 'ok' && remoteServerInfo && (
                                                <Alert severity="success" variant="outlined" icon={false} sx={{ '& .MuiAlert-message': { width: '100%' } }}>
                                                    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.75 }}>
                                                        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                                                            <Typography variant="body2" fontWeight="bold">Connected</Typography>
                                                            <Chip label={remoteServerInfo.model} size="small" color="primary" variant="outlined" />
                                                        </Box>
                                                        <Typography variant="caption" color="text.secondary">
                                                            {remoteServerInfo.acceleration ?? 'CPU'}
                                                            {remoteServerInfo.gpu && ` -- ${remoteServerInfo.gpu}`}
                                                            {remoteServerInfo.gpuMemoryMb && ` (${remoteServerInfo.gpuMemoryMb} MB)`}
                                                            {!remoteServerInfo.gpu && remoteServerInfo.cpu && ` -- ${remoteServerInfo.cpu}`}
                                                        </Typography>
                                                        <Box sx={{ display: 'flex', gap: 0.75, flexWrap: 'wrap' }}>
                                                            <Chip
                                                                label={remoteServerInfo.binaryFound ? 'whisper-cli' : 'whisper-cli missing'}
                                                                color={remoteServerInfo.binaryFound ? 'success' : 'error'}
                                                                size="small" variant="outlined"
                                                            />
                                                            <Chip
                                                                label={remoteServerInfo.modelFound ? 'Model loaded' : 'Model missing'}
                                                                color={remoteServerInfo.modelFound ? 'success' : 'error'}
                                                                size="small" variant="outlined"
                                                            />
                                                            <Chip
                                                                label={remoteServerInfo.diarizationAvailable ? 'WhisperX' : 'No WhisperX'}
                                                                color={remoteServerInfo.diarizationAvailable ? 'success' : 'default'}
                                                                size="small" variant="outlined"
                                                            />
                                                        </Box>
                                                    </Box>
                                                </Alert>
                                            )}

                                            {connectionStatus === 'error' && (
                                                <Alert severity="error" variant="outlined">
                                                    <AlertTitle>Connection Failed</AlertTitle>
                                                    <Typography variant="body2">
                                                        {connectionError || 'Could not reach the remote server.'}
                                                    </Typography>
                                                </Alert>
                                            )}

                                            {/* Speaker Diarization */}
                                            <Box sx={{ pt: 0.5, borderTop: '1px solid', borderColor: 'divider' }}>
                                                <SettingRow
                                                    label="Speaker Diarization"
                                                    description={
                                                        remoteServerInfo?.diarizationAvailable === false
                                                            ? 'Not available. Install WhisperX and configure a HuggingFace token on the server.'
                                                            : 'Identify different speakers in transmissions using WhisperX.'
                                                    }
                                                    checked={settings['EnableDiarization'] === 'true'}
                                                    disabled={!remoteServerInfo || !remoteServerInfo.diarizationAvailable}
                                                    onChange={() => handleToggle('EnableDiarization', settings['EnableDiarization'] || 'false')}
                                                />
                                            </Box>
                                        </Stack>
                                    )}
                                </Stack>
                            )}
                        </Paper>

                        {/* System Info Section */}
                        {(systemInfo.Version || systemInfo.Commit) && (
                            <Paper variant="outlined" sx={{ p: 2 }}>
                                <SectionHeader icon={<InfoOutlinedIcon fontSize="small" />} title="System" />
                                <Stack spacing={0.5}>
                                    {systemInfo.Version && (
                                        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                                            <Typography variant="body2" color="text.secondary">Version</Typography>
                                            <Typography variant="body2" fontWeight="medium">{systemInfo.Version}</Typography>
                                        </Box>
                                    )}
                                    {systemInfo.Commit && (
                                        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                                            <Typography variant="body2" color="text.secondary">Commit</Typography>
                                            <Typography variant="body2" sx={{ fontFamily: 'monospace', fontSize: '0.75rem' }}>
                                                {systemInfo.Commit.substring(0, 12)}
                                            </Typography>
                                        </Box>
                                    )}
                                </Stack>
                            </Paper>
                        )}

                    </Stack>
                )}
            </DialogContent>
            <DialogActions>
                <Button onClick={onClose}>Close</Button>
            </DialogActions>
        </Dialog>
    );
};

export default SettingsManager;
