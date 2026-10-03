# B: what the old AIBA Connector's Rust side actually does (read-only audit)

- **Repo:** `D:\aiba\connector` on branch `release`. HEAD is `cfab3a1` ("ci: let the release guard pass for a version that is not published yet").
- **Working tree:** one modified file, `resources/1uz-adapter/adapter.version`, and one untracked folder, `claudetest/`.
- **App version:** 1.3.109 (`Cargo.toml:3`, `tauri.conf.json:5`). Tauri 2.9, identifier `aiba.uz`.
- **Scope:** `apps/app/src-tauri` (22,038 lines of Rust) and what it bundles or spawns. That includes `adapter-router/src/main.rs` (3,345 lines, built into `resources/1c-adapter/adapter-router.exe`), the NSIS hooks, capabilities and the updater config.
- **How it was checked:** code only. I did not run anything. Where a claim depends on runtime behaviour, it says so.

Path conventions:
- File references with no prefix are under `apps/app/src-tauri/src/`.
- On Windows, `app_data_dir` is **`%APPDATA%\aiba.uz`** (Roaming). The WebView2 profile, which holds `localStorage`, is under `%LOCALAPPDATA%\aiba.uz\EBWebView` (Tauri default).

---

## 0. Findings that matter most (details further down)

1. **The adapter listens on all network interfaces with no authentication, and the app opens the firewall for it.**
   - The router binds `[::]:55899` in dual-stack mode (`adapter-router/src/main.rs:2717-2725`).
   - The default config written by Rust uses `"host": "0.0.0.0"` and `"enableAuth": false` (`handlers/filesystem.rs:1093-1104`).
   - `netsh advfirewall firewall add rule name="AIBA_Adapter_Allow_55899" dir=in action=allow protocol=TCP localport=55899` runs on every adapter start (`filesystem.rs:1227-1288`). It has no `remoteip` or profile restriction.
   - The router answers with `Access-Control-Allow-Origin: *` (`adapter-router/src/main.rs:1883`).
   - Result: anyone on the LAN can read and write every configured 1C base through `/api/db/...`.
2. **Any call that runs `ensure_adapter_dir` kills the live adapter.** This is a code inference; I did not observe it at runtime.
   - `ensure_adapter_dir` calls `kill_orphaned_adapters(&["oscript"])` (`filesystem.rs:1028`). That function sweeps ports 55899–55924 and runs `taskkill /F` on any oscript or `adapter-router` process it finds, with no list of processes to keep (`filesystem.rs:86-119`, `reap_router` at 91).
   - So the healthy router and all its workers are killed. The Rust watchdog then respawns them within about 1 s, and every base pays its cold COM connect again (6–8 s).
   - Callers that run while the adapter is up:
     - `get_adapter_pool_config`, on every visit to the Settings page (`settings/page.tsx:213`)
     - `add_adapter_database`, `remove_adapter_database`, `discover_databases`, `test_adapter_connections`
     - `start_reports_worker`, when the reports worker is not already alive
     - `start_partition_workers`
     - `start_adapter` itself, which calls `get_adapter_config_status` before its own "is our child already alive" check (`filesystem.rs:1790` comes before 1802)
3. **Release builds include DevTools.** `scripts/updater.ps1:277-286` always builds with `--features devtools`, and `open_devtools` is enabled under that feature (`lib.rs:684-695`). In the UI, Ctrl+D then Ctrl+I opens DevTools (`providers/mode-switcher.tsx:74-112`). Ctrl+Shift+D silently switches the app between the prod and dev cloud (`mode-switcher.tsx:63-72`).
4. **Device identity is the Windows `MachineGuid`, cached in WebView2 `localStorage` and never checked again.** The cloud's connector id is `${user_id}:${MachineGuid}`. See §6.
5. **Credentials are stored as plaintext JSON:**
   - 1C user and password: `1c-adapter\config.json`, plus up to 21 backup copies.
   - 1UZ SQL credentials: `1uz-adapter\sql-auth.json`.
   - The tunnel token: `tunnel.json`.
   - The 1C Designer password is passed on a process command line.
   - Only the SQB password uses DPAPI.
6. **The signer tunnel exposes the whole local signer admin API to the relay backend.**
   - The connector dials out to `wss://relay.aiba.uz/ws/connector/?token=…`, with the token in the URL query.
   - It relays **any HTTP method and path** to `127.0.0.1:7777` (signer admin) or `:6210`, and any Styx frame, including `signMSG`, to `:13589` (`handlers/tunnel_agent.rs:336-468`).
7. **About 4,600 lines of event-log tailing code are shipped but can never run.** `isEventLogSyncEnabled()` returns `false` unconditionally (`apps/app/src/utils/dev-settings.ts:80-86`).
8. **Commands that are registered but never called** (§1):
   - Kapital Bank headless login (`open_kapital_auth_window`)
   - Kapital Bank webview sync (`sync_kapital_via_webview`)
   - `install_adapter`, which runs an elevated `install.ps1` that no longer exists
   - `usb_test_disable_one`, which disables a smart-card reader persistently
   - about ten others

---

## 1. Every `#[tauri::command]` (82 registered, `lib.rs:805-883`)

How each command was classified: I searched `apps/app/src/**/*.ts(x)` for the command name as a word, then read the call sites.

| Status | Meaning |
|---|---|
| ACTIVE | Called from a normal UI or provider path |
| RARE | Called only from a user action or settings screen |
| FLAGGED | Behind a flag that is off |
| UNUSED | Registered, but no TS caller |
| DEAD | Unused, and the target it acts on does not exist |

### 1a. Adapter lifecycle and config (`handlers/filesystem.rs`)

