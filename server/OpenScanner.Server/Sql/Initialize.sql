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
    range REAL
);
