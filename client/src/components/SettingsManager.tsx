import React, { useState, useEffect } from 'react';
import { 
    Dialog, DialogTitle, DialogContent, DialogActions, 
    Button, List, ListItem, ListItemText, ListItemSecondaryAction, Switch,
    Box, CircularProgress
} from '@mui/material';

interface Props {
    open: boolean;
    onClose: () => void;
}

const SettingsManager: React.FC<Props> = ({ open, onClose }) => {
    const [settings, setSettings] = useState<Record<string, string>>({});
    const [loading, setLoading] = useState(false);

    useEffect(() => {
        if (open) {
            fetchSettings();
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
