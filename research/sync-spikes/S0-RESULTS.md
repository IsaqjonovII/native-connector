# Sync — S0 spike results (2026-09-30)

All read-only except Q1b/Q4, which used one test-owned document per base
(`AIBA_REWRITE_S0_<time>`, created, changed, deleted by the spike; 0 left, checked with
`FindOwned`). Raw output in `measurements/sync-s0/`.

Tools: `research/sync-spikes/LogShapes` (reads .lgp files, prints the Data field shape
per metadata kind and event), `OneC.Host spike-versions`, `OneC.Host spike-lifecycle`
(`src/OneC.Host/SyncSpikes.cs`).

**Verdict: no architectural assumption is contradicted.** Two details are added to the
design (the `TotalsMaxPeriodUpdate` event and recorder-type resolution), both inside S3/S6.

## Q1 — `ВерсияДанных` in queries; what changes it

Selectable in a query on 8.3.15 (KAN server, bilim file) and 8.3.18 (bilim), for documents and
catalogs. Arrives over COM as a base64 string of 8 bytes (`AAAAAAAAAAE=`).

Lifecycle, same result on bilim and KAN (`lifecycle-*.txt`):

| step | ВерсияДанных | document log events |
|---|---|---|
| create (clone) | new | New |
| write with no change | **changed** | Update |
| post | changed | Update + Post |
| repost | changed | Update + Post |
| unpost | changed | Update + Unpost |
| set deletion mark | changed | Update |
| clear deletion mark | changed | Update |
| delete | row gone | Delete |

So every write bumps the version, posting included (the §12 fallback premise holds). Because a
write with no change also bumps it, version equality proves "unchanged", but a new version does
not prove a synced field changed — no-op suppression (§6) still works for replays and repeated
events, which is its purpose.

## Q2 — version-scan speed (keyset `Ссылка > &Last`, 5 000 per page)

| base | table | rows | time | rows/s | host RAM delta |
|---|---|---|---|---|---|
| KAN (server, 8.3.15) | Документ.РеализацияТоваровУслуг | 89 988 | 9.6 s | 9 415 | +11 MB private |
| KAN | Справочник.Номенклатура | 37 527 | 1.7 s | 21 997 | +4 MB |
| bilim (file, 8.3.18) | Документ.ПоступлениеТоваровУслуг | 68 | 0.07 s | — | 0 |

1 M documents ≈ 2 min per table on KAN, against ~220 docs/s for a full re-read with tabular
sections (≈ 75 min). The per-row cost is the `XMLСтрока(ref)` call; S3's op can drop it if needed.
bilim has no table big enough to time; file-base speed is re-measured in S11 on a larger copy.

## Q3 — what register events carry (KAN: whole September log, 2.7 GB, 5.9 M records, read in 23 s)

| kind | Data field | count (Sept, KAN) |
|---|---|---|
| РегистрБухгалтерии Update | `{"R", type:guid}` = recorder | 1 324 |
| РегистрНакопления Update | `{"R", …}` recorder | 10 465 |
| РегистрРасчета / Перерасчет Update | `{"R", …}` recorder | 21 |
| РегистрСведений, recorder-subordinate (ЦеныНоменклатуры, СрокГодносты, ТМЦ_принтуз, …) | `{"R", …}` recorder | 3 935 |
| РегистрСведений, independent (МИКО_*, ВерсииОбъектов, GG_*, …) | `{"U"}` — nothing | 18 698 (+ 341 250 МИКО call history) |
| Константа Update | `{"U"}` | 4 |
| РегистрБухгалтерии/Накопления **TotalsMaxPeriodUpdate** | `{"D", date}` | Kanstik 64 |

- Every recorder-register event carried a recorder ref; **15 733 of 15 733** on KAN had a
  document event for the same ref in the same transaction (bilim 10 800/10 800, Kanstik 1/1,
  signum 2/2). The ref's type code (`214:`) is the recorder's type, not a metadata name.
- Register events are **not a complete signal**: posting the test document on KAN logged only
  one of its registers; unpost logged none; delete logged 115 register updates (every register
  the recorder could have). The design never relies on them alone — Post/Unpost/Delete of the
  document already schedule `SyncRecorder` / `DeleteObject`, which reconcile every configured
  register (§9, §10). Register events stay a secondary trigger (R-9: registers of a document type
  not in the config).
- Independent information registers give nothing to key on: confirms §8 (coalesced refresh).
  Volume matters for D-6: KAN's call-history registers take ~4 writes/min each around the clock.

Design additions: `TotalsMaxPeriodUpdate` (`{"D",…}`) is not a data change — the reader already
ignores it (`LogFormat.DataEventKind` knows five events); S6 adds a test. A register event whose
recorder type is unknown resolves the recorder by the same-transaction document event (always
present in the sample), else by probing the register's recorder types in S3's by-id op.

## Q4 — deletion mark, rename, restore

Deletion mark set and cleared: an `Update` event, version bumps (table above). A catalog rename
is an object write (`Update`, same mechanism; not written on a real catalog — test documents
only). Infobase restore appears as `_$InfoBase$_.RestoreStart/Finish` in KAN's dictionary; the log
instance GUID survives a restore, so a restore must also trigger Recovery: S6 treats these two
events as a reset signal.

## Q5 — log retention

8.3's file-format log has no automatic retention: files stay until an administrator reduces the
log. Evidence: KAN keeps every month since its restore (July, 3 files, 9.8 GB), bilim since
April (60+ files). The base's setting is not readable without admin rights, so the planner
cannot compare against it; §11's "drain the feed during long snapshots" becomes unconditional
(cheap: the feed is gated by file size). A manual reduction shows as `cursor_file_gone` → Recovery.
Split period varies per base (KAN monthly, bilim ad hoc hourly/daily files).

## Q6 — "start of the first file" cursor

Not expressible today: `EventLogReader.Read(dir, null)` returns the tail, and with no `.lgp`
file it returns no cursor at all, so a base whose first log file appears after the handshake
would lose its first events. S6 adds a start cursor (`LgfGuid` + empty file, offset 0 = "from
the first file") — a reader addition, no design change.

## Q7 — GUID case of cloud rows

Deferred to S12 (needs a dev backend login; affects only the D-3 rebuild list).
(`logshapes-kan-inforeg.txt` filters to information registers only, so its pairing line counts 0 by construction — use `logshapes-kan-sept.txt` for pairing.)
