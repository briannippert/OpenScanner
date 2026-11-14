import React, { useState, useEffect, useRef, useContext, useCallback, useMemo, createContext, memo } from 'react';
import {
    Box,
    List,
    ListItem,
    ListItemText,
    ListItemButton,
    Collapse,
    Typography,
    IconButton,
    Divider,
    TextField,
    CircularProgress,
    InputAdornment,
    Tooltip,
    Button,
} from '@mui/material';
import { alpha } from '@mui/material/styles';
import {
    ExpandLess,
    ExpandMore,
    PlayCircleOutline,
    StopCircle,
    Delete,
    Download,
    Folder,
    Search,
    Star,
    StarBorder,
    CalendarMonth,
    Today,
    Radio,
    History as HistoryIcon,
    Article,
    Link as LinkIcon,
    SearchOff,
    Inbox,
    Campaign,
    Close,
} from '@mui/icons-material';
import { Chip } from '@mui/material';
import type { CallLog, Channel } from '../types';
import { CALL_GROUPS, callTypeLabel, groupOf } from '../lib/callTypes';
import { chainLabel, srcLabel, tgLabel, type NameFor } from '../lib/aliasLabels';
import { status } from '../theme/tokens';
import EmptyState from './common/EmptyState';
import PanelSkeleton from './common/PanelSkeleton';
import SegmentedControl from './common/SegmentedControl';
import ConfirmDialog from './common/ConfirmDialog';
import { transmissionQuery, useTransmissionPages, type PageStatus } from '../hooks/useTransmissionPages';

type LogTab = 'recent' | 'favorites' | 'browse';
type LogFilter = 'all' | 'tones';

// A recording counts as a "tone-out" if it detected a tone itself, or if a fire
// tone-out event links to it (events carry the transmissionId of their recording).
const matchesFilter = (log: CallLog, filter: LogFilter, toneOutIds?: Set<string>): boolean => {
    if (filter === 'tones') return !!log.detectedTone || (!!toneOutIds && toneOutIds.has(log.id));
    return true;
};

const monoFont = { fontFamily: '"Roboto Mono", ui-monospace, monospace' };
const GOLD = '#f5c542';
const HIGHLIGHT = status.warn;

// Single indent scale for the history tree (collapses on small screens).
const indent = (depth: number) => ({ pl: { xs: Math.min(depth, 2) * 2, sm: depth * 2 } });
const rowBorder = { borderBottom: '1px solid', borderColor: 'surface.border' } as const;

interface LogNodeProps {
    playingId: string | null;
    onPlay: (id: string, path: string, duration?: number, label?: string) => void;
    onDelete: (id: string) => void;
    onFavoriteToggle: () => void;
}

/** A request to scroll to and flash a specific log. `seq` bumps on every request
 *  so re-clicking the same event re-triggers the effect. */
export interface HighlightRequest {
    id: string;
    seq: number;
}

const HighlightContext = createContext<HighlightRequest | null>(null);
/** Per-channel SRC/TG display-name resolver, provided to the deeply-nested rows. */
const AliasContext = createContext<NameFor | null>(null);
/**
 * Asks the panel to confirm deleting a recording, then runs `proceed`. One dialog for the
 * whole panel, reached through context, so each row stays as light as it was.
 */
type RequestDelete = (log: CallLog, proceed: () => void) => void;
const DeleteContext = createContext<RequestDelete | null>(null);

/** "today 14:03:22" style label for a recording, in the viewer's time zone. */
const formatLogTime = (timestamp: string): string => {
    const d = new Date(timestamp.endsWith('Z') ? timestamp : timestamp + 'Z');
    const isToday = d.toDateString() === new Date().toDateString();
    return isToday
        ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
        : `${d.toLocaleDateString([], { month: '2-digit', day: '2-digit' })} ${d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`;
};

interface Props {
    liveLogs: CallLog[];
    playingId: string | null;
    onPlay: (id: string, path: string, duration?: number, label?: string) => void;
    onDelete: (id: string) => void;
    highlight?: HighlightRequest | null;
    /** False while the initial history fetch is in flight (drives skeletons). */
    loaded?: boolean;
    /** Recording ids referenced by fire tone-out events (for the Tone-outs filter). */
    toneOutIds?: Set<string>;
    /** Resolves per-channel display names for SRC/TG. */
    nameFor?: NameFor;
}

