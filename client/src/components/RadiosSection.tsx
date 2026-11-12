import React, { useState } from 'react';
import {
    Alert, Box, Button, Switch, TextField, ToggleButton, ToggleButtonGroup, Tooltip, Typography,
} from '@mui/material';
import RefreshIcon from '@mui/icons-material/Refresh';
import StatusChip from './common/StatusChip';
import type { AssignmentMode, PlanDevice, Radio, RadiosResponse, ScannerState } from '../types';
import { planSummary, roleSummary } from '../lib/radios';
import type { RadioPatch } from '../hooks/useRadios';

const MONO = '"Roboto Mono", ui-monospace, SFMono-Regular, Menlo, monospace';

/** Measured crystal error on this SDR's strongest active channel, as a ppm figure. */
const measuredPpm = (state: ScannerState | undefined, deviceId: string, single: boolean) => {
    const active = (state?.parallelChannels ?? [])
        .filter(pc => (pc.deviceId === deviceId || (single && !pc.deviceId)))
        .filter(pc => pc.isActive && pc.measuredOffsetHz != null && pc.channel.frequency > 0)
        .sort((a, b) => b.signalStrength - a.signalStrength);
    if (active.length === 0) return null;
    const best = active[0];
    return {
        offsetHz: best.measuredOffsetHz!,
        ppm: best.measuredOffsetHz! / (best.channel.frequency * 1e6) * 1e6,
        label: best.channel.alphaTag || `${best.channel.frequency} MHz`,
    };
};

/** A number input that saves on blur or Enter, not on every keystroke (each save restarts the SDR). */
const NumberField: React.FC<{
    label: string; value: number; min: number; max: number; step?: number; onCommit: (v: number) => void; width?: number;
}> = ({ label, value, min, max, step = 0.1, onCommit, width = 96 }) => {
    // Seeded from the saved value; the caller keys this by that value, so a save (or a
    // change from elsewhere) remounts it with the new one.
    const [draft, setDraft] = useState(String(value));
    const commit = () => {
        const v = parseFloat(draft);
        if (!Number.isFinite(v) || v < min || v > max) { setDraft(String(value)); return; }
        if (v !== value) onCommit(v);
    };
    return (
        <TextField
            label={label}
            type="number"
            size="small"
            value={draft}
            inputProps={{ min, max, step, 'aria-label': label }}
            onChange={e => setDraft(e.target.value)}
            onBlur={commit}
            onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
            sx={{ width }}
        />
    );
};

/** The SDR's name, saved on blur or Enter. Keyed by the saved name, like NumberField. */
const LabelField: React.FC<{ value: string; onCommit: (v: string) => void }> = ({ value, onCommit }) => {
    const [label, setLabel] = useState(value);
    return (
        <TextField
            size="small"
            value={label}
            inputProps={{ 'aria-label': 'SDR name', maxLength: 64 }}
            onChange={e => setLabel(e.target.value)}
            onBlur={() => { if (label.trim() && label !== value) onCommit(label.trim()); else setLabel(value); }}
            onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
            sx={{ width: 180 }}
        />
    );
};

const RadioCard: React.FC<{
    radio: Radio;
    planned?: PlanDevice;
    scannerState?: ScannerState;
    single: boolean;
    onUpdate: (patch: RadioPatch) => void;
}> = ({ radio, planned, scannerState, single, onUpdate }) => {
    const live = scannerState?.devices?.find(d => d.id === radio.id);
    const measured = measuredPpm(scannerState, radio.id, single);

    const chip = !radio.attached
        ? { label: 'UNPLUGGED', tone: 'error' as const }
        : !radio.enabled
            ? { label: 'DISABLED', tone: 'muted' as const }
            : live && !live.isHardwareConnected && live.status !== 'IDLE'
                ? { label: 'NO DATA', tone: 'warn' as const }
                : { label: (planned?.role ?? 'Idle') === 'FastScan' ? 'FAST SCAN' : (planned?.role ?? 'Idle').toUpperCase(), tone: planned?.role === 'Idle' || !planned ? 'muted' as const : 'success' as const };

    return (
        <Box
            data-testid={`radio-${radio.id}`}
            sx={{ border: '1px solid', borderColor: 'surface.border', borderRadius: 1.5, p: 1.5, mb: 1.5 }}
        >
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, flexWrap: 'wrap' }}>
                <LabelField key={radio.label} value={radio.label} onCommit={v => onUpdate({ label: v })} />
                <StatusChip label={chip.label} tone={chip.tone} sx={{ fontSize: 10 }} />
                <Box flexGrow={1} />
                <Tooltip title={radio.enabled ? 'Stop using this SDR' : 'Use this SDR'}>
                    <Switch
                        checked={radio.enabled}
                        onChange={() => onUpdate({ enabled: !radio.enabled })}
                        inputProps={{ 'aria-label': `Use ${radio.label}` }}
                    />
                </Tooltip>
            </Box>

            <Typography variant="caption" color="text.secondary" component="div" sx={{ mt: 0.5, fontFamily: MONO }}>
                {[radio.product || 'RTL-SDR', radio.serial ? `SN ${radio.serial}` : 'no serial', radio.index != null ? `USB #${radio.index}` : null]
                    .filter(Boolean).join(' · ')}
            </Typography>
            {radio.attached && radio.enabled && (
                <Typography variant="body2" sx={{ mt: 0.5 }}>
                    {roleSummary(planned)}
                    {planned && planned.role === 'FastScan' && planned.gainClass !== 'Mixed' && ` · ${planned.gainClass.toLowerCase()} gain`}
                    {planned && planned.role === 'FastScan' && planned.gainClass === 'Mixed' && ' · mixed gain'}
                </Typography>
            )}

            <Box sx={{ display: 'flex', gap: 1.5, mt: 1.5, flexWrap: 'wrap', alignItems: 'center' }}>
                <NumberField label="Analog dB" key={`gainAnalog-${radio.gainAnalog}`} value={radio.gainAnalog} min={0} max={60} onCommit={v => onUpdate({ gainAnalog: v })} />
                <NumberField label="Digital dB" key={`gainDigital-${radio.gainDigital}`} value={radio.gainDigital} min={0} max={60} onCommit={v => onUpdate({ gainDigital: v })} />
                <NumberField label="Mixed dB" key={`gainMixed-${radio.gainMixed}`} value={radio.gainMixed} min={0} max={60} onCommit={v => onUpdate({ gainMixed: v })} />
                <NumberField label="PPM" key={`ppm-${radio.ppm}`} value={radio.ppm} min={-500} max={500} onCommit={v => onUpdate({ ppm: v })} />
                {measured && (
                    <Tooltip title={`Measured on ${measured.label}: ${(measured.offsetHz / 1000).toFixed(2)} kHz off`}>
                        <Button size="small" variant="outlined" onClick={() => onUpdate({ ppm: Number((radio.ppm + measured.ppm).toFixed(1)) })}>
                            Apply {(radio.ppm + measured.ppm).toFixed(1)} ppm
                        </Button>
                    </Tooltip>
                )}
            </Box>
        </Box>
    );
};