| Command | Line | Args → Return | What it does | TS callers | Status |
|---|---|---|---|---|---|
| `prepare_adapter` | 1170 | → `{adapter_dir, config_path, install_script, main_script}` | `ensure_adapter_dir` (see §2.3) | `providers/adapter.tsx:175`, `database-config-modal.tsx` | ACTIVE |
| `install_adapter` | 1185 | → `{success,message}` | Runs `powershell Start-Process PowerShell -Verb RunAs … -File install.ps1 -Wait`. **`install.ps1` does not exist** in `resources/1c-adapter` | none | DEAD |
| `ensure_adapter_firewall_rule` | 1293 | → `()` | Adds the netsh rule (the same internal function also runs on every start) | none (internal path is ACTIVE) | UNUSED as a command |
| `start_adapter` | 1735 | → `{running,pid}` | Full spawn path (§2.1) | `adapter.tsx:177`, settings page, modal | ACTIVE |
| `stop_adapter` | 2446 | → `{running:false}` | Kills the child (5 s), partition and reports workers, runs the lineage sweep and frees the port | `adapter.tsx:166`, `update.tsx:226` (before an update), modal | ACTIVE |
| `restart_adapter` | 1984 | → status | Sets `RESTARTING`, kills, sweeps, calls `start_adapter` | modal, heal-panel, `use-info-base-form.ts` | ACTIVE |
| `adapter_status` | 2503 | → `{running,pid}` | `try_wait` on the child | `adapter.tsx:259`, every 15 s | ACTIVE |
| `get_adapter_pool_config` | 2364 | → `{ownership, max_workers}` | Reads `routerMode` and `maxWorkers`. **Runs `ensure_adapter_dir`, which kills the live adapter (finding 0.2)** | `settings/page.tsx:213` on mount | RARE |
| `set_adapter_pool_config` | 2389 | `ownership, max_workers?` → status | Writes `routerMode`, removes `workerPoolSize`, then backup and `restart_adapter` | settings page | RARE |
| `start_partition_workers` | 2272 | `db_name, count` → ports | 1–8 standalone oscript workers on 55929+i with `--own-bases <db>`, in a kill-on-close job | `incremental-fetcher.ts:1839` | FLAGGED: `COLD_READ_WORKERS` dev setting, default 1 means off (`dev-settings.ts:94`) |
| `stop_partition_workers` | 2125 | → `()` | Kills tracked workers and sweeps 55929–55944 for oscript | `incremental-fetcher.ts` | FLAGGED |
| `start_reports_worker` | 2196 | → port 55949 | One long-lived oscript on adapter_port+50, in the job | `info-base-sync-provider.tsx:390` via `reports-lane-driver.ts:352`, every due cycle; returns early if already alive | ACTIVE |
| `stop_reports_worker` | 2256 | → `()` | | none (the exit handler reaps it) | UNUSED |
| `set_adapter_kind` | 1433 | `kind` → `()` | Stores `ADAPTER_KIND`, **which nothing reads** | none | DEAD |
| `start_uz_adapter` | 1567 | → status | Spawns `1uz-adapter.exe` (.NET, MSSQL) with env `ADAPTER_PORT=55900`; has its own watchdog | `adapter.tsx:423`, the 1uz forms | ACTIVE (1UZ tenants) |
| `stop_uz_adapter` | 1646 | | | `adapter.tsx:443` | ACTIVE |
| `uz_adapter_status` | 1662 | | | `adapter.tsx:453` | ACTIVE |
| `set_uz_sql_auth` | 1686 | `server?, user?, password?` | Writes or deletes **plaintext** `%APPDATA%\aiba.uz\1uz-adapter\sql-auth.json` | `use-info-base-form.ts` | RARE |
| `get_adapter_database_config` | 2583 | `db_name` → one base, **including the password** | Fast path: reads `config.json` directly | modal, info-base form, `stores/adapter.ts` | ACTIVE (~12/min per base, per the comment at 2634) |
| `get_all_adapter_database_configs` | 2687 | → all bases with passwords | | modal | RARE |
| `list_infobase_users` | 4629 | | Reads 1C users: file bases by parsing `1Cv8.1CD` V8USERS directly (`onec_1cd.rs`); server bases via `psql -w` with trust auth or `sqlcmd -E` against `v8users` (`onec_cluster.rs:285-344`) | modal | RARE |
| `discover_databases` | 2756 | → list | `oscript main.os --discover`; reads `discover-result.json` (UTF-8) | modal | RARE |
| `add_adapter_database` | 3776 | `{name,path,user,password,type,server,database,comVersion}` | Merge-writes `config.json`, then backup | modal, info-base form | ACTIVE |
| `remove_adapter_database` | 3910 | `name` | | modal | RARE |
| `test_adapter_connections` | 3399 | `{defaults, items[]}` → summary | `oscript --test-connections` (§2.4); emits `adapter:test-progress`; may flip the machine-wide comcntr registration | modal | RARE |
| `set_adapter_port` | 3965 | `port` | `oscript main.os --port N` | none | UNUSED |
| `get_adapter_config_status` | 4054 | → `{config_exists, database_count, port}` | `ensure_adapter_dir`, then reads config | `adapter.tsx:115` at startup; also called inside `start_adapter` | ACTIVE |
| `get_runtime_environment_status` | 4009 | | | none | UNUSED |
| `validate_runtime` | 2438 | → `RuntimeStatus` | Extracts the runtime and runs `oscript -version` | `providers/runtime.tsx` | ACTIVE |
| `write_file` | 600 | `{path, content}` | **Writes to any path.** The only guard is a blocklist of `C:\Windows\System32`, `SysWOW64`, `Program Files*` and `ProgramData` (605-618). Other drives, `%APPDATA%\…\Startup` and `C:\Windows` itself are allowed, and the process is often elevated | `export-data-service.ts` | RARE |

### 1b. Logging and app (`handlers/logging.rs`, `lib.rs`, misc)