const TransmissionLog: React.FC<Props> = ({ liveLogs, playingId, onPlay, onDelete, highlight, loaded = true, toneOutIds, nameFor }) => {
    const [searchQuery, setSearchQuery] = useState('');
    const [debouncedQuery, setDebouncedQuery] = useState('');
    const [years, setYears] = useState<string[]>([]);
    const [tab, setTab] = useState<LogTab>('recent');
    const [filter, setFilter] = useState<LogFilter>('all');
    const [powerDmsDept, setPowerDmsDept] = useState<string | null>(null);
    const [linkedLog, setLinkedLog] = useState<CallLog | null>(null);

    // Rows keep their own star state, and the Favorites tab refetches whenever it is
    // opened, so a toggle needs no list refresh. Stable identity: rows are memoized.
    const handleFavoriteToggle = useCallback(() => {}, []);

    // When a new highlight arrives, expand Recent Activity and clear any active
    // search so the target item is rendered and can be scrolled into view. This
    // adjusts state during render in response to a changed prop (React's
    // recommended pattern) rather than in an effect.
    const [lastHighlightSeq, setLastHighlightSeq] = useState<number | undefined>(highlight?.seq);
    if (highlight && highlight.seq !== lastHighlightSeq) {
        setLastHighlightSeq(highlight.seq);
        setTab('recent');
        setSearchQuery('');
    }

    // The target recording may not be in the live "Recent Activity" list — e.g.
    // an older tone-out whose recording has scrolled out of the recent window,
    // or after a page reload. In that case fetch it directly so we can pin it at
    // the top and scroll to it, making the tone-out link always work. setState
    // only happens in the async callback (never synchronously in the effect).
    useEffect(() => {
        if (!highlight) return;
        if (liveLogs.some(l => l.id === highlight.id)) return; // already rendered in Recent
        if (linkedLog?.id === highlight.id) return;            // already pinned
        const targetId = highlight.id;
        let cancelled = false;
        fetch(`/api/history/entry/${encodeURIComponent(targetId)}`)
            .then(res => (res.ok ? res.json() : null))
            .then((data: CallLog | null) => { if (!cancelled && data) setLinkedLog(data); })
            .catch(err => console.error('Failed to fetch linked recording:', err));
        return () => { cancelled = true; };
    }, [highlight, liveLogs, linkedLog]);

    // Only show the pinned recording while it is the active target and isn't
    // already present in the live list (avoids showing it twice).
    const showLinked = !!linkedLog && highlight?.id === linkedLog.id
        && !liveLogs.some(l => l.id === linkedLog.id);

    // Deleting a recording removes its audio file for good, so rows ask first. The target
    // is kept after the dialog closes so its text does not blank out during the fade.
    const [deleteTarget, setDeleteTarget] = useState<{ log: CallLog; proceed: () => void } | null>(null);
    const [deleteOpen, setDeleteOpen] = useState(false);
    const requestDelete = useCallback<RequestDelete>((log, proceed) => {
        setDeleteTarget({ log, proceed });
        setDeleteOpen(true);
    }, []);
    const confirmDelete = () => {
        setDeleteOpen(false);
        deleteTarget?.proceed();
    };

    // Search pages through the server. The box is debounced; clearing it hides the
    // results at once (searchResults keys off the live box value too).
    useEffect(() => {
        const q = searchQuery.trim();
        const timer = setTimeout(() => setDebouncedQuery(q), q ? 500 : 0);
        return () => clearTimeout(timer);
    }, [searchQuery]);
    const search = useTransmissionPages(debouncedQuery ? transmissionQuery({ q: debouncedQuery }) : null);
    const searchResults = searchQuery.trim() && debouncedQuery ? search.items : null;
    const searchLoading = !!searchResults && search.status === 'loading' && search.items.length === 0;

    // Older history below the live window. The first "Load older" pins the current live
    // list as a snapshot, so rows that later scroll out of the live window (it keeps the
    // newest 100) stay put rather than leaving a gap above the older pages.
    const [olderAnchor, setOlderAnchor] = useState<{ until: string; snapshot: CallLog[] } | null>(null);
    const older = useTransmissionPages(
        olderAnchor ? transmissionQuery({ until: olderAnchor.until, tones: filter === 'tones' }) : null,
    );
    const { loadMore: loadMoreOlder } = older;
    const loadOlder = useCallback(() => {
        if (olderAnchor) { loadMoreOlder(); return; }
        const oldest = liveLogs[liveLogs.length - 1];
        setOlderAnchor({ until: oldest?.timestamp ?? new Date().toISOString(), snapshot: liveLogs });
    }, [olderAnchor, loadMoreOlder, liveLogs]);

    // Stable identity, like handleFavoriteToggle: search-result rows are memoized too.
    const { remove: removeSearchRow } = search;
    const { remove: removeOlderRow } = older;
    const handleDelete = useCallback((id: string) => {
        removeSearchRow(id);
        removeOlderRow(id);
        setOlderAnchor(prev => prev && { ...prev, snapshot: prev.snapshot.filter(l => l.id !== id) });
        onDelete(id);
    }, [onDelete, removeSearchRow, removeOlderRow]);

    const recentLogs = useMemo(() => {
        const live = liveLogs.filter(l => matchesFilter(l, filter, toneOutIds));
        if (!olderAnchor) return live;
        const seen = new Set(live.map(l => l.id));
        const rest = [...olderAnchor.snapshot.filter(l => matchesFilter(l, filter, toneOutIds)), ...older.items]
            .filter(l => !seen.has(l.id) && !!seen.add(l.id));
        return [...live, ...rest];
    }, [liveLogs, filter, toneOutIds, olderAnchor, older.items]);

    // Initial load of years
    useEffect(() => {
        fetch('/api/history/years')
            .then(res => res.json())
            .then(data => setYears(data))
            .catch(err => console.error("Failed to fetch years:", err));
    }, []);

    // Fetch PowerDMS config once on mount
    useEffect(() => {
        fetch('/api/powerdms/config')
            .then(res => res.json())
            .then(data => setPowerDmsDept(data.department ?? null))
            .catch(() => setPowerDmsDept(null));
    }, []);


    return (
        <AliasContext.Provider value={nameFor ?? null}>
        <HighlightContext.Provider value={highlight ?? null}>
        <DeleteContext.Provider value={requestDelete}>
        <Box data-testid="transmission-log" sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
            <Box sx={{ p: 1.5, borderBottom: '1px solid', borderColor: 'surface.border', display: 'flex', flexDirection: 'column', gap: 1.25 }}>
                <TextField
                    fullWidth
                    variant="outlined"
                    size="small"
                    placeholder="Search logs…"
                    value={searchQuery}
                    onChange={(e) => setSearchQuery(e.target.value)}
                    onKeyDown={(e) => { if (e.key === 'Escape' && searchQuery) { e.stopPropagation(); setSearchQuery(''); } }}
                    slotProps={{
                        htmlInput: { 'aria-label': 'Search logs' },
                        input: {
                            startAdornment: (
                                <InputAdornment position="start">
                                    <Search sx={{ color: 'text.secondary' }} />
                                </InputAdornment>
                            ),
                            endAdornment: searchQuery ? (
                                <InputAdornment position="end">
                                    <IconButton size="small" edge="end" onClick={() => setSearchQuery('')} aria-label="Clear search" title="Clear search">
                                        <Close fontSize="small" />
                                    </IconButton>
                                </InputAdornment>
                            ) : undefined,
                        },
                    }}
                    sx={{
                        '& .MuiOutlinedInput-root': {
                            bgcolor: 'surface.base',
                            '&.Mui-focused fieldset': { borderColor: 'primary.main' },
                        },
                    }}
                />
                {!searchResults && (
                    <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 1, flexWrap: 'wrap' }}>
                        <SegmentedControl<LogTab>
                            aria-label="Recording view"
                            size="small"
                            segments={[
                                { value: 'recent', label: 'Recent', icon: <HistoryIcon /> },
                                { value: 'favorites', label: 'Favorites', icon: <Star /> },
                                { value: 'browse', label: 'Browse', icon: <Folder /> },
                            ]}
                            value={tab}
                            onChange={setTab}
                        />
                        {tab !== 'browse' && (
                            <Box sx={{ display: 'flex', gap: 0.75 }}>
                                {([
                                    { value: 'all', label: 'All' },
                                    { value: 'tones', label: 'Tone-outs', icon: <Campaign sx={{ fontSize: 15 }} /> },
                                ] as { value: LogFilter; label: string; icon?: React.ReactNode }[]).map(f => (
                                    <Chip
                                        key={f.value}
                                        label={f.label}
                                        icon={f.icon as React.ReactElement | undefined}
                                        size="small"
                                        variant={filter === f.value ? 'filled' : 'outlined'}
                                        color={filter === f.value ? 'primary' : 'default'}
                                        onClick={() => setFilter(f.value)}
                                        sx={{ cursor: 'pointer' }}
                                    />
                                ))}
                            </Box>
                        )}
                    </Box>
                )}
            </Box>

            <Box data-log-scroll sx={{ flexGrow: 1, minHeight: 0, overflowY: 'auto' }}>
                {searchLoading && <Box sx={{ p: 2, textAlign: 'center' }}><CircularProgress size={20} /></Box>}

                {searchResults ? (
                    <List dense>
                        {searchResults.length === 0 && search.status === 'loaded' && (
                            <EmptyState icon={<SearchOff />} title="No results found" hint="Try a different search term." />
                        )}
                        {searchResults.map(log => (
                            <LogItem key={log.id} log={log} playingId={playingId} onPlay={onPlay} onDelete={handleDelete} onFavoriteToggle={handleFavoriteToggle} />
                        ))}
                        {searchResults.length > 0 && (
                            <LoadMoreRow status={search.status} hasMore={search.hasMore} onLoadMore={search.loadMore} label="Load more results" />
                        )}
                    </List>
                ) : tab === 'recent' ? (
                    <List dense disablePadding>
                        {/* Linked Recording — a tone-out target that isn't in the recent list */}
                        {showLinked && linkedLog && (
                            <Box sx={{ ...rowBorder }}>
                                <Box sx={{ display: 'flex', alignItems: 'center', px: 2, py: 1, bgcolor: alpha(HIGHLIGHT, 0.06) }}>
                                    <LinkIcon sx={{ mr: 2, color: HIGHLIGHT, fontSize: 20 }} />
                                    <Typography sx={{ fontWeight: 'bold', color: HIGHLIGHT, fontSize: '0.9rem' }}>
                                        Linked Recording
                                    </Typography>
                                </Box>
                                <LogItem log={linkedLog} playingId={playingId} onPlay={onPlay} onDelete={onDelete} onFavoriteToggle={handleFavoriteToggle} />
                            </Box>
                        )}
                        {!loaded ? (
                            <Box sx={{ p: 2 }}><PanelSkeleton rows={6} rowHeight={44} /></Box>
                        ) : recentLogs.length === 0 && !olderAnchor && filter === 'all' ? (
                            <EmptyState icon={<Inbox />} title="No recent activity" hint="Live transmissions will appear here as they are received." />
                        ) : (
                            <>
                                {recentLogs.length === 0 && older.status !== 'loading' && (
                                    <EmptyState icon={<Inbox />} title="No recent activity" hint={olderAnchor ? 'No recordings match this filter.' : 'No recent recordings match this filter.'} />
                                )}
                                {recentLogs.map(log => (
                                    <LogItem key={log.id} log={log} playingId={playingId} onPlay={onPlay} onDelete={handleDelete} onFavoriteToggle={handleFavoriteToggle} />
                                ))}
                                <LoadMoreRow
                                    status={olderAnchor ? older.status : 'idle'}
                                    hasMore={!olderAnchor || older.hasMore}
                                    onLoadMore={loadOlder}
                                    label="Load older recordings"
                                />
                            </>
                        )}
                    </List>
                ) : tab === 'favorites' ? (
                    <FavoritesList filter={filter} playingId={playingId} onPlay={onPlay} onDelete={onDelete} onFavoriteToggle={handleFavoriteToggle} />
                ) : (
                    <>
                        <List dense component="nav">
                            {years.length === 0 ? (
                                <EmptyState icon={<Folder />} title="No archived recordings" hint="Older recordings are grouped by date here." />
                            ) : years.map(year => (
                                <YearNode key={year} year={year} powerDmsDept={powerDmsDept} playingId={playingId} onPlay={onPlay} onDelete={onDelete} onFavoriteToggle={handleFavoriteToggle} />
                            ))}
                        </List>
                    </>
                )}
            </Box>
            <ConfirmDialog
                open={deleteOpen}
                title="Delete recording?"
                confirmLabel="Delete"
                onConfirm={confirmDelete}
                onCancel={() => setDeleteOpen(false)}
            >
                {deleteTarget && (
                    <>
                        This permanently deletes the recording from <strong>{deleteTarget.log.alphaTag || `${deleteTarget.log.frequency.toFixed(3)} MHz`}</strong> ({formatLogTime(deleteTarget.log.timestamp)}) and its audio file. This cannot be undone.
                    </>
                )}
            </ConfirmDialog>
        </Box>
        </DeleteContext.Provider>
        </HighlightContext.Provider>
        </AliasContext.Provider>
    );
};

