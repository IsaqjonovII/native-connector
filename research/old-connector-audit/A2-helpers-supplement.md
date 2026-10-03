# A supplement — findings of A's helper audits that did not reach A-frontend.md

Source: two helper audits of D:\aiba\connector (release @ cfab3a1), read-only. Paths relative to apps/app/src. Verify before relying on a single line.

## App shell / providers / auth (helper "app shell")
- Provider tree: app/layout.tsx:31-63 — Observability(Sentry) > Theme > Query > DevSettings > Runtime > Adapter(+UzAdapter) > AppUpdate > ModeSwitcher > Auth > OneCSyncProvider + EventLogSyncProvider(hard-disabled) + WebSocketWrapper(autoConnect=false). Dashboard layout adds token keepalive, ConnectionStatusProvider, SeedProvider, OnecHubProvider, InfoBaseSyncProvider.
- NEXT mode = API URL containing /api/v2 (services/api.ts:49); only login.ts:35 and company.ts:49 use it. AuthProvider always GETs /user/me (auth.tsx:151) and clearAuth()s on error (:173) → NEXT sessions die on reload; NEXT has no refresh token.
- App-shell WS `/connections/ws` (websocket/providers/socket.tsx) never connects: autoConnect=false (layout.tsx:48), nobody calls connectWebSocket → seed.tsx:216-241 auth.connect / auth.logout / bank.credentials DEAD.
- Any 401 from bank/1C/1uz calls → shared APIService interceptor → refresh against main /auth/refresh → on failure clearAuth + /login (services/api.ts:84-106).
- Tray Quit: Rust emits app-quit-requested (src-tauri/src/setup/tray.rs:92); only listener in ConnectionStatusProvider (connection-status.tsx:486), mounted only in dashboard → Quit ignored on /login and debug-logs.
- Release builds with --features devtools (scripts/updater.ps1:277-286); Ctrl+D then Ctrl+I → open_devtools (mode-switcher.tsx:76-111). localStorage aiba-auth (tokens), aiba-infobases (1C username/password, stores/infobase.ts:130-135).
- Ctrl+Shift+D toggles prod/dev + logout, no confirm (mode-switcher.tsx:63-73). Alt+B OS-global → debug-logs window (src-tauri/src/lib.rs:935-956). Ctrl+Shift+B / ?dev=true / window.__openDevSettings → Dev Settings (localStorage dev_settings; URL overrides read at module import). Ctrl+Shift+X theme. Alt+C on Settings → start_adapter. Ctrl+Shift+T bulk mode in DB modal. Ctrl+Shift+S sync all companies.
- Adapter watchdog (providers/adapter.tsx): 15 s; restarts after 90 s unresponsive; "no config → inactive" bypassed because watchdog starts adapter anyway (:233-265). 1uz supervisor only when a 1uz base exists.
- DatabaseConfigModal: discover_databases, test_adapter_connections (+ adapter:test-progress events), add/remove_adapter_database, restart_adapter, list_infobase_users; HealPanel: GET /api/db/{n}/healScan then invoke heal_base (real config write: «Внешнее соединение» dynamic update), restart.
- ConnectionStatusProvider: every 60 s PATCH {1C}/onec/connection/{id} {status running|active}; on startup PATCH active; on quit PATCH inactive then exit_app.
- Updater: GitHub latest.json, minisign; 30-min checks in prod; patch = optional, minor/major = undismissable RequiredUpdateModal; install = stop_adapter, prepare_for_update, install, relaunch. Required-update button shows literal &apos;.
- Token keepalive (lib/aiba-token-keepalive.ts): every 60 s POST {SIGNER :7777}/api/aab/auth-token {token, env, bank_api_base, user_id} — pushes the cloud bearer to the local signer; every 10 min GET /user/me.
- Settings: Tunnel (tunnel_get_status / tunnel_ensure_config, tunnel-status event), Bank key manager toggle, ParallelModeCard (get/set_adapter_pool_config, polls /api/status every 4 s), FastSyncCard (BULK_REGISTERS, COLD_READ_WORKERS).
- Debug-logs window: get_logs/clear_logs/export_logs, live log-entry events; Copy-all unscrubbed.
- Sentry: on only in prod builds with DSN, not dev mode; sends phone number, full name, company INN/name, device.id; captureConsole(error); write contexts and structured logs not scrubbed.
- Seed: companies, banks list, infobases (refetch every 30 s), bank subscriptions, epasses (localhost:6210). reloadSeed is a no-op.
- Unused: ModeSwitcherContext, STORAGE_KEY, RuntimeProvider result, (public) route group, `unauthorized` listener (auth.tsx:212, no emitter). Committed .tmp files in layouts/ and banks/.

