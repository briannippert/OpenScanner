import sqlite3
import os

db_path = './data/openscanner.db'
if os.path.exists(db_path):
    conn = sqlite3.connect(db_path)
    cursor = conn.cursor()
    cursor.execute("DELETE FROM transmissions")
    conn.commit()
    conn.close()
    print("Transmissions table cleared.")
else:
    print("Database not found.")

# Also clear audio files
audio_dir = './data/recordings'
if os.path.exists(audio_dir):
    for f in os.listdir(audio_dir):
        os.remove(os.path.join(audio_dir, f))
    print("Audio recordings deleted.")
