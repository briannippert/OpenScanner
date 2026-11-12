import type { PlanDevice, SdrPlan } from '../types';

/** One line describing a plan's coverage, e.g. "5 of 7 channels fast-scanned across 2 SDRs · 2 hopping on SDR 2". */
export const planSummary = (plan: SdrPlan): string => {
    const busy = plan.devices.filter(d => d.role !== 'Idle');
    const parts = [
        `${plan.fastScanned} of ${plan.totalChannels} channel${plan.totalChannels === 1 ? '' : 's'} fast-scanned` +
        ` across ${busy.length} SDR${busy.length === 1 ? '' : 's'}`,
        ...busy.filter(d => d.role === 'Hop').map(d => `${d.frequencies.length} hopping on ${d.label}`),
    ];
    if (plan.unscannedChannelIds.length > 0) parts.push(`${plan.unscannedChannelIds.length} not scanned`);
    return parts.join(' · ');
};

/** What an SDR is doing, e.g. "Fast scan 155.000–156.200 MHz · 6 channels". */
export const roleSummary = (p?: PlanDevice): string => {
    if (!p || p.role === 'Idle') return 'Idle: no channels assigned';
    const n = `${p.frequencies.length} channel${p.frequencies.length === 1 ? '' : 's'}`;
    if (p.role === 'Hop') return `Hopping · ${n} (one at a time)`;
    const lo = Math.min(...p.frequencies).toFixed(3);
    const hi = Math.max(...p.frequencies).toFixed(3);
    return lo === hi ? `Fast scan ${lo} MHz` : `Fast scan ${lo}–${hi} MHz · ${n}`;
};