| Command | Line | What it does | Callers | Status |
|---|---|---|---|---|
| `get_logs` | logging.rs:288 | Returns up to 2,000 entries from the in-memory ring buffer | debug-logs window | RARE |
| `ui_log` | logging.rs:264 | Writes a renderer log into `debug.log` (only WARN and ERROR reach the file) | `utils/ui-log.ts` | ACTIVE |
| `clear_logs` | logging.rs:293 | Clears the buffer only | debug-logs | RARE |
| `export_logs` | logging.rs:303 | **Only returns the file path**; exports nothing | debug-logs page | RARE |
| `get_log_file_path` | logging.rs:298 | | none | UNUSED |
| `toggle_debug_window` | logging.rs:308 | Opens a `/debug-logs` webview window | `banks/keys/page.tsx`, and the global **Alt+B** (`lib.rs:927-959`) | ACTIVE |
| `open_devtools` | lib.rs:685 | Opens DevTools; a no-op unless debug or `feature=devtools`, **and release uses that feature** | `mode-switcher.tsx:106` (Ctrl+D, Ctrl+I) | ACTIVE (hidden) |
| `exit_app` | lib.rs:698 | `app.exit(0)` | `connection-status.tsx` (after tray Quit) | ACTIVE |
| `get_device_id` | utils/fingerprint.rs:4 | `machine_uid::get()`, which is the MachineGuid | `utils/device-id.ts`, `observability.tsx:43` | ACTIVE (§6) |
| `set_backend_locale` | locale.rs:2 | `rust_i18n::set_locale` | none | UNUSED (the tray picks its own locale at setup) |
| `check_network_status` | network.rs:10 | `HEAD https://www.google.com`, 3 s | none | UNUSED |
| `proxy` | proxy.rs:45 | Generic reqwest from Rust: any URL, any method, multipart, 600 s timeout, **no allowlist** | `utils/tauri-proxy.ts:29`, only for URLs matching `^https://1uz.aiba.group/` (CORS workaround) | RARE |
| `onec_get/post/put/patch/delete` | utils/onec.rs:3-54 → services/onec.rs | HTTP with Basic auth to any `baseUrl`, header `X-User-Agent: AIBA-Connector`, 600 s | `onec/utils/fetcher.ts` ← `onec/services/unisoft.ts` ← `onec/utils/company.ts` (legacy "Unisoft" 1C HTTP-service path) | UNKNOWN / legacy |
| `parse_bulk_page` | onec_bulk.rs:185 | Parses XDTO ValueTable XML into JSON rows in Rust | `incremental-fetcher.ts`, `core/config.ts` | ACTIVE (bulk XDTO reads) |
| `parse_valuetable_xml` | onec_bulk.rs:167 | | none | UNUSED |
| `list_com_connector_versions` | com_connector.rs:409 | | none | UNUSED |
| `set_com_connector_version` | com_connector.rs:433 | `regsvr32` for a chosen DLL, then writes `%APPDATA%\aiba.uz\1c-adapter\comcntr.preference` | none | UNUSED |
| `scan_base_heal_status` | heal_extconn.rs:683 | `1cv8 DESIGNER /DumpConfigToFiles` to `%TEMP%` and reads the CommonModules flags | heal-panel | RARE |
| `heal_base` | heal_extconn.rs:709 | **Changes the live server base**: sets `ExternalConnection=true` in risky modules, then `/LoadConfigFromFiles … /UpdateDBCfg -Dynamic+`. No dry run (scan is the separate dry run), no undo command; backup stays in `%TEMP%`. Password passed as `/P` on the command line | heal-panel | RARE |
| `eventlog_start` | eventlog.rs:2471 | Starts the `.lgf/.lgp` tail thread (§2.6) | `eventlog-provider.tsx`, only if `isEventLogSyncEnabled()`, **which is hard-coded false** | FLAGGED (unreachable) |
| `eventlog_ack` | eventlog.rs:2510 | Moves the cursor forward and persists it | eventlog-driver | FLAGGED |
| `eventlog_status` | eventlog.rs:2552 | | eventlog-provider | FLAGGED |

### 1c. Bank, signer, USB and tunnel

| Command | File:line | What it does | Callers | Status |
|---|---|---|---|---|
| `get_signer_health` | sidecar.rs:691 | Signer status snapshot | `types/signer.ts` | ACTIVE |
| `get_pkcs11_status` | sidecar.rs:733 | Reads `library=` from `signer.toml` | `lib/signer-api.ts` | ACTIVE |
| `restart_signer` | sidecar.rs:741 | **Ignores the bank-key-manager OFF switch** | `banks/keys/page.tsx` | RARE |
| `prepare_for_update` | sidecar.rs:788 | Shuts down the sidecars, `taskkill /F /IM bank-connector.exe`, waits 1.2 s | `update.tsx:237` | ACTIVE (on update) |
| `get_sqb_health` / `restart_sqb_sidecar` | sqb_sidecar.rs:479/484 | | none | UNUSED |
| `get_bank_key_manager_enabled` / `set_…` | bank_key_manager.rs:155/163 | `bank_key_manager.json`. ON: clear ports, kill vendor Styx, spawn signer. OFF: stop signer, relaunch vendor Styx | settings, `bank-key-manager.tsx` | RARE |
| `activate_chip` | chip_activation.rs:1615 | Needs elevation. Isolates ePass readers (VID_096E), runs the vendor StyxTokenManager handover, backs up and restores `CurrentUser\My` certs through SST | `banks/keys/page.tsx` | RARE |
| `activate_chip_via_vendor` | chip_activation.rs:107 | Same flow without reader isolation | keys page | RARE |
| `cancel_chip_activation` | chip_activation.rs:92 | | keys page | RARE |
| `list_my_certs` / `pair_chip_cert` | pairing.rs:90/153 | PowerShell listing of the cert store; `POST :7777/api/learned-pairing` | keys page | RARE |
| `list_readers` | usb_control.rs:248 | `Get-PnpDevice -Class SmartCardReader` | none | UNUSED |
| `usb_test_disable_one` | usb_control.rs:256 | **Debug probe in production.** Disables the first ePass reader with `Disable-PnpDevice`/`pnputil`, **which persists across reboot**, waits 3 s, then re-enables | none | UNUSED but dangerous |
| `tunnel_get_config` | tunnel_agent.rs:820 | Returns the token in plaintext | none | UNUSED |
| `tunnel_set_config` | tunnel_agent.rs:854 | | none | UNUSED |
| `tunnel_ensure_config` | tunnel_agent.rs:835 | Generates a token (20 random bytes as hex; falls back to a clock-derived value if the RNG fails), saves, reconnects | `settings/page.tsx:50` on mount | RARE |
| `tunnel_get_status` | tunnel_agent.rs:825 | | settings | RARE |
| `open_kapital_auth_window` | lib.rs:67 | Opens a hidden WebView2 at `https://b2b.kapitalbank.uz`, off-screen at (-32000,-32000), with an injected script (details below the table) | none | UNUSED (kept registered; handles credentials) |
| `sync_kapital_via_webview` | lib.rs:466 | 2×2 px off-screen WebView2 that pulls payment orders in 30-day chunks for up to a year and posts them to the signer | none | UNUSED |

The `open_kapital_auth_window` script:
- patches `fetch` and `XMLHttpRequest` to capture bearer tokens
- POSTs the user's **login and password** to `b2b-api.kapitalbank.uz/api/auth`
- signs the INN through `http://127.0.0.1:7777/api/kapital/sign-inn`
- posts the password, access token and accounts to `:7777/api/kapital/store-session`
- spoofs `x-user-app: Uzum Business 2.23.0`

---

## 2. Process management

### 2.1 The 1C adapter (`start_adapter`, `filesystem.rs:1735-1975`)

**Trigger.** The UI provider calls `get_adapter_config_status` at startup. If there are bases, it calls `start_adapter("app startup")`. After that it polls (`providers/adapter.tsx:28`, 15 s). The Rust watchdog also triggers it (§2.2).

