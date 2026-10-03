# Sync flow cheatsheet

What the **old Connector** does today, from code (2026-09-30). Details and evidence: `SYNC_ARCHITECTURE_AUDIT.md`.

## Who does what

- **The sync runs in the webview** (TypeScript), not in Rust. Rust starts the 1C adapter and does nothing else for sync.
- **The 1C adapter** (`main.os`, port 55899) answers reads over HTTP, one request at a time per process. It does no hashing and no change detection.
- **The event log is switched off.** The watcher exists but `isEventLogSyncEnabled()` returns `false`. Change detection today is guesswork with backstops (below).
- **The backend** (`backend/1c`) stores rows and trusts the client for everything else, including deletes.

## When something changes in 1C, how does it reach the backend?

1. **A timer picks the base.** Every 60 s per base (poller), or every 4 min for the reports lane, or a button, or a cloud `sync_now` command. One run per base at a time (a mutex).
2. **Count check.** Asks the adapter and the backend how many rows each table has (`/counts`). A table whose counts differ is selected. A change that keeps the count the same (an edit) is **not** noticed here.
3. **Cursor read.** Documents and registers are read from the last saved date forward (`cursorDate` + skip), 500 rows per page. Catalogs are re-read **entirely** if their count changed.
4. **Gap guard.** If the counts still disagree after the cursor read, it looks for back-dated rows by comparing ids (documents) or keys (registers). Turned off while a backfill or cold read runs.
5. **Sweep, every 30 min or more.** Re-reads a small projection of documents (`posted`, `deletionMark`, `date`, total) and re-uploads the ones that changed, at most 25 per sweep. A document missing from two complete scans in a row is deleted in the cloud (`prune-missing`). Then `reconcile-recorder` drops register rows that no longer exist for changed documents, **only for registers in that run's table list**.
6. **Upload.** Each page goes straight out: `POST /api/v2/entity/upload`, multipart, up to 34 MB, split in half on 413. Rows carry `__rowKey` (the GUID in `id`, or `recorderRef#lineNo` for registers).
7. **Save the cursor** only after the table is finished and uploaded (not per page, except catalog cold reads).

What it **cannot** see reliably: information-register changes, an edit that keeps a document's total, a repost that only changes register amounts, back-dated rows during a backfill.

## First sync of a base

- **Documents, accumulation and accounting registers:** reads only the **newest 3 months** first, sets the cursor, then walks history backwards **one 3-month slice per cycle** until it reaches the oldest data. The accounting register is night-only by default.
- **Catalogs:** keyset walk by `Ссылка`, 20 pages of 500 per cycle, position saved after each page.
- **Charts of accounts and information registers:** one full read.
- **Stock:** replaced whole, every cycle.
- **Order:** reference catalogs, then documents, then movements. There is **no** step that records a change marker before the cold read.

## What the backend does with a row

- **Has a key** (`__rowKey`, or a GUID `id`): updated **in place**. Last write wins. No version check.
- **No key** (information registers): matched by content hash, so an **edited row becomes a second row**.
- Duplicate-key errors are **swallowed**: two rows with the same key in one batch keep the first; the insert count is over-reported.
- **Deletes are never inferred.** Only `prune-missing` (refuses over 5% of the table, or an empty table) and `reconcile-recorder` delete anything. Neither updates counts or caches.
- No transactions. A document and its movements arrive as separate requests.

## The rewrite's first engine (milestone 5.9) in one paragraph

As of 2026-09-30; that engine was removed on 2026-10-02 (D48). The current engine, `src/OneC.Sync`, is
described in `SYNC_ENGINE_ARCHITECTURE.md` and was designed to fix the four defects below.

Takes the event-log **tail first**, cold-reads each table (500-row pages, state saved after each accepted page), then **replays the feed**: changed documents are re-read by id, deleted ones pruned, and Post/Unpost re-reads and reconciles the recorder's movements. That ordering handles changes made during a cold read correctly. But it only ever talked to its own stub, the desktop app never starts it, and it has four known defects: information-register events reset the whole table, a non-periodic information register loops forever, accumulation rows have no key, and a base whose log it cannot read never syncs at all.

## Five things to remember

1. Row identity = GUID, or `recorderRef#lineNo`. Never `Регистратор`.
2. Deletes are the client's job and must be explicit.
3. Checkpoint only after the backend accepted the data.
4. The old sync's heuristics exist because it has no change feed; a feed-based design needs none of them.
5. Stale register movements are the worst silent risk in the old sync.
