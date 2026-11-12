CREATE TABLE IF NOT EXISTS transmissions (
    id TEXT PRIMARY KEY,
    timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
    frequency REAL,
    alphaTag TEXT,
    description TEXT,
    lat REAL,
    lon REAL,
    alt REAL,
    audio_path TEXT,
    duration REAL,
    transcription TEXT,
    detectedTone TEXT,
    sourceID INTEGER,
    targetID INTEGER
);

CREATE TABLE IF NOT EXISTS fire_tones (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT,
    frequencyA REAL,
    frequencyB REAL,
    description TEXT
);

CREATE TABLE IF NOT EXISTS aliases (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    kind TEXT NOT NULL,          -- 'SRC' | 'TG'
    value INTEGER NOT NULL,
    name TEXT NOT NULL,
    alphaTag TEXT NOT NULL,
    frequency REAL NOT NULL,
    UNIQUE(kind, value, alphaTag, frequency)
);

CREATE TABLE IF NOT EXISTS channels (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    frequency REAL UNIQUE,
    license TEXT,
    type TEXT,
    tone TEXT,
    alphaTag TEXT,
    description TEXT,
    mode TEXT,
    tag TEXT,
    lat REAL,
    lon REAL,
    range REAL,
    avoid INTEGER DEFAULT 0,
    dmrSlot INTEGER,
    dmrColorCode INTEGER,
    dmrTalkgroup INTEGER,
    sdrDeviceId TEXT
);

-- One row per RTL-SDR dongle OpenScanner has seen. The id is stable across replugs: the
-- dongle's serial when it is unique among the attached devices, otherwise its USB index.
CREATE TABLE IF NOT EXISTS sdr_devices (
    id TEXT PRIMARY KEY,
    label TEXT NOT NULL DEFAULT '',
    serial TEXT NOT NULL DEFAULT '',
    product TEXT NOT NULL DEFAULT '',
    enabled INTEGER NOT NULL DEFAULT 1,
    ppm REAL NOT NULL DEFAULT 0,
    gainAnalog REAL NOT NULL DEFAULT 0,
    gainDigital REAL NOT NULL DEFAULT 0,
    gainMixed REAL NOT NULL DEFAULT 0,
    sortOrder INTEGER NOT NULL DEFAULT 0
);
