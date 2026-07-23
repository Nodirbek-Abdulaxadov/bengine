-- SQLite dialect sample
CREATE TABLE notes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    title TEXT NOT NULL,
    body TEXT,
    pinned INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE tags (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    note_id INTEGER NOT NULL REFERENCES notes (id) ON DELETE CASCADE,
    label TEXT NOT NULL
);

CREATE INDEX ix_tags_note ON tags (note_id);
CREATE UNIQUE INDEX ux_tags_label ON tags (label);
