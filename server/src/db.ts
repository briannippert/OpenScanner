import Database from 'better-sqlite3';
import path from 'path';
import fs from 'fs';
import { Channel, CHANNELS } from './models';

const DB_PATH = path.join(__dirname, '../data/openscanner.db');
const RECORDINGS_PATH = path.join(__dirname, '../data/recordings');

// Ensure directories exist
if (!fs.existsSync(path.join(__dirname, '../data'))) {
    fs.mkdirSync(path.join(__dirname, '../data'));
}
if (!fs.existsSync(RECORDINGS_PATH)) {
    fs.mkdirSync(RECORDINGS_PATH);
}

const db = new Database(DB_PATH);

// Initialize schema
db.exec(`
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
        duration REAL
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
        tag TEXT
    );
`);

// Seed Channels if empty
const count = db.prepare('SELECT count(*) as count FROM channels').get() as { count: number };
if (count.count === 0) {
    const insert = db.prepare(`
        INSERT INTO channels (frequency, license, type, tone, alphaTag, description, mode, tag)
        VALUES (@frequency, @license, @type, @tone, @alphaTag, @description, @mode, @tag)
    `);
    const insertMany = db.transaction((channels: Channel[]) => {
        for (const channel of channels) insert.run(channel);
    });
    insertMany(CHANNELS);
    console.log(`[DB] Seeded ${CHANNELS.length} initial channels.`);
}

export interface DBTransmission {
    id: string;
    timestamp: string;
    frequency: number;
    alphaTag: string;
    description: string;
    lat?: number;
    lon?: number;
    alt?: number;
    audio_path?: string;
    duration?: number;
}

export const getAllChannels = (): Channel[] => {
    return db.prepare('SELECT * FROM channels ORDER BY frequency ASC').all() as Channel[];
};

export const addChannel = (channel: Channel): number => {
    const stmt = db.prepare(`
        INSERT INTO channels (frequency, license, type, tone, alphaTag, description, mode, tag)
        VALUES (@frequency, @license, @type, @tone, @alphaTag, @description, @mode, @tag)
    `);
    const info = stmt.run(channel);
    return Number(info.lastInsertRowid);
};

export const updateChannel = (channel: Channel) => {
    const stmt = db.prepare(`
        UPDATE channels 
        SET frequency=@frequency, license=@license, type=@type, tone=@tone, 
            alphaTag=@alphaTag, description=@description, mode=@mode, tag=@tag
        WHERE id=@id
    `);
    stmt.run(channel);
};

export const deleteChannel = (id: number) => {
    const stmt = db.prepare('DELETE FROM channels WHERE id = ?');
    stmt.run(id);
};

export const saveTransmission = (t: DBTransmission) => {
    const stmt = db.prepare(`
        INSERT INTO transmissions (id, timestamp, frequency, alphaTag, description, lat, lon, alt, audio_path, duration)
        VALUES (?, datetime('now'), ?, ?, ?, ?, ?, ?, ?, ?)
    `);
    stmt.run(t.id, t.frequency, t.alphaTag, t.description, t.lat || null, t.lon || null, t.alt || null, t.audio_path || null, t.duration || null);
};

export const getHistory = (limit = 100): DBTransmission[] => {
    const stmt = db.prepare(`SELECT * FROM transmissions ORDER BY timestamp DESC LIMIT ?`);
    return stmt.all(limit) as DBTransmission[];
};

export const deleteTransmission = (id: string) => {
    const stmt = db.prepare(`DELETE FROM transmissions WHERE id = ?`);
    stmt.run(id);
};

export default db;