**Sequence:**
1. Set `ADAPTER_SHOULD_RUN=true` and start the watchdog.
2. Take the `SPAWNING` flag. A concurrent caller waits up to 5 s, then reports the real child status.
3. `ensure_adapter_dir` (§2.3). **This kills orphan and live oscript/router processes on ports 55899–55924.**
4. `ensure_adapter_firewall_rule_internal`: netsh `show rule`, then `add rule` if missing.
5. Port is taken from `get_adapter_config_status`, which runs `ensure_adapter_dir` a second time and so a second kill sweep.
6. If our own child is alive **and** listening on the port, return.
7. `kill_orphaned_adapters` (a third sweep), then `kill_stale_adapter_lineage` (PowerShell `Get-CimInstance Win32_Process` for oscript.exe and adapter-router.exe, matched on our adapter path in the command line, keeping tracked partition and reports PIDs; `filesystem.rs:157-233`), then `free_adapter_port` (netstat LISTENING; `taskkill /F /T` up to 10 times; refuses to kill a process that is not ours; 450-490).
8. Open `1c-adapter\adapter.log` for append, used as the child's stdout and stderr.
9. `com_connector::ensure_registered`: preference file, then PowerShell probe `New-Object -ComObject V83.COMConnector`, then `regsvr32 /s` if needed (com_connector.rs:470-601). No self-elevation; exit code 5 means not elevated.
10. Runtime architecture: x86 if the newest `comcntr.dll` path contains " (x86)" or "i386", otherwise x64. Then `ensure_runtime_extracted` (§2.5).
11. `read_worker_pool_size` (510-561):
    - Explicit `workerPoolSize` wins.
    - Otherwise `clamp(enabledBases, 2, 8)`.
    - Any `comVersion` forces at least 2.
    - **The router is on by default**, even for one base.
12. If the pool size is ≥ 2 and `adapter-router.exe` exists:
    - Command: `adapter-router.exe --public-port 55899 --base-port 55901 --workers N --oscript <rt>\bin\oscript.exe --main-os <dir>\main.os --parent-pid <our pid>`.
    - Environment from `build_environment()`: `DOTNET_ROOT`, `DOTNET_MULTILEVEL_LOOKUP=0`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `OSCRIPT_LIB`, and **the 1C bin dir prepended to `PATH`** (runtime.rs:70-115).
    - Otherwise: `oscript main.os --non-interactive --port 55899`.
13. Verify the new process owns the port: 24 checks × 500 ms. If something else owns it, return Err. If nothing owns it yet, report running (a cold COM connect can delay the bind).

**Router behaviour** (`adapter-router/src/main.rs`):
- **Ownership mode** is the default per `get_adapter_pool_config`; the router source comment says shard is the default, so the two disagree. Ownership means one oscript per base, spawned lazily, with LRU eviction.
- Budget: `free_RAM_MB/700`, clamped to `1..cores-1`, overridable with `maxWorkers` (555-562). Raised to at least 2 when there are write-worker bases.
- Kill-on-close Job Object for workers (40-130). If assignment fails it logs "ORPHAN PROTECTION OFF"; this happens when the app was started by the NSIS updater.
- Watches the parent PID and self-terminates when the connector dies (2607-2641).
- `ComRegLock` uses `%TEMP%\aiba-1c-comreg.lock` (627-660) and wraps `regsvr32 /s` for per-base `comVersion` (765-810). The registered DLL is read back with `reg query HKCR\…` (715).
- Answers `/api/status` locally. It **proxies `/api/db/{name}/…` to the owning worker on 127.0.0.1:55901+**.
- Listens on `[::]:55899` with SO_REUSEADDR and a backlog of 512 (2703-2743).

**Exit** (`lib.rs:1085-1145`, on `RunEvent::ExitRequested` only):
- kill partition workers, then the reports worker, then the main child (`kill` + `wait`), then the 1uz child
- `SidecarState.shutdown()` and `SqbSidecarState.shutdown()`

A crash or Task Manager "End task" skips all of this. The safety nets are then the router's parent watch, the Job Objects, and the next start's sweeps.

### 2.2 Watchdogs

| Watchdog | Where | Interval | Logic |
|---|---|---|---|
| oscript/router | `filesystem.rs:1369-1428` | 1 s poll | If `SHOULD_RUN` is set, `RESTARTING` and `SPAWNING` are clear, and the child is dead or absent, call `block_on(start_adapter)`. On error, back off 1→2→…→15 s |
| 1uz | `filesystem.rs:1464-1561` | 1 s | Crash-loop guard: 4 deaths within 30 s of start means **give up** (`UZ_SHOULD_RUN=false`). Backoff up to 300 s |
| UI health | `providers/adapter.tsx:248-338` | 15 s | Process check first, then `fetch /api/status` with a 5 s timeout. Restarts only after 90 s of continuous HTTP failure while the process is alive |
| signer | `sidecar.rs:231-307` | 30 s grace, then `GET 127.0.0.1:7777/api/healthz` every 15 s (5 s timeout) | 3 failures in a row means restart. Crash restart backs off 1→30 s, with a generation counter |
| SQB Python | `sqb_sidecar.rs:332-374` | `try_wait` every 3 s | Respawn with backoff 2→60 s. No HTTP health check |
| router → workers | `adapter-router/src/main.rs` | | Respawns dead workers; a pinned worker respawns forever |

### 2.3 `ensure_adapter_dir` (`filesystem.rs:1022-1167`), run by about 15 commands

1. `kill_orphaned_adapters(["oscript"])`. Ports 55899..55924 (26 ports): for each, `cmd /C netstat -ano | findstr :P`, then `tasklist /FI "PID eq X"`, then `taskkill /F /PID` if the name contains "oscript" or "adapter-router". That is **up to 26 + N cmd spawns per call**. It has no keep-PID, so it kills our own live router and workers (finding 0.2).
2. Read `%APPDATA%\aiba.uz\1c-adapter\config.json`. If it has content, back it up (§5). If it is empty, missing or unreadable, restore from backup.
3. Copy `resources/1c-adapter` into AppData with `copy_dir_optimized` (647-701):
   - Skips `config.json`, `config.local.json` and `adapter.log`.
   - Copies a file when forced, when the destination is missing, or **when the sizes differ** (no hash check).
   - Forced when `adapter.version` text differs from the bundled one (compared untrimmed).
   - A locked `adapter-router.exe` is skipped with a warning.
4. Port migration: forces `server.port = 55899` (or the build-time `AIBA_ADAPTER_PORT`). **`server.host` is left as it was.**
5. With no config and no backup, writes the default (`host 0.0.0.0`, `enableAuth:false`, `token:"your-secret-token"`). It never seeds from the bundled `config.json`; the comment cites a real field leak (1149-1157).

### 2.4 Connection test (`test_adapter_connections`, 3211-3749)

- Writes `%TEMP%\aiba-test-conn-<pid>-<nanos>.json`, **containing the default user and password in plaintext**.
- Spawns `oscript main.os --test-connections <file>` and tails `…-out\r-N.json` every 150 ms, emitting `adapter:test-progress` for each item.
- Parses comVersion from 1C error text: "Different client and server versions (a - b)" and the configuration-floor message in RU or EN.
- Runs `regsvr32` on the matching `comcntr.dll` **for the whole machine**, retests, then restores the original registration (`ComRegSession`, 3056-3203).
- A server base that connects successfully gets `detectedComVersion` set to whatever DLL was registered.
- Temp files are deleted afterwards.
- No overall timeout on the oscript child.

