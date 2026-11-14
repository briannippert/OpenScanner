import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import ScannerDisplay from './ScannerDisplay';
import type { ScannerState, Channel } from '../types';

const channel = (frequency: number): Channel =>
    ({ id: frequency, frequency, alphaTag: `CH ${frequency}`, mode: 'NFM' } as unknown as Channel);

const parallelState = (db: number): ScannerState => ({
    status: 'SCANNING',
    signalStrength: 0,
    parallelChannels: [
        { channel: channel(154.1), isActive: false, signalStrength: db, isRecording: false },
        { channel: channel(155.2), isActive: true, signalStrength: db - 3.3, isRecording: false },
    ],
});

/** Total CSS rules emotion has injected into the document so far. */
const cssRuleCount = () => Array.from(document.querySelectorAll('style'))
    .reduce((n, el) => n + (el.sheet?.cssRules.length ?? 0) + (el.sheet ? 0 : 1), 0);

describe('ScannerDisplay', () => {
    // The server sends scanner state ten times a second with a fresh float dB per parallel
    // channel. Styling the meter through `sx` made emotion inject (and keep forever) a new
    // CSS class for every distinct value, so a page left open for hours bogged down.
    it('does not add CSS rules as parallel signal levels change', () => {
        // Visit both ends of the meter first so its (bounded) colour variants are in place.
        const { rerender } = render(<ScannerDisplay state={parallelState(-60)} />);
        rerender(<ScannerDisplay state={parallelState(-10)} />);
        const before = cssRuleCount();
        for (let i = 0; i < 200; i++) {
            rerender(<ScannerDisplay state={parallelState(-60 + i * 0.237)} />);
        }
        expect(cssRuleCount()).toBe(before);
    });
});