// Fetched each time the Favorites tab opens (switching tabs unmounts it), one page at
// a time. The tone-out filter runs on the server so it covers every starred recording.
const FavoritesList = ({ filter, playingId, onPlay, onDelete, onFavoriteToggle }: { filter: LogFilter } & LogNodeProps) => {
    const favorites = useTransmissionPages(transmissionQuery({ favorites: true, tones: filter === 'tones' }));
    const { remove } = favorites;

    const handleDelete = useCallback((id: string) => {
        remove(id);
        onDelete(id);
    }, [remove, onDelete]);

    if (favorites.items.length === 0) {
        if (favorites.status === 'idle' || favorites.status === 'loading') {
            return <Box sx={{ p: 2 }}><PanelSkeleton rows={5} rowHeight={44} /></Box>;
        }
        if (favorites.status === 'error') {
            return <LoadMoreRow status="error" hasMore onLoadMore={favorites.loadMore} label="Load favorites" />;
        }
        return filter === 'tones'
            ? <EmptyState icon={<StarBorder />} title="No starred tone-outs" hint="Starred recordings with a tone-out appear here." />
            : <EmptyState icon={<StarBorder />} title="No favorites yet" hint="Star a recording to add it here." />;
    }
    return (
        <List dense disablePadding>
            {favorites.items.map(log => (
                <LogItem key={log.id} log={log} playingId={playingId} onPlay={onPlay} onDelete={handleDelete} onFavoriteToggle={onFavoriteToggle} />
            ))}
            <LoadMoreRow status={favorites.status} hasMore={favorites.hasMore} onLoadMore={favorites.loadMore} label="Load more favorites" />
        </List>
    );
};