### 2.5 Bundled OneScript runtime (`handlers/runtime.rs`)

- Copies `resources/onescript-runtime[-x86]` to `%APPDATA%\aiba.uz\onescript-runtime[-x86]` and writes a `.extracted` marker (348-442).
- **Only re-extracts when the marker or `oscript.exe`/`.dll` is missing.** A newer runtime shipped in an app update is **never** deployed to existing installs (surprise).
- Before re-extracting it runs `taskkill /F /IM oscript.exe /T` (32-37). This kills **every oscript on the machine**, including other users' processes and unrelated customer scripts if the app is elevated.

### 2.6 Event-log tailer (flagged off; `handlers/eventlog.rs`, `eventlog_discovery.rs`)

**Status:** unreachable in production. `eventlog_start` is never called from Rust setup, and the TS gate returns false.

What it would do:
- One thread with no stop command. Polls every 3 s and reloads config every 60 s.
- **Supports only the old text format** (`1Cv8Log\1Cv8.lgf` + `*.lgp`). `.lgd` (SQLite) is refused as `lgd_unsupported`, and `.lgx` is ignored.
- Server bases are supported only when the cluster is on the same machine. It finds the running `ragent.exe` through PowerShell `Get-CimInstance Win32_Service`, parses `-d`/`-regport`, reads `reg_<port>\1CV8Clst.lst` to get the base GUID, then uses `<srvinfo>\reg_<port>\<guid>\1Cv8Log`.
- Keeps `_$Data$_.New/Update/Post/Unpost/Delete` events, decodes the ref to a UUID, and emits `eventlog:changed` (2418) in batches: 12 s or 500 events, with an overflow flag above 2,000.
- Cursor is persisted at `%APPDATA%\aiba.uz\eventlog-cursors.json` and moves only on ack (at-least-once delivery). The write is non-atomic.

### 2.7 Every spawned process

| Exe / command | Where | Notes |
|---|---|---|
| `adapter-router.exe …` | filesystem.rs:1884 | Pool supervisor |
| `oscript.exe main.os --non-interactive --port P [--own-bases X]` | runtime.rs:241 (from filesystem.rs:1913, 2230, 2322) | Adapter, reports worker, partition workers |
| `oscript.exe main.os --discover` / `--test-connections f` / `--port N` | filesystem.rs:2768, 3255, 3976 | |
| `oscript.exe -version` | runtime.rs:122 | |
| `1uz-adapter.exe` (env `ADAPTER_PORT`) | filesystem.rs:1623 | Bundled file is about 106 MB |
| `bank-connector.exe signer --config signer.toml` (Tauri sidecar) | sidecar.rs:333 | Env `BANK_CONNECTOR_DATA_DIR`, `PATH`+epass |
| `python -m sqb_connector run` (bundled python, else `SQB_PYTHON_EXE`, else **system `python`/`py`**) | sqb_sidecar.rs:203-248, 384-407 | Spawned unconditionally at startup (`lib.rs:1039`). Playwright on `business.sqb.uz`, admin server `127.0.0.1:7778` |
| `cmd /C netstat -ano \| findstr …`, `tasklist`, `taskkill /F [/T] /PID\|/IM` | filesystem.rs, sidecar, chip_activation, port_clearance | Process images killed by name: `oscript.exe`, `bank-connector.exe`, `StyxTokenManager.exe`, `ePassCertd_2003.exe` |
| `powershell -NoProfile …` | lineage sweep, COM probe, `Get-PnpDevice`/`Disable-`/`Enable-PnpDevice`, `Export-`/`Import-Certificate`, `Get-CimInstance` (ragent and Styx path), `Invoke-RestMethod`, `IsInRole` | Many call sites |
| `regsvr32 /s <comcntr.dll>` (or the SysWOW64 one) | com_connector.rs:241; router:786 | Changes the machine-wide registration |
| `reg query HKCR\…` | router:715 | |
| `netsh advfirewall firewall show/add rule` | filesystem.rs:1242-1259 | |
| `netsh int ipv4 show/delete excludedportrange` | port_clearance.rs:155/183 | Ports 6210, 13589, 8181 |
| `pnputil /disable-device /enable-device` | usb_control.rs:156/174 | |
| `certutil -user -store My`; `where StyxTokenManager.exe` | chip_activation.rs:1027/910 | |
| `1cv8.exe DESIGNER /S srv\db /N u /P <pwd> /DumpConfigToFiles\|/LoadConfigFromFiles … /UpdateDBCfg -Dynamic+` | heal_extconn.rs:216, 537 | |
| `psql.exe -w …` / `sqlcmd -E …` against `v8users` | onec_cluster.rs:296/322 | |
| vendor `StyxTokenManager.exe` (untracked, not in a job) | chip_activation.rs:357; bank_key_manager.rs:141 | |
| `ShellExecuteW("runas", current_exe)` | utils/privileges.rs:~200 | Self-elevation |

---

## 3. Platform discovery, COM and elevation

**Finding comcntr** (`com_connector.rs:77-199`):
- `HKLM\SOFTWARE\[WOW6432Node\]Microsoft\Windows\CurrentVersion\Uninstall\*`, where `DisplayName` contains "1C:Enterprise 8.3" or "1C:Предприятие 8", using `InstallLocation`.
- Plus `%ProgramFiles%` and `%ProgramFiles(x86)%\1cv8\<ver>\bin\comcntr.dll`.
- Sorted by numeric version.

**Registered DLL:** `HKCR\V83.ComConnector\CLSID` → `HKCR\CLSID\{…}\InprocServer32` (342-370).

**Registration is machine-wide state.** The same registration is flipped by the router per worker, by connection tests, by `ensure_registered`, and by the unused `set_com_connector_version`. The serialising lock lives in **per-user `%TEMP%`**, so two Windows users on one PC do not serialise against each other.

**Elevation** (`main.rs:36-39`, `utils/privileges.rs`):
- Release builds only. Disabled by the build-time `AIBA_SKIP_ELEVATION`.
- Elevates when `admin_work_pending()` is true, which is any of:
  - a `comVersion` or `defaultComVersion` is pinned in the config
  - comcntr is installed but not registered
  - **any `SmartCardReader`-class device exists, including ones not present** (`Get-PnpDevice` without `-PresentOnly`)
