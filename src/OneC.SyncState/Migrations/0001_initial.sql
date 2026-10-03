-- Sync Engine v2 state, schema version 1 (SYNC_V2_ARCHITECTURE §13).
-- Changes from the §13 draft, made while building S1:
--   work_items.generation   bumped by every merge, so completing an item that was merged into
--                           while it ran keeps it for another run instead of deleting it;
--   work_items.id           is the FIFO order (a merge keeps the row), replacing first_seen_seq;
--   object_versions.seen_run the verify pass stamps every key 1C still has; keys left with an
--                           older run are gone. 1C orders refs by its own byte order, not the GUID
--                           text, so a sorted streaming merge would be wrong — this needs no order;
--   register_rows.seen_run  the same for an independent register's refresh (§8).

CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

CREATE TABLE sync_bases (
  base_id        TEXT PRIMARY KEY,
  target_kind    TEXT NOT NULL,
  connection_id  TEXT NOT NULL,
  company_id     TEXT,
  mode           TEXT NOT NULL,
  mode_reason    TEXT,
  paused_by_user INTEGER NOT NULL DEFAULT 0,
  config_hash    TEXT NOT NULL,
  updated_at     TEXT NOT NULL
);

CREATE TABLE sync_tables (
  base_id      TEXT NOT NULL,
  table_name   TEXT NOT NULL,
  metadata     TEXT NOT NULL,
  family       TEXT NOT NULL,
  key_strategy TEXT NOT NULL,
  enabled      INTEGER NOT NULL,
  PRIMARY KEY (base_id, table_name)
);

CREATE TABLE feed_cursors (
  base_id           TEXT PRIMARY KEY,
  cursor            TEXT,
  cursor_at         TEXT,
  last_event_at     TEXT,
  log_path          TEXT,
  last_reset_reason TEXT
);

CREATE TABLE snapshot_slices (
  base_id     TEXT NOT NULL,
  table_name  TEXT NOT NULL,
  run_id      INTEGER NOT NULL,
  slice_no    INTEGER NOT NULL,
  from_value  TEXT,
  to_value    TEXT,
  cursor      TEXT,
  cursor_skip INTEGER,
  rows_done   INTEGER NOT NULL DEFAULT 0,
  done        INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (base_id, table_name, run_id, slice_no)
);

CREATE TABLE work_items (
  id              INTEGER PRIMARY KEY,
  base_id         TEXT NOT NULL,
  kind            TEXT NOT NULL,
  table_name      TEXT NOT NULL,
  object_key      TEXT NOT NULL,
  flags           INTEGER NOT NULL,
  priority        INTEGER NOT NULL,
  generation      INTEGER NOT NULL DEFAULT 0,
  attempts        INTEGER NOT NULL DEFAULT 0,
  next_attempt_at TEXT,
  lease_until     TEXT,
  last_error      TEXT,
  UNIQUE (base_id, table_name, object_key)
);
CREATE INDEX work_items_claim ON work_items (base_id, priority, id);

CREATE TABLE object_versions (
  base_id      TEXT NOT NULL,
  table_name   TEXT NOT NULL,
  object_key   TEXT NOT NULL,
  data_version TEXT,
  synced_at    TEXT NOT NULL,
  seen_run     INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (base_id, table_name, object_key)
) WITHOUT ROWID;

CREATE TABLE object_partitions (
  base_id      TEXT NOT NULL,
  object_key   TEXT NOT NULL,
  partition_id TEXT NOT NULL,
  PRIMARY KEY (base_id, object_key, partition_id)
) WITHOUT ROWID;

CREATE TABLE register_rows (
  base_id    TEXT NOT NULL,
  table_name TEXT NOT NULL,
  row_key    TEXT NOT NULL,
  row_hash   TEXT NOT NULL,
  seen_run   INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (base_id, table_name, row_key)
) WITHOUT ROWID;

CREATE TABLE dead_letters (
  id              INTEGER PRIMARY KEY,
  base_id         TEXT NOT NULL,
  work_kind       TEXT NOT NULL,
  table_name      TEXT NOT NULL,
  object_key      TEXT NOT NULL,
  category        TEXT NOT NULL,
  error           TEXT NOT NULL,
  http_status     INTEGER,
  attempts        INTEGER NOT NULL,
  first_failed_at TEXT NOT NULL,
  last_failed_at  TEXT NOT NULL,
  next_retry_at   TEXT,
  payload_hint    TEXT
);
CREATE INDEX dead_letters_base ON dead_letters (base_id, category);

CREATE TABLE unmapped_orgs (
  base_id       TEXT NOT NULL,
  org_ref       TEXT NOT NULL,
  table_name    TEXT NOT NULL,
  rows_seen     INTEGER NOT NULL,
  first_seen_at TEXT NOT NULL,
  last_seen_at  TEXT NOT NULL,
  PRIMARY KEY (base_id, org_ref, table_name)
);

CREATE TABLE sync_runs (
  id             INTEGER PRIMARY KEY,
  base_id        TEXT NOT NULL,
  kind           TEXT NOT NULL,
  started_at     TEXT NOT NULL,
  finished_at    TEXT,
  outcome        TEXT,
  rows_read      INTEGER,
  rows_uploaded  INTEGER,
  bytes_uploaded INTEGER,
  error          TEXT
);
CREATE INDEX sync_runs_started ON sync_runs (started_at);