## Sync engine (helper "sync engine + utils")
- Entry: SyncOrchestrator.syncSingleInfoBase (features/accounting/lib/sync/sync-orchestrator.ts:386). Triggers: poller (sync-poller.ts, self-scheduling setTimeout, interval from SYNC_INTERVAL_MIN default 60 s), manual play button, bulk upload (mode full, onlyTables, urgent, preempts other lanes), Ctrl+Shift+S all companies, cloud sync_now/pull_now (lane cloud), reports lane (ReportsLaneDriver, separate worker :55949 via start_reports_worker), event-log driver (DEAD).
- Table list: GET {v1}/onec/{id}/sync-tables (10 s timeout; failure → null); PUT full replace from drawer and AUTO-SEED once when backend returns unset (info-base-sync-provider.tsx:192-235). Compiled defaults resolveTablesForInfoBase/TABLES_BY_PROVIDER (core/config.ts); mandatory venkon tables; policy overrides config (sync-policy.ts): РозничнаяВыручка + ОтчетОРозничныхПродажах → reports lane; AccountingRegister_* always night (21:00–07:00, one daytime run if >36 h since last); Document_ЧекККМ manual only. Source capability: tables absent per /counts suppressed 6 h.
- Priorities tableSyncPriority (config.ts:330-361): chart 10, Контрагенты 20, Номенклатура 25, Банки 30, Договоры 35, Document 40, InfoReg 45, other Catalog 50, AccumReg 70, AccountingReg 999.
- Paging: 500 rows (1000 when total>50k); adaptive page size by latency with sticky memory localStorage adapter_page_limits; poison rows skipped (adapter_poison_rows, 7 days); offset/date-cursor/keyset walks; /counts batch probe; documents batch by-id POST …/batch 200 ids.
- Change detection: row key id/Ref_Key, chart code, register recorderRef#lineNo; sha256 row hash; hash-diff only for Контрагенты, Банки, Номенклатура, charts (GET refhashes); probe scan with projections + 256-bucket digests (edit sweep every ≥30 min, 2 s budget); two-strike delete detection → POST prune-missing; register reconcile per changed/deleted document → POST reconcile-recorder; back-dated guard; future-date guard (>366 days). Non-hash catalogs (e.g. ДоговорыКонтрагентов ~116k) re-upload fully on any count change.
- Upload: POST {apiUrl}/entity/upload multipart; ФизическиеЛица also uploaded as Catalog_Сотрудники; GZIP_UPLOAD flag (backend support only on unmerged branch → would 400); 34 MB buffer split; 413 → split; 5xx not retried (fails table); network errors 6 retries.
- Other backend calls: entity/stock-snapshot (GET adapter /stock?accounts=1010,2910,1080,1090 every non-targeted cycle, both lanes), entity/data-coverage, reconciliation/ledger/upload (1uz), reports/retail-rollup/ingest (turnover 400 days first, then 14), counts?scope=connection, refids/refhashes/prune-missing/reconcile-recorder (NO timeout, NO abort signal → a hung backend pins a run past the 10-min stall watchdog), org-bindings GET/PUT, entity purge DELETE (local cursors not reset → per-id GET recovery storm).
- Concurrency: adapter-gateway lanes high/write/writePrep/normal/low/partition; global normal cap 1 unless ownership budget; per-base mutex; write gate (beginWrite) blocks sync for the base, 90 s auto-release, 20 s cooldown.
- Reports lane leaks: touches main-lane state (sync count, connection status, stock snapshot, coverage) without lane guard; several reads hard-code ADAPTER_API_URL → run on main adapter.
- Parallel cold read for registers (COLD_READ_WORKERS 2..8): start_partition_workers / stop_partition_workers — FLAGGED/RARE.
- Multi-org: org-router partitions movement rows by orgRef; stock snapshot and event-log uploads not partitioned; duplicate backend connection rows silently ignored (lexically smaller id wins).
- DEAD exports: refreshTargetedDocument, clearTableMetadata/clearAllTableMetadata, fetchAdapterDatabases, BufferManager.reset, the event-log stack (~1.8k TS lines + Rust tailer).
- Prod env flags (BULK_REGISTERS, GZIP_UPLOAD, COLD_READ_WORKERS) come from CI secret ENV_PRODUCTION — unknown from the repo; local files don't set them.

## onec-sync-provider extras (helper; mostly already in A-frontend.md — check)
- refdata_get cache replays the first request_id (7612-7630) → later callers time out.
- activeChart shared mutable variable across concurrent write_to_1c for different bases (4891, 11770).
- postBankDocWithFallbacks (12459-12519) re-POSTs on unresolved_reference up to 4× without checking created id; auto-creates organisation bank account (СоздатьЕслиНетоПусто) without confirmation.
- Failure caches: config version "" cached forever after one timeout (4971/4983); doc schema availability false cached; learnDepartment fallback cached.
- Hire flow: name-only match, first 500 rows, replies success even when hire doc failed (6814-6834). Dead handleEmployeeWriteCommand + buildEmployeeWriteDocument with hardcoded tenant names and a real person's name as default manager (11225).
- No unit test for onec-sync-provider.tsx (15,452 lines).