/**
 * Footer for a paged list: a "load more" button, a spinner while a page loads, or a
 * retry after a failure. Renders nothing once the server says there is no more.
 */
const LoadMoreRow = ({ status, hasMore, onLoadMore, label }: { status: PageStatus; hasMore: boolean; onLoadMore: () => void; label: string }) => {
    if (status === 'loading') {
        return <Box sx={{ p: 1.5, textAlign: 'center' }}><CircularProgress size={18} aria-label="Loading" /></Box>;
    }
    if (status === 'error') {
        return (
            <Box sx={{ p: 1.5, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 1 }}>
                <Typography variant="body2" color="text.secondary">Couldn't load recordings.</Typography>
                <Button size="small" onClick={onLoadMore}>Retry</Button>
            </Box>
        );
    }
    if (!hasMore) return null;
    return (
        <Box sx={{ p: 1, textAlign: 'center' }}>
            <Button size="small" onClick={onLoadMore}>{label}</Button>
        </Box>
    );
};

/**
 * A list fetched the first time a Browse tree node is expanded. Tracks loading and
 * failure so the node can show them, instead of sitting open and empty either way.
 */
function useLazyList<T>(url: string) {
    const [items, setItems] = useState<T[]>([]);
    const [status, setStatus] = useState<'idle' | 'loading' | 'loaded' | 'error'>('idle');

    const load = useCallback(() => {
        setStatus('loading');
        fetch(url)
            .then(res => {
                if (!res.ok) throw new Error(`HTTP ${res.status}`);
                return res.json();
            })
            .then((data: T[]) => { setItems(data); setStatus('loaded'); })
            .catch(err => { console.error(`Failed to load ${url}:`, err); setStatus('error'); });
    }, [url]);

    /** Call when the node is being expanded: loads once, and again after a failure. */
    const ensureLoaded = () => { if (status === 'idle' || status === 'error') load(); };

    return { items, setItems, status, load, ensureLoaded };
}