interface Props {
    radios: RadiosResponse;
    error: string | null;
    scannerState?: ScannerState;
    onSetMode: (mode: AssignmentMode) => void;
    onUpdate: (id: string, patch: RadioPatch) => void;
    onRescan: () => void;
}

/**
 * Settings for every SDR dongle: whether channels are divided automatically or by hand, how
 * the current plan covers them, and each dongle's gains and frequency correction.
 */
const RadiosSection: React.FC<Props> = ({ radios, error, scannerState, onSetMode, onUpdate, onRescan }) => {
    const byId = new Map(radios.plan.devices.map(d => [d.deviceId, d]));
    const single = radios.devices.length === 1;

    return (
        <>
            <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 2, py: 0.75, flexWrap: 'wrap' }}>
                <Box sx={{ minWidth: 0, flex: '1 1 260px' }}>
                    <Typography variant="body2" fontWeight={600}>Channel assignment</Typography>
                    <Typography variant="caption" color="text.secondary">
                        {radios.mode === 'auto'
                            ? 'Auto divides channels so as many as possible are fast-scanned (heard simultaneously), keeping analog and digital on separate SDRs where that costs nothing.'
                            : 'Manual uses the SDR you pick for each channel in Manage Channels. Channels left on Auto, or pinned to an SDR that is unplugged, are placed automatically.'}
                    </Typography>
                </Box>
                <ToggleButtonGroup
                    size="small"
                    exclusive
                    value={radios.mode}
                    onChange={(_, v: AssignmentMode | null) => { if (v && v !== radios.mode) onSetMode(v); }}
                    aria-label="Channel assignment mode"
                >
                    <ToggleButton value="auto">Auto</ToggleButton>
                    <ToggleButton value="manual">Manual</ToggleButton>
                </ToggleButtonGroup>
            </Box>

            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, my: 1 }}>
                <Typography variant="body2" color="text.secondary" sx={{ flexGrow: 1 }} data-testid="plan-summary">
                    {planSummary(radios.plan)}
                </Typography>
                <Button size="small" startIcon={<RefreshIcon />} onClick={onRescan}>Rescan</Button>
            </Box>

            {error && <Alert severity="error" sx={{ mb: 1 }}>{error}</Alert>}
            {radios.warnings.map(w => <Alert key={w} severity="warning" sx={{ mb: 1 }}>{w}</Alert>)}

            {radios.devices.map(r => (
                <RadioCard
                    key={r.id}
                    radio={r}
                    planned={byId.get(r.id)}
                    scannerState={scannerState}
                    single={single}
                    onUpdate={patch => onUpdate(r.id, patch)}
                />
            ))}

            <Typography variant="caption" color="text.secondary" component="div">
                Gain is in dB; 0 uses the tuner&apos;s AGC. An SDR carrying only digital (P25/DMR) channels uses the
                digital gain, which usually wants to be lower: overload and intermodulation break symbol decoding long
                before they are audible. Analog-only SDRs use the analog gain, and an SDR carrying both uses the mixed
                gain. A hopping SDR switches gain with each channel. PPM corrects the dongle&apos;s crystal error; each
                dongle has its own.
            </Typography>
        </>
    );
};

export default RadiosSection;