- Elevation relaunches through `runas` **without the original arguments**, so `--autostart` is lost, then `exit(0)`.
- If the user declines, the app runs non-elevated and emits `com-admin-required {pinnedBases}` (`lib.rs:1058-1074`).
- `app.manifest` is `asInvoker`.

**1C launcher list (`ibases.v8i`):** never read anywhere in Rust or in the router. Discovery is done in `main.os --discover`, which this audit did not cover.

---

## 4. Ports and local servers

| Port | Owner | Bind |
|---|---|---|
| 55899 | adapter-router (or a single oscript) | `[::]` dual-stack, all interfaces; inbound firewall rule open |
| 55900 | 1uz-adapter | not checked |
| 55901–55924 | router pool workers | 127.0.0.1 (the router proxies to 127.0.0.1) |
| 55929–55944 | cold-read partition workers (flagged) | |
| 55949 | reports-lane worker | |
| 7777 | bank-connector signer admin API | |
| 6210 | Kapitalbank/AAB crypto helper | |
| 13589 | Styx WebSocket | |
| 8088 | iABS7 fingerprint WebSocket | |
| 7778 | SQB Python admin | 127.0.0.1 |
| 8181 | legacy Styx; only its exclusion range is cleared | |

All of these can be shifted at build time with `AIBA_ADAPTER_PORT` / `AIBA_UZ_ADAPTER_PORT` (`filesystem.rs:62-78`). The lab config is `tauri.lab.conf.json` (identifier `aiba.lab`, updater pointed at `updates.invalid`).

---

## 5. Files, registry and environment variables

### 5.1 Files (all under `%APPDATA%\aiba.uz\` unless noted)

| Path | R/W | Content |
|---|---|---|
| `1c-adapter\` (main.os, adapter-router.exe, adapter.version) | W | Copied from resources |
| `1c-adapter\config.json` | RW | `server{host,port}`, `databases{name:{type,path,server,database,user,**password**,enabled,comVersion,writeWorker,workers}}`, `workerPoolSize`, `routerMode`, `maxWorkers`, `defaultComVersion`, `api{enableAuth,token}`. **Plaintext.** Rust reads `config.local.json` nowhere except eventlog; main.os prefers it |
| `1c-adapter\adapter.log`, `reports-55949.log`, `partition-<port>.log` | W | Child stdout and stderr. **No rotation** |
| `1c-adapter\discover-result.json` | R, then deleted | |
| `1c-adapter\comcntr.preference` | RW | DLL path, built from the `APPDATA` env var |
| `backups\1c-adapter-config\config.latest.json` + `config.<UTC>.json` ×20 | W | **Plaintext copies with passwords.** A guard stops a zero-base config overwriting good backups; the newest backup that still has bases is always kept (842-949) |
| `1uz-adapter\` (exe, `uz-adapter.log`, **`sql-auth.json`** plaintext) | RW | |
| `onescript-runtime[-x86]\` + `.extracted` | W | |
| `logs\debug.log` (+ `.log.old`) | W | Rotated at 10 MB to `.old`, so about 20 MB maximum. Files over 20 MB are deleted at startup. Signer and UI INFO/DEBUG are not written to disk (logging.rs:97-122) |
| `eventlog-cursors.json` | RW | Flagged feature |
| `signer.toml`, `tunnel.json` (**plaintext token**), `bank_key_manager.json`, `chip_cert_map.json`, `usb-isolation-pending.json`, `my-store-pre-activation.sst` | RW | Bank side |
| `sqb_credentials.enc.json` (DPAPI), `sqb_cache.sqlite3`, `sqb-profiles\`, `aiba_token.json` (**plaintext bearer**) | Python sidecar | |
| `%TEMP%\aiba-1c-comreg.lock`, `aiba-test-conn-*.json` (password), `heal_<pid>_<n>\` (full config dump **left behind on success**), `aiba-v8users-*.txt` | | |
| `<cwd or exe dir>\logs\chunks`, `unisoft-data` | deleted at startup | `utils/cleanup.rs` |
| 1C files read: `<base>\1Cv8.1CD` (shared read, V8USERS), `1Cv8Log\*`, `srvinfo\reg_*\1CV8Clst.lst` | R | |

### 5.2 Registry

| Key | Access | Purpose |
|---|---|---|
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → value `AIBA-Connector` = `"<exe>" --autostart` | **written on every start** if it differs (`utils/startup.rs`) | Autostart. Nothing reads `--autostart`; the window always opens visible. **NSIS does not remove it on uninstall** (no hook) |
| `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid` | R (`machine-uid` 0.5.4, `KEY_WOW64_64KEY`) | Device id |
| `HKCR\V83.ComConnector\CLSID`, `HKCR\CLSID\{…}\InprocServer32` | R; written indirectly by `regsvr32` | |
| `HKLM\…\Uninstall\*` (1C, Styx) | R | |

Windows Credential Manager and DPAPI are not used from Rust. The `Win32_Security_Credentials` feature is enabled in `Cargo.toml` but I found no use of it.

### 5.3 Environment variables

**Build time:** `SENTRY_DSN`, `SENTRY_ENV` (baked by build.rs from `apps/app/.env` `NEXT_PUBLIC_SENTRY_*`), `AIBA_ADAPTER_PORT`, `AIBA_UZ_ADAPTER_PORT`, `AIBA_SKIP_ELEVATION`, `BANK_CONNECTOR_EXE`.

**Runtime reads:** `PATH`, `APPDATA`, `ProgramFiles(x86)`, `SystemRoot`, `COMPUTERNAME`, `USERDNSDOMAIN`, `SIGNER_CONFIG`, `SQB_PYTHON_EXE`, `SQB_BROWSER`.

**Set on children:** `DOTNET_ROOT`, `DOTNET_MULTILEVEL_LOOKUP`, `DOTNET_CLI_TELEMETRY_OPTOUT`, `OSCRIPT_LIB`, `PATH`, `ADAPTER_PORT`, `BANK_CONNECTOR_DATA_DIR`, `SQB_ADMIN_PORT`, `PYTHONPATH`, `PGCLIENTENCODING`.

---

## 6. Device identity (precise)

**Source.** `get_device_id` (`utils/fingerprint.rs:4-7`) returns `machine_uid::get()`, which on Windows is `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid` read with `KEY_WOW64_64KEY` and trimmed (machine-uid 0.5.4 `src/lib.rs:162-197`). Rust adds no salt, no hash and no file.

**Storage.**
- The renderer caches it in zustand `persist` under the `localStorage` key `aiba-connector-store` (`stores/app.ts:44-95`; `partialize` keeps `appMode` and `deviceId`).
- `getDeviceId()` returns the cached value if present and **never re-reads it from Rust** (`utils/device-id.ts:4-14`).
- `localStorage` lives in the WebView2 profile, `%LOCALAPPDATA%\aiba.uz\EBWebView`, which is per Windows user.

**Use.** `connector_id = ${user_id}:${device_id}` (`providers/onec-sync-provider.tsx:1481, 1496`). The hub dedups on this value.
- A retired random fallback id caused "ghost" double connectors (measured on KANSLER 2026-09-29). It is deleted on start (`dropRetiredFallbackId`, ~209-232).
- The socket stays quiet until `device_id` resolves (`resolveDeviceId`, about 236-260).

| Question | Answer |
|---|---|
| Stable across app restart | **Yes.** Same MachineGuid, and the cached copy |
| Stable across app update | **Yes.** Identifier `aiba.uz` is unchanged, so the WebView2 profile survives |
| Stable across reinstall | **Yes** in value. Even if the uninstaller deletes app data, Rust re-reads the same MachineGuid |
| Stable across AIBA logout | **Yes.** Normal logout keeps it. The global `unauthorized` handler runs `localStorage.clear()` (`providers/auth.tsx:212-225`), which wipes the cache and also resets `appMode` and dev settings, but the value is re-read and comes back the same. `connector_id` changes if a different AIBA user logs in, because `user_id` is part of it |
| Stable across a different Windows user on the same PC | **Same `device_id`.** MachineGuid is machine-wide; each user has their own cached copy. Connector id differs only by AIBA `user_id` |
| Stable across machine migration | **No, by design** for a new or sysprepped Windows install (new MachineGuid). **Two risks:** (a) VM or disk images cloned without sysprep share one MachineGuid, so two machines get one identity and steal each other's bases; (b) a copied user profile carries the **stale cached** old GUID in `localStorage`, and it is never revalidated. Domain roaming profiles roam `%APPDATA%` (the 1C config with passwords) but not `%LOCALAPPDATA%` (the device cache) |
| Multiple connectors per user | Not possible on one machine. `connector_id` is deterministic, so a second instance in another session under the **same AIBA user** produces the **same** `connector_id` with two sockets. That is the ghost-routing failure class the code comment describes. Nothing in the code prevents it |
| Multiple Windows users per PC | Each session can run its own instance: the single-instance mutex is `"aiba.uz-sim"` with no `Global\` prefix, so it is per session (`tauri-plugin-single-instance-2.4.1/src/platform_impl/windows.rs:65-70`). **But every port is machine-global.** The second instance's sweeps `taskkill` the first user's oscript and router (this succeeds when elevated, which is common). Its signer fights for 7777/6210/13589. The router uses `SO_REUSEADDR`, which on Windows can let **two routers bind 55899 at once**, matching the "stale router answered with an old config" incident at `filesystem.rs:397-405`. `regsvr32` flips are machine-wide while the lock is per user |
| Multiple instances of one user | Blocked in release builds by the single-instance plugin, which focuses the existing window (`lib.rs:772-785`). Debug builds allow it. The lab build uses identifier `aiba.lab` |
| Installation id / connector id stored by Rust? | **None.** No installation UUID exists anywhere. The tunnel token in `tunnel.json` is the only other generated identity; it is per user, created on the first Settings visit, and plaintext |

---

## 7. Tray, autostart, updater, deep links, notifications, shortcuts

- **Tray** (`setup/tray.rs`): Show, Hide, Quit, localised en/ru/uz.
  - Quit emits `app-quit-requested` to the main window; the renderer then calls `exit_app`.
  - Left click shows the window.
  - Closing the main window **hides** it (`lib.rs:788-797`). The Kapital windows are allowed to close.
- **Autostart:** the HKCU Run key (above). There is no tauri autostart plugin.
- **Updater** (`tauri-plugin-updater`):
  - Endpoint `https://github.com/AIBAUZ/aiba-connector/releases/latest/download/latest.json`, signed with a minisign pubkey, Windows `installMode: passive`.
  - **There is one channel and no dev/beta channel.**
  - The JS checks every 30 minutes (`providers/update.tsx:16`) and downloads in the background. Install happens only on a user click; there is a forced modal for non-patch versions.
  - Install sequence: `stop_adapter`, then `prepare_for_update`, then `update.install()`, then `relaunch()`.
  - **`relaunch` comes from `@tauri-apps/plugin-process`, which is not registered in Rust** (`Cargo.toml` and `lib.rs:738-755` have no process plugin). It would fail if it were ever reached; on Windows the NSIS passive install normally terminates the app first.