/** Loading / failed / empty row shown inside an expanded tree node. */
const NodeStatus = ({ status, empty, depth, onRetry }: { status: 'idle' | 'loading' | 'loaded' | 'error'; empty: boolean; depth: number; onRetry: () => void }) => {
    if (status === 'loading') {
        return (
            <Box sx={{ ...indent(depth), py: 1, display: 'flex', alignItems: 'center', gap: 1 }} role="status">
                <CircularProgress size={14} />
                <Typography variant="caption" color="text.secondary">Loading…</Typography>
            </Box>
        );
    }
    if (status === 'error') {
        return (
            <Box sx={{ ...indent(depth), py: 0.5, display: 'flex', alignItems: 'center', gap: 1 }} role="alert">
                <Typography variant="caption" color="error">Couldn't load this.</Typography>
                <Button size="small" onClick={onRetry}>Retry</Button>
            </Box>
        );
    }
    if (status === 'loaded' && empty) {
        return (
            <Typography variant="caption" color="text.secondary" sx={{ ...indent(depth), py: 1, display: 'block' }}>
                Nothing here.
            </Typography>
        );
    }
    return null;
};

const YearNode = ({ year, powerDmsDept, playingId, onPlay, onDelete, onFavoriteToggle }: { year: string; powerDmsDept: string | null } & LogNodeProps) => {
    const [open, setOpen] = useState(false);
    const list = useLazyList<string>(`/api/history/${year}/months`);
    const months = list.items;

    const handleToggle = () => {
        if (!open) list.ensureLoaded();
        setOpen(!open);
    };

    return (
        <>
            <ListItemButton onClick={handleToggle} sx={{ ...indent(1), ...rowBorder }}>
                <Folder sx={{ mr: 2, color: 'text.disabled', fontSize: 20 }} />
                <ListItemText primary={year} primaryTypographyProps={{ fontWeight: 'bold' }} />
                {open ? <ExpandLess /> : <ExpandMore />}
            </ListItemButton>
            <Collapse in={open} timeout="auto" unmountOnExit>
                <NodeStatus status={list.status} empty={months.length === 0} depth={2} onRetry={list.load} />
                <List component="div" disablePadding>
                    {months.map(month => (
                        <MonthNode key={month} year={year} month={month} powerDmsDept={powerDmsDept} playingId={playingId} onPlay={onPlay} onDelete={onDelete} onFavoriteToggle={onFavoriteToggle} />
                    ))}
                </List>
            </Collapse>
        </>
    );
};

const MonthNode = ({ year, month, powerDmsDept, playingId, onPlay, onDelete, onFavoriteToggle }: { year: string; month: string; powerDmsDept: string | null } & LogNodeProps) => {
    const [open, setOpen] = useState(false);
    const list = useLazyList<string>(`/api/history/${year}/${month}/days`);
    const days = list.items;

    const monthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    const monthName = monthNames[parseInt(month) - 1] || month;

    const handleToggle = () => {
        if (!open) list.ensureLoaded();
        setOpen(!open);
    };

    return (
        <>
            <ListItemButton onClick={handleToggle} sx={{ ...indent(2), ...rowBorder }}>
                <CalendarMonth sx={{ mr: 2, color: 'text.disabled', fontSize: 18 }} />
                <ListItemText primary={monthName} />
                {open ? <ExpandLess /> : <ExpandMore />}
            </ListItemButton>
            <Collapse in={open} timeout="auto" unmountOnExit>
                <NodeStatus status={list.status} empty={days.length === 0} depth={3} onRetry={list.load} />
                <List component="div" disablePadding>
                    {days.map(day => (
                        <DayNode key={day} year={year} month={month} day={day} powerDmsDept={powerDmsDept} playingId={playingId} onPlay={onPlay} onDelete={onDelete} onFavoriteToggle={onFavoriteToggle} />
                    ))}
                </List>
            </Collapse>
        </>
    );
};

