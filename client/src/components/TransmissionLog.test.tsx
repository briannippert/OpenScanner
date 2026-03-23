import { render, screen, fireEvent, waitFor } from '@testing-library/react';
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
        expect(screen.getByPlaceholderText('Search logs...')).toBeInTheDocument();
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
        expect(screen.getByText('No recent activity.')).toBeInTheDocument();
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
             expect(mockOnPlay).toHaveBeenCalledWith('1', 'audio1.wav', 5.5);
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
        
        const searchInput = screen.getByPlaceholderText('Search logs...');
        fireEvent.change(searchInput, { target: { value: 'Search' } });

        await waitFor(() => {
            expect(fetchMock).toHaveBeenCalledWith(expect.stringContaining('/api/history/search?q=Search'));
            expect(screen.getByText('Search Result')).toBeInTheDocument();
        });
    });
});