- **NSIS** (`installer-hooks.nsh`): pre-install and pre-uninstall run `taskkill /F /IM bank-connector.exe /T`. The hooks do not kill oscript or the router, do not remove the firewall rule or Run key, and do not unregister comcntr. `wix: null`; the WebView2 bootstrapper is downloaded. Default Tauri NSIS install mode (per user) is assumed.
- **Deep links:** none (no deep-link plugin or scheme).
- **Notifications:** none from Rust.
- **Global shortcut:** **Alt+B, system-wide**, toggles the debug-log window (`lib.rs:927-959`). It steals Alt+B from every other app while the connector runs.
- `tauri-plugin-prevent-default` disables the context menu.
- **Capabilities:** `core:default`, `shell:default` for `main` and `debug-logs`; `updater:default` for `main`. All custom commands are callable from those windows. The external `kapital-*` windows are not listed, so they get no IPC; their scripts only use HTTP to `127.0.0.1:7777`.
- `security.csp: null`, so there is no CSP.

---

## 8. Logging, Sentry and telemetry

- **LogManager** (`handlers/logging.rs`):
  - Ring buffer of 2,000 entries.
  - Emits `log-entry` to all windows for every entry.
  - Writes `debug.log` in the format `[ts] [LEVEL] [cat] msg\n{details}\nSource:`.
- **Sentry** (Rust, `lib.rs:706-729`):
  - Release builds only, and only if a DSN was baked in at build. **The release script does not require one** (`scripts/run-updater.ps1:224`), so whether production Rust Sentry is on is UNKNOWN.
  - Release name `aiba-connector@<ver>`; `auto_session_tracking` is on.
  - Every log entry becomes a breadcrumb and every ERROR becomes an event. Message and details pass through `redact::scrub` first, which masks values after password, pwd, secret, token or api_key followed by `=` or `:`.
  - **`debug.log` and stderr are not scrubbed.**
  - `AdapterDatabaseConfig` has a hand-written `Debug` that hides the password, after the comment notes 11,381 cleartext password lines were found on one machine (2634-2637).
- **Other telemetry:** none from Rust. `check_network_status` would contact google.com but is unused. .NET telemetry is explicitly opted out.
- **Resource stats:** none from the Rust host. The router reads free RAM and core count only to size its budget.

---

## 9. Talking to the cloud directly from Rust