const DayNode = ({ year, month, day, powerDmsDept, playingId, onPlay, onDelete, onFavoriteToggle }: { year: string; month: string; day: string; powerDmsDept: string | null } & LogNodeProps) => {
    const [open, setOpen] = useState(false);
    const list = useLazyList<Channel>(`/api/history/${year}/${month}/${day}/channels`);
    const channels = list.items;
    const [logExists, setLogExists] = useState<boolean | null>(null);

    // Check whether a PowerDMS daily log exists for this date
    useEffect(() => {
        if (!powerDmsDept) return;
        fetch(`/api/powerdms/check/${year}/${month}/${day}`)
            .then(res => res.json())
            .then(data => setLogExists(data.exists ?? false))
            .catch(() => setLogExists(false));
    }, [powerDmsDept, year, month, day]);

    const handleToggle = () => {
        if (!open) list.ensureLoaded();
        setOpen(!open);
    };

    const handleOpenDailyLog = (e: React.MouseEvent) => {
        e.stopPropagation();
        window.open(`/api/powerdms/daily-log/${year}/${month}/${day}`, '_blank');
    };

    return (
        <>
            <ListItemButton onClick={handleToggle} sx={{ ...indent(3), ...rowBorder }}>
                <Today sx={{ mr: 2, color: 'text.disabled', fontSize: 18 }} />
                <ListItemText primary={`Day ${day}`} />
                {powerDmsDept && logExists !== null && (
                    <Tooltip title={logExists ? 'Open PowerDMS Daily Log' : 'No daily log available for this date'} arrow>
                        <span>
                            <IconButton
                                size="small"
                                onClick={handleOpenDailyLog}
                                disabled={!logExists}
                                sx={{ mr: 0.5 }}
                                aria-label="Open PowerDMS daily log"
                            >
                                <Article sx={{ color: logExists ? status.info : 'text.disabled', fontSize: 18 }} />
                            </IconButton>
                        </span>
                    </Tooltip>
                )}
                {open ? <ExpandLess /> : <ExpandMore />}
            </ListItemButton>
            <Collapse in={open} timeout="auto" unmountOnExit>
                <NodeStatus status={list.status} empty={channels.length === 0} depth={4} onRetry={list.load} />
                <List component="div" disablePadding>
                    {channels.map((ch, idx) => (
                        <ChannelNode
                            key={`${ch.frequency}-${idx}`}
                            year={year} month={month} day={day}
                            channel={ch}
                            playingId={playingId} onPlay={onPlay} onDelete={onDelete} onFavoriteToggle={onFavoriteToggle}
                        />
                    ))}
                </List>
            </Collapse>
        </>
    );
};

const ChannelNode = ({ year, month, day, channel, playingId, onPlay, onDelete, onFavoriteToggle }: { year: string; month: string; day: string; channel: Channel } & LogNodeProps) => {
    const [open, setOpen] = useState(false);
    const list = useLazyList<CallLog>(
        `/api/history/filter?year=${year}&month=${month}&day=${day}&alphaTag=${encodeURIComponent(channel.alphaTag)}&frequency=${channel.frequency}`,
    );
    const logs = list.items;
    const setLogs = list.setItems;

    const handleToggle = () => {
        if (!open) list.ensureLoaded();
        setOpen(!open);
    };

    const handleDelete = (id: string) => {
        setLogs(prev => prev.filter(log => log.id !== id));
        onDelete(id);
    };

    return (
        <>
            <ListItemButton onClick={handleToggle} sx={{ ...indent(4), ...rowBorder, bgcolor: open ? alpha('#ffffff', 0.02) : 'transparent' }}>
                <Radio sx={{ mr: 2, color: 'primary.main', fontSize: 16 }} />
                <ListItemText
                    primary={channel.alphaTag || `${channel.frequency} MHz`}
                    secondary={channel.alphaTag ? `${channel.frequency} MHz` : null}
                    primaryTypographyProps={{ fontSize: '0.9rem', color: 'text.primary' }}
                    secondaryTypographyProps={{ fontSize: '0.75rem', color: 'text.secondary' }}
                />
                {open ? <ExpandLess /> : <ExpandMore />}
            </ListItemButton>
            <Collapse in={open} timeout="auto" unmountOnExit>
                <NodeStatus status={list.status} empty={logs.length === 0} depth={5} onRetry={list.load} />
                <List component="div" disablePadding>
                    {logs.map(log => (
                        <LogItem key={log.id} log={log} tree playingId={playingId} onPlay={onPlay} onDelete={handleDelete} onFavoriteToggle={onFavoriteToggle} />
                    ))}
                </List>
            </Collapse>
        </>
    );
};

type LogItemProps = { log: CallLog; tree?: boolean } & LogNodeProps;

