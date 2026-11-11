import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { useState } from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import TransmissionLog from './TransmissionLog';
import type { CallLog } from '../types';

// Mock fetch
const fetchMock = vi.fn();
vi.stubGlobal('fetch', fetchMock);

describe('TransmissionLog Component', () => {
    const mockOnPlay = vi.fn();
    const mockOnDelete = vi.fn();
    const mockLogs: CallLog[] = [
        {
            id: '1',
            frequency: 155.000,
            timestamp: '2023-10-27T10:00:00Z',
            audio_path: 'audio1.wav',
            alphaTag: 'Police Dispatch',
            description: 'Main Dispatch',
            duration: 5.5,
            sourceID: 101
        },
        {
            id: '2',
            frequency: 156.000,
            timestamp: '2023-10-27T10:05:00Z',
            audio_path: 'audio2.wav',
            alphaTag: 'Fire Dispatch',
            description: 'Fireground 1',
            duration: 3.2,
            sourceID: 202
        }
    ];

    beforeEach(() => {
        vi.clearAllMocks();
        // Default mock for years fetch
        fetchMock.mockResolvedValue({
            json: async () => []
        });
    });

    const waitForInit = async () => {
        await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/api/history/years'));
    };

    it('renders search bar', async () => {
        render(<TransmissionLog liveLogs={[]} playingId={null} onPlay={mockOnPlay} onDelete={mockOnDelete} />);
        await waitForInit();
        expect(screen.getByPlaceholderText('Search logs…')).toBeInTheDocument();
    });

    it('displays live logs in "Recent Activity"', async () => {
        render(<TransmissionLog liveLogs={mockLogs} playingId={null} onPlay={mockOnPlay} onDelete={mockOnDelete} />);
        await waitForInit();
        
        // Check if logs are visible (Recent Activity is open by default)
        expect(screen.getByText('Police Dispatch')).toBeInTheDocument();
        expect(screen.getByText('Fire Dispatch')).toBeInTheDocument();
    });

    it('displays "No recent activity" when liveLogs is empty', async () => {
        render(<TransmissionLog liveLogs={[]} playingId={null} onPlay={mockOnPlay} onDelete={mockOnDelete} />);
        await waitForInit();
        expect(screen.getByText('No recent activity')).toBeInTheDocument();
    });

    it('calls onPlay when play button is clicked', async () => {
        render(<TransmissionLog liveLogs={mockLogs} playingId={null} onPlay={mockOnPlay} onDelete={mockOnDelete} />);
        await waitForInit();
        
        // Find play buttons (using the icon would be ideal, but finding by role or test id is standard)
        // Since we don't have test-ids, we can assume the buttons are there.
        // Let's get the PlayCircleOutline icons (rendered as buttons in MUI)
        // Or better, interact with the list item secondary action.
        
        // Getting all buttons
        // Filter for the one that likely triggers play (usually the first one in the item)
        // In the component: IconButton for play is first in secondary action
        
        // A more robust way might be to add data-testid to the component, but we can try to find by specific text or behavior if possible.
        // Let's try to click the first play button we find in the logs.
        // We know there are 2 logs.
        
        // Actually, we can assume the play button is present if audio_path is present.
        // Let's look for the row "Police Dispatch" and find the button within it.
        const row = screen.getByText('Police Dispatch').closest('li');
        expect(row).toBeInTheDocument();
        
        if (row) {
             const buttonsInRow = row.querySelectorAll('button');
             // 0: Star, 1: Play, 2: Delete
             fireEvent.click(buttonsInRow[1]);
             expect(mockOnPlay).toHaveBeenCalledWith('1', 'audio1.wav', 5.5, 'Police Dispatch');
        }
    });

    it('calls onDelete when delete button is clicked', async () => {
         render(<TransmissionLog liveLogs={mockLogs} playingId={null} onPlay={mockOnPlay} onDelete={mockOnDelete} />);
         await waitForInit();
         
         const row = screen.getByText('Fire Dispatch').closest('li');
         if (row) {
             const buttonsInRow = row.querySelectorAll('button');
             // 0: Star, 1: Play, 2: Delete
             fireEvent.click(buttonsInRow[2]);
             expect(mockOnDelete).toHaveBeenCalledWith('2');
         }
    });

    it('searches logs when typing in search bar', async () => {
        const mockSearchResults = [
            {
                id: '3',
                frequency: 155.000,
                timestamp: '2023-01-01T12:00:00Z',
                alphaTag: 'Search Result',
                duration: 2.0
            }
        ];

        fetchMock.mockImplementation((url) => {
            if (url.includes('/api/history/search')) {
                return Promise.resolve({
                    json: async () => mockSearchResults
                });
            }
            return Promise.resolve({ json: async () => [] });
        });

        render(<TransmissionLog liveLogs={[]} playingId={null} onPlay={mockOnPlay} onDelete={mockOnDelete} />);
        
        const searchInput = screen.getByPlaceholderText('Search logs…');
        fireEvent.change(searchInput, { target: { value: 'Search' } });

        await waitFor(() => {
            expect(fetchMock).toHaveBeenCalledWith(expect.stringContaining('/api/history/search?q=Search'));
            expect(screen.getByText('Search Result')).toBeInTheDocument();
        });
    });
    // The app re-renders on every scanner state update (up to 10/s). The log panel
    // holds up to 100 rows of MUI components, so it must sit those renders out.
    // Every row with a source ID resolves its display name through `nameFor` while
    // rendering, so counting those calls counts row renders.
    const hundredLogs: CallLog[] = Array.from({ length: 100 }, (_, i) => ({
        ...mockLogs[0], id: `log-${i}`, alphaTag: `Channel ${i}`,
    }));

    it('does not re-render when its parent re-renders with the same props', async () => {
        const nameFor = vi.fn();
        const Parent = () => {
            const [tick, setTick] = useState(0);
            return (
                <>
                    <button onClick={() => setTick(t => t + 1)}>tick {tick}</button>
                    <TransmissionLog liveLogs={hundredLogs} playingId={null} onPlay={mockOnPlay} onDelete={mockOnDelete} nameFor={nameFor} />
                </>
            );
        };
        render(<Parent />);
        await waitForInit();
        expect(screen.getByText('Channel 99')).toBeInTheDocument();
        expect(nameFor).toHaveBeenCalled();
        nameFor.mockClear();

        fireEvent.click(screen.getByText('tick 0'));
        fireEvent.click(screen.getByText('tick 1'));

        expect(screen.getByText('tick 2')).toBeInTheDocument();
        expect(nameFor).not.toHaveBeenCalled();
    });

    it('re-renders only the affected row when playback starts', async () => {
        const nameFor = vi.fn();
        const props = { liveLogs: hundredLogs, onPlay: mockOnPlay, onDelete: mockOnDelete, nameFor };
        const { rerender } = render(<TransmissionLog {...props} playingId={null} />);
        await waitForInit();
        nameFor.mockClear();
        rerender(<TransmissionLog {...props} playingId="log-0" />);
        const callsForOneRow = nameFor.mock.calls.length;
        expect(callsForOneRow).toBeGreaterThan(0);

        // Moving playback to another row touches two rows (old and new), not 100.
        nameFor.mockClear();
        rerender(<TransmissionLog {...props} playingId="log-50" />);
        expect(nameFor.mock.calls.length).toBe(callsForOneRow * 2);
    });

    it('still updates a row when it starts and stops playing', async () => {
        const { rerender } = render(<TransmissionLog liveLogs={mockLogs} playingId={null} onPlay={mockOnPlay} onDelete={mockOnDelete} />);
        await waitForInit();
        expect(screen.queryByLabelText('Stop playback')).not.toBeInTheDocument();

        rerender(<TransmissionLog liveLogs={mockLogs} playingId="2" onPlay={mockOnPlay} onDelete={mockOnDelete} />);
        expect(screen.getAllByLabelText('Stop playback')).toHaveLength(1);
        expect(screen.getAllByLabelText('Play recording')).toHaveLength(1);

        rerender(<TransmissionLog liveLogs={mockLogs} playingId={null} onPlay={mockOnPlay} onDelete={mockOnDelete} />);
        expect(screen.queryByLabelText('Stop playback')).not.toBeInTheDocument();
    });
});