| Channel | Code | Target | Status |
|---|---|---|---|
| Tunnel WebSocket | `tunnel_agent.rs:168-182, 627` | `wss://relay.aiba.uz/ws/connector/?token=<hex>`; the URL comes from the UI (`core/config.ts:218-234`) | ACTIVE only if a token exists **and** the bank key manager is ON. Pings every 15 s, idle timeout 45 s, reconnect after a fixed 3 s. **The full URL including the token is passed to `log::info!`** (whether any logger is installed is unconfirmed; none found in `src/`) |
| `proxy` | proxy.rs | `https://1uz.aiba.group/*` | RARE |
| `onec_*` | services/onec.rs | Any 1C HTTP-service base URL with Basic auth | legacy |
| Updater | plugin | GitHub releases | ACTIVE |
| Kapital webviews | lib.rs | `b2b(-api).kapitalbank.uz` | UNUSED |
| SQB Python | `cloud_client.py` | `https://bank.aiba.group/api/internal/connector-sync/*` with the bearer from `aiba_token.json` | ACTIVE (spawned at startup) |

The main cloud sync and presence sockets (onec-hub) are in the **renderer**, not in Rust.

---

## Hidden / surprising findings

1. **LAN-exposed, unauthenticated 1C read/write API.** `[::]:55899`, `enableAuth:false`, an inbound firewall rule on every profile, and CORS `*`. Any web page the user opens can also call it through the browser, because CORS allows every origin. (§0.1)
2. **The connector kills its own live adapter** whenever any config or helper command runs `ensure_adapter_dir`, including just opening Settings. The watchdog hides it as a blip of about 1 s plus cold reconnects. This could explain unexplained "adapter restarted" sync gaps. (§0.2, §2.3; code inference, unverified at runtime)
3. **Production builds ship DevTools** (`updater.ps1` uses `--features devtools`). There is also a hidden hotkey to switch the whole app to the **dev cloud** (Ctrl+Shift+D).
4. **The OneScript runtime never updates on existing installs.** Extraction is gated only on a `.extracted` marker.
5. **`taskkill /F /IM oscript.exe /T` is machine-wide** before a runtime re-extract. It hits other users' and customers' oscript processes.
6. **Single-instance is per session, ports are per machine.** On RDP or terminal servers, two users' connectors kill each other's adapters and fight over the signer ports. `SO_REUSEADDR` on Windows lets two routers share 55899.
7. **The comcntr registration lock is in per-user `%TEMP%`,** but the registration it protects is machine-wide.
8. **`write_file` is an almost unrestricted file write** from the renderer, often running elevated.
9. **Plaintext secrets in roughly 25 places:** `config.json` plus 21 backups, `sql-auth.json`, `tunnel.json`, `aiba_token.json`, the `%TEMP%` test input, the Designer `/P` argument, and `get_all_adapter_database_configs` returning every 1C password to the UI. `%APPDATA%` is *Roaming*, so on domain machines the 1C passwords roam.
10. **The tunnel is effectively remote control of the local signer.** Any method and path on `:7777`/`:6210` plus `signMSG`, with a token in the URL. Per-sign USB reader isolation changes global device state.
11. **The elevation prompt fires on almost any machine that ever had a smart-card reader** (a ghost device counts). The relaunch drops the original arguments.
12. **Dead or unused but still registered:**
    - The Kapital Bank headless login, which takes a user password, spoofs the "Uzum Business" app headers and stores the password through the signer.
    - `install_adapter`, which points at an `install.ps1` that does not exist.
    - `usb_test_disable_one`, which disables a reader persistently.
    - `set_adapter_kind`, whose flag is never read.
    - `tunnel_get_config`, which returns the token.
    - Plus `set_com_connector_version`, `list_com_connector_versions`, `parse_valuetable_xml`, `get_sqb_health`, `restart_sqb_sidecar`, `list_readers`, `get_log_file_path`, `set_backend_locale`, `check_network_status`, `get_runtime_environment_status`, `set_adapter_port`, `stop_reports_worker`, `tunnel_set_config`, `ensure_adapter_firewall_rule`.
13. **The event-log change feed is about 4,600 lines of Rust that can never start**, and it supports only the legacy `.lgp` format and local-cluster server bases.
14. **`@tauri-apps/plugin-process` is used in JS but not registered in Rust**, so `relaunch()` after an update would throw.
15. **Bundled ePass PKCS#11 DLL is missing.** `resources/epass` holds only `r2yepass.dll`, which nothing uses. The signer falls back to `System32\eps2003csp11.dll`.
16. **`dev-certificate.pfx` is committed in git** (`apps/app/src-tauri/dev-certificate.pfx`, 2,918 B). Not opened.
17. **A local `resources/1c-adapter/config.json` exists and is untracked.** It contains `host 127.0.0.1` and empty databases. It is bundled into any installer built from this tree (`resources/**/*`). Rust deliberately never seeds from it, but its contents ship.
18. **The `--autostart` argument is written to the Run key but never read**, so autostart always opens the window.
19. **`adapter.log`, `uz-adapter.log`, `partition-*.log` and `reports-*.log` are never rotated.**
20. **Uninstall leaves behind** the Run key, the firewall rule, the comcntr registration, `%APPDATA%` (passwords and backups), and possibly orphan oscript processes.

## Open questions

1. Does killing the live router through `ensure_adapter_dir` actually happen in the field? Check a client `debug.log` for "Killing orphaned adapter process" lines right after Settings-page visits or base additions. Note that this specific message is logged at INFO with category "adapter", and INFO from Rust *is* written to disk.
2. Do production installers have a Sentry DSN baked in? This depends on each release machine's `apps/app/.env`.
3. What is the effective `server.host` on real client configs? The Rust default is `0.0.0.0`, the bundled stub is `127.0.0.1`, and the router ignores `host` and binds `[::]` regardless. Does main.os honour `host` in single-oscript mode, and does it enforce `enableAuth` when that is true?
4. Does the relay at `relay.aiba.uz` restrict which signer paths it forwards? The connector side allows everything.
5. Is the NSIS install per user or per machine in practice? `installMode` is not set in `tauri.conf.json`, so the Tauri default applies.
6. Which 1UZ adapter bind address and authentication? Not audited (.NET source is in `resources/1uz-adapter/src`).
7. How does `main.os --discover` find bases (`ibases.v8i`, registry)? It is outside the Rust scope, and nothing in Rust reads `ibases.v8i`.
8. Is the `onec_*` "Unisoft" HTTP path still reachable for any tenant (`onec/utils/company.ts`)?
9. Is a `log` backend initialised anywhere (for example by a Tauri plugin), which would put the tunnel token from `tunnel_agent.rs:627` into a log?