const LogItemInner = ({ log, tree, playingId, onPlay, onDelete, onFavoriteToggle }: LogItemProps) => {
    const [isFavorite, setIsFavorite] = useState(log.isFavorite ?? false);
    const highlight = useContext(HighlightContext);
    const nameFor = useContext(AliasContext);
    const requestDelete = useContext(DeleteContext);
    const itemRef = useRef<HTMLLIElement>(null);

    // Per-channel SRC/TG display names (fall back to the raw number; raw kept on hover).
    const at = log.alphaTag, fq = log.frequency;

    // Scroll into view and briefly flash when this log is the highlight target.
    // The flash is driven imperatively via the Web Animations API so the effect
    // only touches the DOM (no setState) — it returns to the base style on its own.
    useEffect(() => {
        if (!highlight || highlight.id !== log.id) return;
        const el = itemRef.current;
        if (!el) return;
        // Scroll ONLY the log's own scroll container — using el.scrollIntoView()
        // would also scroll ancestor containers (pushing the fire tone-out panel
        // out of view with no way to scroll back). Center the row within it.
        const container = el.closest('[data-log-scroll]') as HTMLElement | null;
        if (container) {
            const cRect = container.getBoundingClientRect();
            const eRect = el.getBoundingClientRect();
            const delta = (eRect.top - cRect.top) - (container.clientHeight - el.clientHeight) / 2;
            container.scrollBy({ top: delta, behavior: 'smooth' });
        }
        if (typeof el.animate !== 'function') return;
        const flash = alpha(HIGHLIGHT, 0.28);
        const anim = el.animate(
            [
                { backgroundColor: flash, boxShadow: `inset 3px 0 0 ${HIGHLIGHT}` },
                { backgroundColor: flash, boxShadow: `inset 3px 0 0 ${HIGHLIGHT}`, offset: 0.7 },
                { backgroundColor: 'rgba(0,0,0,0)', boxShadow: `inset 3px 0 0 ${alpha(HIGHLIGHT, 0)}` },
            ],
            { duration: 2000, easing: 'ease-out' }
        );
        return () => anim.cancel();
    }, [highlight, log.id]);

    const handleFavoriteClick = (e: React.MouseEvent) => {
        e.stopPropagation();
        const newValue = !isFavorite;
        setIsFavorite(newValue);
        fetch(`/api/history/${log.id}/favorite`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ isFavorite: newValue })
        })
            .then(() => onFavoriteToggle())
            .catch(err => {
                console.error('Failed to update favorite:', err);
                setIsFavorite(!newValue);
            });
    };

    return (
        <React.Fragment>
            <ListItem
                ref={itemRef}
                sx={{
                    // Flat contexts (Recent/Favorites/Search) left-justify; only the
                    // history tree indents rows to sit under their channel node.
                    ...(tree ? indent(5) : { pl: 2 }),
                    pr: 2,
                    py: 1,
                    '& .MuiListItemSecondaryAction-root': { right: 8 },
                    bgcolor: alpha('#000000', 0.2),
                    animation: 'rowEnter 220ms ease',
                }}
                secondaryAction={
                    <Box sx={{ display: 'flex', gap: 0.5 }}>
                        <IconButton size="small" onClick={handleFavoriteClick} aria-label={isFavorite ? 'Remove from favorites' : 'Add to favorites'} title={isFavorite ? 'Remove from favorites' : 'Add to favorites'}>
                            {isFavorite
                                ? <Star sx={{ color: GOLD, fontSize: 18 }} />
                                : <StarBorder sx={{ color: 'text.disabled', fontSize: 18 }} />
                            }
                        </IconButton>
                        {log.audio_path && (
                            <>
                                <IconButton size="small" onClick={() => onPlay(log.id, log.audio_path!, log.duration, log.alphaTag)} aria-label={playingId === log.id ? 'Stop playback' : 'Play recording'}>
                                    {playingId === log.id
                                        ? <StopCircle sx={{ color: 'error.main', fontSize: 20 }} />
                                        : <PlayCircleOutline sx={{ color: 'primary.main', fontSize: 20 }} />
                                    }
                                </IconButton>
                                <IconButton
                                    size="small"
                                    component="a"
                                    href={`/audio/${log.audio_path}`}
                                    download={log.audio_path.split('/').pop() || 'recording.wav'}
                                    aria-label="Download recording"
                                    title="Download recording"
                                >
                                    <Download sx={{ color: 'primary.main', fontSize: 20, opacity: 0.7 }} />
                                </IconButton>
                            </>
                        )}
                        <IconButton
                            size="small"
                            onClick={() => (requestDelete ? requestDelete(log, () => onDelete(log.id)) : onDelete(log.id))}
                            aria-label="Delete recording"
                            title="Delete recording"
                        >
                            <Delete sx={{ color: 'text.disabled', fontSize: 18 }} />
                        </IconButton>
                    </Box>
                }
            >
                <ListItemText
                    sx={{ pr: 14 }}
                    primary={
                        <Box display="flex" alignItems="center" justifyContent="space-between" gap={1}>
                            <Typography sx={{ color: 'text.primary', fontWeight: 'bold', fontSize: '0.85rem', lineHeight: 1.2 }}>
                                {log.alphaTag}
                            </Typography>
                            <Box display="flex" alignItems="center" gap={0.75} flexShrink={0}>
                                {log.detectedTone && (
                                    log.detectedTone === 'EMRG' ? (
                                        <Typography variant="caption" sx={{
                                            color: '#fff', fontWeight: 'bold', fontSize: '9px',
                                            bgcolor: status.error, border: `1px solid ${alpha(status.error, 0.6)}`,
                                            px: 0.6, py: 0.1, borderRadius: 0.5, letterSpacing: 0.5,
                                        }}>
                                            ! EMRG
                                        </Typography>
                                    ) : (
                                        <Typography variant="caption" sx={{ color: status.error, fontWeight: 'bold', fontSize: '9px', border: `1px solid ${status.error}`, px: 0.5, borderRadius: 0.5 }}>
                                            {log.detectedTone}
                                        </Typography>
                                    )
                                )}
                                {log.duration && (
                                    <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.7rem', ...monoFont }}>
                                        {log.duration.toFixed(1)}s
                                    </Typography>
                                )}
                            </Box>
                        </Box>
                    }
                    secondary={
                        <Box component="span" display="block">
                            <Box display="flex" alignItems="center" gap={0.75} mt={0.3} flexWrap="wrap">
                                <Typography variant="caption" sx={{ ...monoFont, color: 'text.disabled', fontSize: '0.68rem' }}>
                                    {formatLogTime(log.timestamp)}
                                </Typography>
                                <Typography variant="caption" sx={{ color: 'surface.borderStrong', fontSize: '0.65rem' }}>·</Typography>
                                <Typography variant="caption" sx={{ ...monoFont, color: 'text.disabled', fontSize: '0.68rem' }}>
                                    {log.frequency.toFixed(3)} MHz
                                </Typography>
                                {(log.speakerChain || log.sourceID || log.targetID) && (
                                    <>
                                        <Typography variant="caption" sx={{ color: 'surface.borderStrong', fontSize: '0.65rem' }}>·</Typography>
                                        <Box display="flex" alignItems="center" gap={0.4}>
                                            {log.speakerChain ? (
                                                <>
                                                    <Tooltip title={log.speakerChain}>
                                                        <Typography variant="caption" sx={{ color: status.warn, fontSize: '0.7rem', ...monoFont, fontWeight: 'bold' }}>
                                                            {chainLabel(nameFor, log.speakerChain, at, fq)}
                                                        </Typography>
                                                    </Tooltip>
                                                    {log.targetID && (
                                                        <>
                                                            <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.65rem' }}>→</Typography>
                                                            <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.65rem', ...monoFont }}>TG</Typography>
                                                            <Tooltip title={String(log.targetID)}>
                                                                <Typography variant="caption" sx={{ color: status.info, fontSize: '0.7rem', ...monoFont, fontWeight: 'bold' }}>
                                                                    {tgLabel(nameFor, log.targetID, at, fq)}
                                                                </Typography>
                                                            </Tooltip>
                                                        </>
                                                    )}
                                                </>
                                            ) : (
                                                <>
                                                    <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.65rem', ...monoFont }}>SRC</Typography>
                                                    <Tooltip title={log.sourceID != null ? String(log.sourceID) : ''}>
                                                        <Typography variant="caption" sx={{ color: status.warn, fontSize: '0.7rem', ...monoFont, fontWeight: 'bold' }}>
                                                            {srcLabel(nameFor, log.sourceID, at, fq)}
                                                        </Typography>
                                                    </Tooltip>
                                                    <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.65rem' }}>→</Typography>
                                                    <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.65rem', ...monoFont }}>TG</Typography>
                                                    <Tooltip title={log.targetID != null ? String(log.targetID) : ''}>
                                                        <Typography variant="caption" sx={{ color: status.info, fontSize: '0.7rem', ...monoFont, fontWeight: 'bold' }}>
                                                            {tgLabel(nameFor, log.targetID, at, fq)}
                                                        </Typography>
                                                    </Tooltip>
                                                </>
                                            )}
                                        </Box>
                                    </>
                                )}
                                {log.lat && log.lat !== 0 && (
                                    <>
                                        <Typography variant="caption" sx={{ color: 'surface.borderStrong', fontSize: '0.65rem' }}>·</Typography>
                                        <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.68rem', ...monoFont }}>
                                            {log.lat.toFixed(3)}, {log.lon?.toFixed(3)}
                                        </Typography>
                                    </>
                                )}
                            </Box>
                            {log.insight?.isIncident && (
                                <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75, mt: 0.4, minWidth: 0 }}>
                                    <Chip
                                        size="small"
                                        label={callTypeLabel(log.insight.callType)}
                                        sx={{
                                            height: 18, fontSize: '0.65rem', fontWeight: 700, flexShrink: 0,
                                            bgcolor: 'surface.overlay', color: 'text.primary',
                                            borderLeft: '3px solid', borderLeftColor: CALL_GROUPS[groupOf(log.insight.callType)].color,
                                        }}
                                    />
                                    <Typography variant="caption" noWrap sx={{ color: 'text.primary', fontSize: '0.7rem', minWidth: 0 }}>
                                        {[log.insight.summary, log.insight.matchedAddress || log.insight.address].filter(Boolean).join(' · ')}
                                    </Typography>
                                </Box>
                            )}
                            {log.transcription && (
                                <Typography variant="body2" sx={{ color: 'text.secondary', fontStyle: 'italic', fontSize: '0.72rem', mt: 0.4, lineHeight: 1.3 }}>
                                    "{log.transcription}"
                                </Typography>
                            )}
                        </Box>
                    }
                    secondaryTypographyProps={{ component: 'div' }}
                />
            </ListItem>
            <Divider component="li" sx={{ borderColor: 'surface.border' }} />
        </React.Fragment>
    );
};

/**
 * A row is a dozen MUI components, and a list holds up to 100 of them, so rows
 * must not re-render unless something they show changed. `playingId` is compared
 * by what it means to this row (is *this* recording playing?), so starting
 * playback re-renders two rows rather than all of them.
 */
const LogItem = memo(LogItemInner, (a, b) =>
    a.log === b.log &&
    a.tree === b.tree &&
    a.onPlay === b.onPlay &&
    a.onDelete === b.onDelete &&
    a.onFavoriteToggle === b.onFavoriteToggle &&
    (a.playingId === a.log.id) === (b.playingId === b.log.id));

// Memoized: the app re-renders on every scanner state update (up to 10 per
// second), and none of this panel's props change with those.
export default memo(TransmissionLog);
