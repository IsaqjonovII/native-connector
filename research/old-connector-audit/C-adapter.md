# C — OLD AIBA Connector: 1C adapter route inventory (code-derived)

Investigator C. Read-only. Nothing was run, no endpoint was called. Every claim below comes from reading code. Behaviour I could not prove from code is marked **unverified**.

## 0. Provenance

| Item | Value |
|---|---|
| Checkout | `D:\aiba\connector` |
| Branch | `release` |
| HEAD | `cfab3a1 ci: let the release guard pass for a version that is not published yet` |
| Adapter file | `apps/app/src-tauri/resources/1c-adapter/main.os`: **23,902 lines** (the brief said ~21.5k; the memory note says ~10.8k, so both are stale), 359 procedures/functions |
| `adapter.version` | `auto-171b736460c8c214`, last changed in `bb84bf3` (2026-09-30 08:58) |
| Last `main.os` commit | `28e98b1` (2026-09-30 13:38). This commit changed `main.os` but **not** `adapter.version`. See Hidden findings #1. |
| Tracked files in the adapter dir | `.gitignore`, `adapter-router.exe` (binary, tracked), `adapter.version`, `main.os`. `config.json` exists on disk but is **untracked** (`.gitignore` lists only `config.local.json`). It is a stub: `databases:{}`, `api.enableAuth:false`, `token:"your-secret-token"`. |
| Other `.os` | `docs/test-scripts/*.os`, `scripts/dev/pg-dump.os` (dev only), `.superpowers/sdd/2026-08-07-.../baseline/main.os` (a stale baseline copy), plus OneScript runtime libs. **No modular adapter remains**: the `src/Модули/*.os` and `modules/*.os` trees in memory note `project_connector_layout.md` are gone from HEAD and from branch `opt/onec`. |

### What actually ships and runs

- `tauri.conf.json:60` bundles `resources/**/*`. `ensure_adapter_dir` (`src-tauri/src/handlers/filesystem.rs`) copies the dir into `%APPDATA%\aiba.uz\1c-adapter\`. It force-overwrites `main.os` only when `adapter.version` differs.
- `start_adapter` (`filesystem.rs:1768-1921`):
  - `read_worker_pool_size` (`filesystem.rs:510`) **defaults to 2**, even when `config.json` is missing or unreadable. A per-base `comVersion` forces the value to at least 2.
  - With pool ≥ 2 and `adapter-router.exe` present, the app spawns **`adapter-router.exe --public-port 55899 --base-port 55901 --workers N --oscript … --main-os main.os --parent-pid …`**. This is the **default production topology**: a Rust tiny_http router on the public port, in front of N `oscript main.os` workers on 55901+. Workers run in shard mode (`--shard-of/--worker-id`) or ownership mode (`--own-bases-enc`, protocol 3).
  - Only otherwise does it run `oscript main.os --non-interactive --port 55899` directly (`filesystem.rs:1913-1920`).
- Extra host-spawned oscript workers:
  - reports lane on `adapter_port()+50` = 55949 (`filesystem.rs:2184`, `core/config.ts:62`)
  - cold-read partition workers on `+30` (`filesystem.rs:2038`, `start_partition_workers`)
  - a per-base worker with `--own-bases <db>` (`filesystem.rs:2326`)
- A separate .NET `1uz-adapter.exe` (MS SQL / BePro) runs on 55900 with the "same REST contract". It owns `/api/db/{db}/reconciliation/ledger`, which is **not** in `main.os`. Out of scope, mentioned only where TS calls overlap.
- Router source: `D:\aiba\connector\adapter-router\src\main.rs` (3,345 lines). Last source commit is `0e4ea95` (2026-09-05). The tracked binary was last updated in `b70cf6c` (2026-09-29, "router binary that shipped"). Nothing in the repo ties the binary to that source.

## 1. Request pipeline (main.os)

1. **Accept loop** `ЗапуститьСервер` (`main.os:23247-23444`):
   - `TCPСервер(port)` handles one connection at a time, strictly serially.
   - Each request goes `ПрочитатьHTTPЗапросПолностью` → `РазобратьHTTPЗапрос` → `ОбработатьЗапрос` → `Клиент.ОтправитьСтрока` → close.
   - Every response carries `Connection: close`.
   - In pool mode the port must be the one the router assigned (exactly one attempt). Single mode tries up to 10 ports upward from the start port (`:23260-23281`).
2. **Dispatcher** `ОбработатьЗапрос` (`main.os:22204-23241`) is one big `Если/ИначеЕсли` on `Сегменты[1]` (the "resource").
3. **CORS**: `OPTIONS` → `{}` 200 (`:22212`). Headers: `Access-Control-Allow-Origin: *`, methods `GET, POST, PUT, DELETE, OPTIONS`, headers `Content-Type, Authorization, X-AIBA-Lane` (`СформироватьHTTPОтвет :21376-21405`).
4. **Auth** `ПроверитьАвторизацию` (`:21360-21374`) is optional bearer-token auth. It is OFF unless `config.api.enableAuth=true`, and the Rust default config writes `enableAuth:false` (`filesystem.rs:1101`). No TS caller sends `Authorization`. Turning auth on would break the connector itself.
5. **Global query parsing** (`:22221-22267`):
   - `limit` (default 100), `offset` (default 0), `fields` (`-` = base fields only, CSV = projection), `after` (keyset UUID).
   - Every other query param becomes an equality **filter**, except the service list `limit,offset,fields,post,after,syncOrder,cursorDate,tabular,skipTotal,exchange,hard,bulk,to,from,refs`. That list is matched by **substring** (`СтрНайти`), so any filter key that is a substring of it is silently dropped (e.g. `o`, `to`, `ab`).
6. **Base selection**:
   - `/api/db/{base}/…` sets `ТекущаяБаза` after an `АктивныеБазы` membership check (404 otherwise), then rewrites the segments to `/api/…`. `/api/db/{base}` alone returns base info.
   - **Without `/db/{base}` every route silently runs against `АктивныеБазы[0]`** (`ПолучитьСоединение :2948-2956`, `ПолучитьИмяТекущейБазы :2989`).
7. **Connect gate** (`:22327-22353`):
   - Applies when the resource is in `catalogs,documents,enums,charts,calcTypes,chartsOfCalculationTypes,registers,constants,schema,metadata,openapi,query,businessProcesses,tasks,exchangePlans,characteristics,chartsOfCharacteristicTypes,changes,counts,stock,sotuv,healScan`. This is also a substring match.
   - The gate calls `ПолучитьСоединение("",Истина)`, which runs a liveness probe and auto-reconnects.
   - No connection → 503 `"No database connected"` / `"connection lost"`, or `COM_CONNECT_FAILED`.
   - `COM_VERSION_MISMATCH` is returned with **HTTP 200** and `success:false` so the gateway does not retry it (`СформироватьОшибкуКоннекта :21006`).
8. **Envelope**:
   - Success: `{success:true, data:…}` (`СформироватьУспех :20848`).
   - Error: `{success:false, error}` (`:20855`).
   - Write routes that go through `СформироватьОтветЗаписи` (`:21048`) return **422** when the result has `error`. **Catalog CRUD, info-register writes and document DELETE still use `СформироватьУспех`**, so their failures arrive as `200 success:true, data.error:"…"`.
   - Any uncaught exception → 500.
   - Status codes outside {200,201,400,401,404,500} are written with reason phrase "Unknown" (503/422/405/501).

## 2. Router layer (adapter-router, `main.rs:1927-2276`)

| Path | Handling |
|---|---|
| `OPTIONS *` | 204 with CORS headers, handled locally (`:1941`) |
| `GET /api/status` | **Answered by the router itself and never proxied** (`:1949-2058`). Returns `router, workers, routerVersion, adapterStamp, routerMode, workerProtocol, live, budget, baseWorkers, writeWorkerBases, shardWorkers, bases{restarts,lastError,lastErrorCode}`. The worker's richer `/api/status` (per-base connection errors, connectLock, httpRead) is therefore invisible in pool mode. |
| `GET /api/databases/status`, `GET /api/databases?withStatus…` | Served from a background **cache** built by polling every worker (`build_databases_status :1710`, `serve_databases_status :1901`). |
| everything else | Proxied to `http://127.0.0.1:{worker}{url}`, with Authorization and other headers forwarded. **Shard mode**: worker = hash(base) % N. **Ownership mode**: `ensure_ready_worker` lazily spawns the owner, does LRU, and gives the write lane its own worker (`writeWorker`). Base-less paths go to `meta_worker`. Requests with lane=write, or any method other than GET/HEAD, count as writes (`:2093-2101`). Ownership mode retries up to 3 times on transport failure; otherwise the router returns 502 `router: worker … unreachable`. |
| internal | Router → worker `GET /api/worker/info` handshake (`:1531`) and `GET /api/status` probe (`:674`). |

The router binds **`[::]:55899` dual-stack, which means all interfaces** (`main.rs:2710-2725`). `ensure_adapter_firewall_rule_internal` (`filesystem.rs:1227-1260`) adds `netsh advfirewall … dir=in action=allow protocol=TCP localport=55899` with **no remoteip or profile restriction**. See Security.

## 3. COMPLETE route table (main.os)

`{p}` = optional `/api/db/{base}` prefix. "Caller" = connector caller found by grep of `apps/app/src` (TS) and `src-tauri/src` (Rust). **C** = also reachable from a cloud WebSocket command (section 5). R/W = read-only / mutating.

### 3a. Service / diagnostics

| # | Method + path | Handler (line) | R/W | What it does | Caller / status |
|---|---|---|---|---|---|
| 1 | `GET /` (no segments) | inline `:22271` | R | `{name:"OneScript 1C Universal REST API", version:"3.0", databasesCount, connected}`. Hard-coded "3.0", unrelated to `adapter.version`. | UNUSED |
| 2 | `GET /api/db/{base}` | inline `:22311` | R | `{name, connected:true, apiPrefix}`. No COM touch. | ACTIVE `onec-sync-provider.tsx:12232` (write path) |
| 3 | `GET /api/worker[/…]` | inline `:22358` | R | Router handshake `{protocol:3, ownershipTransport, mode, bases, workerId, workerCount, stamp}` | Router only (`main.rs:1531`) |
| 4 | `GET /api/status` | inline `:22499-22575` | R | Re-reads `config.json` on **every call**. Returns `connected, databasesCount, configuredCount, databases[{name, apiPrefix, connectionType, status, connected, error(full COM text), httpProxyUrl, baseUrl}], comConnections, httpConnections, adapterStamp, workerId, workerCount, httpRead, connectLock, workerMode`. | ACTIVE as liveness ping (27 TS refs, `adapter-gateway.ts`, `filesystem.rs:2270`). In pool mode the router answers instead. |
| 5 | `GET /api/databases`, `/api/bases` | `ПолучитьСписокБаз1С :19952` | R | Parses `ibases.v8i`: name, connectionString, type, server, database, folder, version for every 1C base on the machine. Merged with config state. | Plain form UNUSED in TS. Discovery goes through CLI `--discover` (`filesystem.rs`). |
| 6 | `GET /api/databases/status` (also `/full`, `/all`, `?withStatus=true|1|yes|да|истина`) | `ПолучитьСписокБазСоСтатусом :20096` | R | Discovered + configured bases with connection status | ACTIVE: `utils/adapter-databases.ts:62`, `utils/info-base.ts:31`, `utils/onec-version.ts:130`. In pool mode this is the router cache. |
| 7 | `GET {p}/api/db-structure` | `ПолучитьСтруктуруХраненияБД :21580` | R | Server bases only. Calls `GetDBStorageStructureInfo()` and returns logical↔physical SQL table map plus `sqlServer`, `sqlDb`. Cached per base for the process lifetime. | UNUSED |
| 8 | `GET {p}/api/sql-dump/{Логическая.Таблица}?limit&offset` | inline `:22404-22469` | R | **Direct MSSQL read**: ADO `Trusted_Connection` (`:21568`), `bcp … queryout` launched through `WScript.Shell.Run` (`:21953`), `FOR JSON`, keyset cursor cached in `КэшКурсораSQL`, ADO fallback. Returns `{table, physical, offset, limit, count, method, queryMs, data}`. | UNUSED. `sync-orchestrator.ts:1695` calls it "a dev-time experiment, never a production path". |
| 9 | `GET {p}/api/counts?tables=documents/X,catalogs/Y,registers/info/Z,…` | `ПолучитьПакетныеСчетчики :21436` | R | One `ОБЪЕДИНИТЬ ВСЕ` `КОЛИЧЕСТВО(*)` across many tables. Returns `{counts, missing, invalid, queryMs, batchError?}`. Falls back to per-table counts. | ACTIVE `utils/table-count-checker.ts:284` |
| 10 | `GET /api/changes` (no base) | inline `:22589-22597` | — | **Returns 501.** Fenced: "all-bases summary is disabled to protect the fleet". `ПолучитьСводкуИзменений :19839` is dead code. | DEAD |
| 11 | `GET {p}/api/changes?hours|from|to|events|types|limit` | `ПолучитьИзменения :19668` | R | **Not the event log**, despite the comment "Журнал регистрации". Runs a `ПЕРВЫЕ {limit}` query on **every document type**, by *document date*, then bubble-sorts O(n²). Returns `{changes[{timestamp, objectType, objectName, status: posted/draft/deleted, id, presentation, apiUrl}], mode, note, limitation}`. `events` is parsed and **ignored**. HTTP bases → 400. | UNUSED by the connector. The adapter comment claims "the cloud polls per-base `/changes`", but there is no connector caller and no cloud path to the adapter (unverified for the cloud side). |
| 12 | `GET {p}/api/metadata` | `ПолучитьМетаданные :19904` | R | `{name, synonym, version (configuration version), counts per metadata kind}`. **No platform version field anywhere in the adapter.** | ACTIVE `onec-sync-provider.tsx:4962` (`adapterGetConfigurationVersion`, used by almost every cloud write command) and `utils/onec-version.ts:99` |
| 13 | `GET {p}/api/healScan` | `ПолучитьHealScan :22154` | R | Lists common modules that are event-subscription handlers with ВызовСервера=true and ВнешнееСоединение=false. Returns `{status: ok/needs_fix, modules[], subscriptionCount}`. Diagnosis only; the fix goes through Designer (`heal_extconn.rs`). | ACTIVE `components/adapter/heal-panel.tsx:188` (local UI) |
| 14 | `GET {p}/api/openapi`, `/api/swagger` | `СформироватьOpenAPI :20756` | R | Static-ish OpenAPI 3.0 | UNUSED |

### 3b. Catalogs (Справочники), generic by name

| # | Method + path | Handler | R/W | Args / shape | Caller |
|---|---|---|---|---|---|
| 15 | `GET {p}/api/catalogs` | `ПолучитьСписокСправочников :6245` | R | List of catalog names | UNUSED |
| 16 | `GET {p}/api/catalogs/{name}` | `ПолучитьЭлементыСправочника :6262` | R | `limit`, `offset` (implemented as `ПЕРВЫЕ limit+offset` then skip, so O(N²) without a cursor), `after` (keyset `Ссылка > &ПослеСсылка`), `fields`/`fields=-`, `bulk=1` (XDTO xml + sideCols + sparseCols + row_count), `skipTotal`, `tabular=false`, the special filter `_ownerInn`, and any other key as an `=` filter. **The filter key is concatenated into the query text** (`:6262+194`). Returns `{success, data|xml…, total_count}`. Total count is skipped when filtered. | ACTIVE: sync via `incremental-fetcher.ts:600`; `onec-sync-provider.tsx:2440` (`adapterGetCatalog`), `:4829` (Банки, limit 2000); `org-list.service.ts:132`; `metadata-preload.ts:294`; `documents/page.tsx:1736`; table-count-checker. **C** |
| 17 | `GET {p}/api/catalogs/{name}/{id}` | `ПолучитьЭлементСправочника :6734` | R | Single element by UUID | ACTIVE `:13091`, `:13135`, `:4418`; `incremental-fetcher.ts:1479`. **C** (catalog_read, catalog_update precondition) |
| 18 | `POST {p}/api/catalogs/{name}` | `СоздатьЭлементСправочника :6857` | **W** | Body = attributes, `tabularSections`, nested refs (find-or-create through `НайтиСправочникПоРеквизитам`), EnumRef. Auto `SetNewCode`. **Always writes with `DataExchange.Load=True`** (`:7138`), which bypasses config BeforeWrite/checks. Passport post-hook for Сотрудники. **`_postCreateMethod` directive** (`:7157-7179`), see Security. Returns 201 `{success:true, data:{id, code, name… or error}}`. | ACTIVE `onec-sync-provider.tsx:12987` (`adapterPostCatalog`). **C** (create_ref, employee, department, schedule, catalog_read/document_read side-effects) |
| 19 | `PUT {p}/api/catalogs/{name}/{id}` | `ОбновитьЭлементСправочника :7228` | **W** | Partial attribute update | ACTIVE `:13052`. **C** (catalog_update) |
| 20 | `DELETE {p}/api/catalogs/{name}/{id}[?hard=true]` | `УдалитьЭлементСправочника :7360` | **W** | Default: deletion mark (`SetDeletionMark`). `hard=true|1|yes|да`: **physical `Delete()`**, no reference-integrity check | ACTIVE soft only: `:14284` (departments/employees). The hard catalog delete is UNUSED by the connector. **C** |

### 3c. Documents, generic by name

| # | Method + path | Handler | R/W | Args / shape | Caller |
|---|---|---|---|---|---|
| 21 | `GET {p}/api/documents` | `ПолучитьСписокДокументов :7592` | R | List of document type names | UNUSED |
| 22 | `GET {p}/api/documents/{name}` | `ПолучитьДокументы :7700` | R | `limit` (0 = count only), `offset`; **keyset by `cursorDate` + `syncOrder` asc/desc (default desc)**, returning `next_cursor_date`/`next_cursor_skip`; `after` (UUID keyset); `from`/`to` (window on Дата, `[from,to)`); `tabular=false` (header only); `bulk=1` (XDTO); `skipTotal`; `refs=objects` (refs as `{id,type,name}`); `fields`; filters with aliases `date`→Дата, `posted`→Проведен, `deletionMark`, `number`→Номер (`РазрешитьПолеФильтраДокумента :7673`), plus JSON ref filters (`{ref,type}`). An error now returns 500 instead of an empty 200. | ACTIVE: sync (`incremental-fetcher.ts:600`), `onec-sync-provider.tsx:2419` (`adapterGet`, check_exists), `:5283` (ПоступлениеТоваровУслуг header probe), `onec-read-commands.ts:83` (document_read, `refs=objects`, ≤200), `documents/page.tsx:73`. **C** |
| 23 | `POST {p}/api/documents/{name}/batch` body `{ids:[…]}` | `ПолучитьДокументыПоИдентификаторам :8614` | R | Batch version of #24. Same row shape. Missing ids are simply absent. GET → 405. | ACTIVE `incremental-fetcher.ts:2384` |
| 24 | `GET {p}/api/documents/{name}/{id}` | `ПолучитьДокумент :8488` | R | Header + all `tabularSections` | ACTIVE `onec-sync-provider.tsx:5325` (`adapterGetDocumentById`), `incremental-fetcher.ts:1479`. **C** |
| 25 | `POST {p}/api/documents/{name}[?post=false][&exchange=true]` | `СоздатьДокумент :15520-17538` (~2,000 lines) | **W** | Posts by default (`post` absent = post). Body: attributes, `tabularSections`, refs by id/name/INN with find-or-create. Directives `_idempotencyMarker` (`:15597`) and `_monthlyUpsert` (`:15614-15723`). Bank-doc aliasing, bank-account auto-create, subconto/analytics prediction, VAT and warehouse/department defaults, Комментарий autofill marker. Posts through `ЗаписатьСПроведением` (`:17870`, non-operational mode). Fallbacks on post failure (draft write, re-post, FORCE posted, see Hidden). `exchange=true` → `DataExchange.Load=True` (`:11659`). Response through `СформироватьОтветЗаписи`: 201 or 422 with `{id, number, date, posted, created, idempotent?, monthly?, fillDiagnostics, writeMode, postRetryUsed?, fallbackDocument?, draftContext?, requestContext?}`. | ACTIVE `onec-sync-provider.tsx:4700-4729` (`adapterPostDocument`; callers `:6799`, `:7116`, `:12465`, `:12478`, `:12506`, `:12687`, `:12917`), `documents/page.tsx:2997` (local UI). **C** (write_to_1c, employee_*, fire) |
| 26 | `PUT {p}/api/documents/{name}/{id}[?post=true|false][&exchange][&autoUnpost=true]` | `ОбновитьДокумент :17541` | **W** | Partial update. `autoUnpost` defaults to OFF at the route; the comment documents live data corruption on Kanstik when it is on. | ACTIVE `:5164-5176` (`adapterUpdateDocument`). **C** (update_1c_document, post/unpost commands) |
| 27 | `DELETE {p}/api/documents/{name}/{id}[?hard=true]` | `УдалитьДокумент :18047` | **W** | Mark-deletion by default. `hard` → physical `Delete()`. Bank alias resolution. **Returns 200 success:true even on error** (no `СформироватьОтветЗаписи`). | ACTIVE `:5209-5218`. **C**: `delete_1c_document` passes `payload.hard` through (`:5845`), so **the cloud can hard-delete any document by type+id**. The connector unposts first (`:5857-5865`). |
| 28 | `POST {p}/api/documents/{name}/{id}/post[?exchange=true]` | `ПровестиДокумент :17885` | **W** | Post. `exchange=true` posts in load mode (no movements). | ACTIVE `:5378` (no exchange). **C** (post_1c_document) |
| 29 | `POST {p}/api/documents/{name}/{id}/unpost` | `ОтменитьПроведениеДокумента :18017` | **W** | `Write(ОтменаПроведения)` | ACTIVE `:5124`. **C** (unpost_1c_document, delete_*) |

### 3d. Enums, charts of accounts, calc types

| # | Method + path | Handler | R/W | Notes | Caller |
|---|---|---|---|---|---|
| 30 | `GET {p}/api/enums` | `ПолучитьСписокПеречислений :18125` | R | | UNUSED |
| 31 | `GET {p}/api/enums/{name}` | `ПолучитьЗначенияПеречисления :18141` | R | Values of one enum | ACTIVE `onec-sync-provider.tsx:2460` (`adapterGetEnum`), `metadata-preload.ts:260`, `documents/page.tsx:1707`. **C** (get_list, refdata_get) |
| 32 | `GET {p}/api/charts` | `ПолучитьСписокПлановСчетов :18171` | R | | UNUSED |
| 33 | `GET {p}/api/charts/{name}?limit&offset` | `ПолучитьСчета :18239` | R | Accounts. Only limit/offset (filters are ignored). | ACTIVE: sync (`ChartOfAccounts_*`), `:4901` (Хозрасчетный, limit 5000) in write_to_1c, `metadata-preload.ts:277`, `documents/page.tsx:2398` |
| 34 | `GET {p}/api/calcTypes/{name}`, `/api/chartsOfCalculationTypes/{name}` | `ПолучитьВидыРасчета :18187` | R | limit (default 500) | ACTIVE `:7377` (get_list). **C** |

### 3e. Registers

| # | Method + path | Handler | R/W | Notes | Caller |
|---|---|---|---|---|---|
| 35 | `GET {p}/api/registers` | `ПолучитьСписокРегистров :18314` | R | All registers | UNUSED |
| 36 | `GET {p}/api/registers/{name}` (3 segments) | `ПолучитьДанныеРегистраБухгалтерии :18740` | R | Treats `{name}` as an **accounting** register | UNUSED (TS always uses `registers/accounting/…`) |
| 37 | `GET {p}/api/registers/accounting/{name}` | same `:18740` | R | `limit`, `offset`, `cursorDate`, `syncOrder`, `skipTotal=1` (**only "1"** here), `bulk=1` (XDTO), `to` (upper bound, exclusive, for parallel cold read). Account codes + subconto in rows (`ОбернутьОтветРегистра :18720`). | ACTIVE: sync (`AccountingRegister_*`), `onec_bulk.rs` (ignored live test), cold-read partitions |
| 38 | `GET {p}/api/registers/accumulation/{name}` | `ПолучитьДанныеРегистраНакопления :18532` | R | Same params, but **no skipTotal** | ACTIVE: sync |
| 39 | `GET {p}/api/registers/info/{name}` (alias `information`) | `ПолучитьДанныеРегистраСведений :18365` | R | Same params, no skipTotal | ACTIVE: sync; `onec-sync-provider.tsx:14579-14585` (`adapterFetchInfoRegister`, holidays). **C** |
| 40 | `POST|PUT {p}/api/registers/info/{name}` | `ЗаписатьМенеджерЗаписиРегистра :7516` | **W** | RecordManager upsert. Dims/resources by name; `{enum,value}` and `{catalog,id}` refs; `Период`. Returns 200 success even on error (`data.error`). | ACTIVE POST `:14506` (`adapterWriteInfoRegister`; holidays, departments). **C** |
| 41 | `DELETE {p}/api/registers/info/{name}` (JSON body) | `УдалитьЗаписьРегистраСведений :7558` | **W** | RecordManager delete by key. Ref resolution is **non-strict**: unresolved keys are set raw. | ACTIVE `:14546`. **C** (holiday_delete, department_*) |
| 42 | `GET {p}/api/registers/accumulation/{name}/turnover?from=YYYY-MM-DD&to=YYYY-MM-DD` | `ПолучитьОборотыРозничнойВыручки :19381` | R | `.Обороты` with literal ДАТАВРЕМЯ, daily. Returns `[{day, retail_point, payment_type, sum, qty}]`. Strict: 500 on failure. `periodicity` is accepted but **hard-coded to day**. Non-accumulation → 400. | ACTIVE `turnover-push.ts:56,80` (reports lane :55949, РозничнаяВыручка) |

### 3f. Business-specific

| # | Method + path | Handler | R/W | Notes | Caller |
|---|---|---|---|---|---|
| 43 | `GET {p}/api/stock?accounts=1010,2910,…&date=ISO` | `ПолучитьОстаткиТоваров :19455` | R | Хозрасчетный.Остатки by Номенклатура×Склад×Счет. Uses the default warehouse account set when `accounts` is empty. Errors come back inside a 200. | ACTIVE `features/accounting/lib/sync/stock-sync.ts:67` («Омбор») |
| 44 | `GET {p}/api/sotuv/istochnik-map` | `ПолучитьКартуИсточниковSotuv :22057` | R | Hard-coded query on `Документ.ЗаказПокупателя`. Returns `{rows:[[cpInn, istochnikRef, istochnikName, lastOrderDate]], count}`. Strict 500. | **UNUSED in this checkout.** Only a comment in `core/config.ts:295` mentions it. The caller presumably lives in another branch or in the cloud (unverified). |

### 3g. Arbitrary query / proxy / introspection

| # | Method + path | Handler | R/W | Notes | Caller |
|---|---|---|---|---|---|
| 45 | `POST {p}/api/query` body `{query, params|parameters}` | `ВыполнитьЗапрос :19284` | R (1C query language cannot mutate) | **Any 1C query text** against the selected base. Parameters are set as primitives only. **Errors are swallowed: the route returns `200 success:true, data:[]`** (`:19311-19313`). HTTP bases are proxied to the base's `/query`. | ACTIVE, internal fixed texts only: `onec-sync-provider.tsx:4791` (owner bank suggestion, INN interpolated), `:12159` (доверенность kind, `q1c()`-escaped name). No raw passthrough from the cloud. |
| 46 | `ANY {p}/api/hs/{…}`, `{p}/api/http/{…}` | `HTTPЗапросК1С :1642` | R/W | Raw proxy to the 1C HTTP service of a `type: http|cloud|odata` base. Query string is rebuilt **without encoding**. | UNUSED (no http-type bases in the connector flow) |
| 47 | `GET {p}/api/schema` | `ПолучитьПолнуюСхему :20422` | R | "Light" full schema (names) | ACTIVE `features/accounting/lib/export-data-service.ts:52` |
| 48 | `GET {p}/api/schema/catalogs/{name}` | `ПолучитьСхемуСправочника :20309` | R | Attributes, types, tabular sections | UNUSED in code (dev reference in `utils/nomenclature-ref.ts:291`) |
| 49 | `GET {p}/api/schema/documents/{name}` | `ПолучитьСхемуДокумента :20329` (memoized in `КэшСхемДокументов`) | R | | ACTIVE `onec-sync-provider.tsx:5002`, `metadata-preload.ts:237`, `documents/page.tsx:1690` |
| 50 | `GET {p}/api/schema/registers/{type}/{name}` | `ПолучитьСхемуРегистра :20352` | R | | UNUSED |
| 51 | `GET {p}/api/constants` | `ПолучитьСписокКонстант :19252` | R | | UNUSED |
| 52 | `GET {p}/api/constants/{name}` | `ПолучитьЗначениеКонстанты :19268` | R | Value of **any** constant | UNUSED |
| 53 | `GET {p}/api/processes`, `/businessProcesses` | `ПолучитьСписокБизнесПроцессов :20564` | R | | UNUSED |
| 54 | `GET {p}/api/processes/{name}` | `ПолучитьБизнесПроцессы :20579` | R | limit only | UNUSED |
| 55 | `GET {p}/api/tasks`, `/tasks/{name}` | `:20612`, `:20627` | R | | UNUSED |
| 56 | `GET {p}/api/exchangePlans|exchange`, `/…/{name}` | `:20660`, `:20675` | R | Exchange plan nodes | UNUSED |
| 57 | `GET {p}/api/characteristics|chartsOfCharacteristicTypes`, `/…/{name}` | `:20708`, `:20723` | R | | UNUSED |
| — | anything else | `:23236` | — | 404 `Not found` | |

### 3h. CLI "routes" (argv; `ОбработатьАргументыКоманднойСтроки :23451`)

| Flag | Effect | Caller |
|---|---|---|
| `--discover` | Writes `ibases.v8i` bases as JSON to `discover-result.json` and stdout, then exits | `filesystem.rs` discover_databases |
| `--status` | Prints config status, exits | — |
| `--configure <json>` | **Overwrites the whole config** | — |
| `--add-db <json>` | Appends a base to config, including user/password | — |
| `--test-connections <input.json>` | Batch connect test. NDJSON lines `{name, success, code(auth/license/path/version/permission/network/unknown via КлассифицироватьОшибку1С :2133), message, ibName, durationMs}` plus per-result files in `outputDir` | `filesystem.rs` (test_adapter_connections) |
| `--port`, `--shard-of`, `--worker-id`, `--own-bases-enc` (protocol 3), `--own-bases` (legacy protocol 2), `--non-interactive`, `--help` | runtime | router, `filesystem.rs` |

## 4. Capability notes

**Pagination.** There are four schemes:
1. offset (`ПЕРВЫЕ limit+offset`, then skip in oscript: O(N²))
2. UUID keyset `after` (catalogs and documents)
3. date keyset `cursorDate`+`syncOrder`, plus `next_cursor_date`/`next_cursor_skip` for documents; registers use `cursorDate`+`to`
4. SQL keyset cache in sql-dump

Charts, calcTypes, processes, tasks, exchange and characteristics have only limit/offset. Count-only = `limit=0`. `skipTotal`: catalogs and documents accept `1|true|yes|да|истина`, accounting registers accept only `"1"`, info and accumulation registers ignore it.

**Bulk/XDTO.** `bulk=1` on catalogs, documents and all register kinds returns `{xml, sideCols, sparseCols, row_count}` (`СобратьБулкОтвет :18683`). If bulk fails the route silently falls back to JSON `data`. The TS side enables bulk for all of these (`incremental-fetcher.ts:584-591`).

**Reference resolution.** `НайтиСправочникПоРеквизитам :9761` (and the no-cache variant `:9825`) does find-or-create by id, code, INN or name. Per-write caches: `КэшРазрешенныхСсылокДокумента`, `КэшЯвноСозданных`. Fuzzy nomenclature matching (`НайтиНоменклатуруПоНечеткомуИмени :9335`, `ПорогСхожестиИмен=0.70`). Group auto-pick (`ПодобратьГруппуНоменклатурыПоТексту :9074`). Deleted-marked items are auto-recreated. Diagnostics surface in `fillDiagnostics`.

**Existence checks.** There is no dedicated route. They are composed from the document list with filters (`check_exists` → `adapterGet` documents list, `tabular=false`) plus marker lookups inside the create path.

**Idempotency and markers.**
- `_idempotencyMarker` → `НайтиСуществующийДокументПоМаркеру :12716`, which runs `Комментарий ПОДОБНО "%<escaped>:%"`. The trailing `:` boundary is dropped when the marker ends in `]`. Fixed on 2026-09-30 to escape `[`, after **543 creates in a row returned the same unrelated document on Kanstik**.
- `_monthlyUpsert` → `НайтиМесячныеДокументыПоМаркеру :12794` (requires a `AIBA_…` marker without `:`, in a period window, excluding deletion-marked docs). It **does not escape `[`**; this is safe only because monthly markers must start with `AIBA_`.
- Autofill comment: "Создано автоматически сервисом AIBA … [AIBA_AUTOFILL]" (`:5330-5401`). The subconto-learning gate excludes any comment containing `AIBA_`.

**exchange=true.** Sets `DataExchange.Load=True` (`:11659-11719`). The connector **always** sends it for 9 HR document types: НачислениеЗарплаты…, НачислениеОтпуска…, НачислениеПоБольничномуЛисту, Отпуск, БольничныйЛист, ВозвратНаРаботу(Организаций), Увольнение(ИзОрганизаций) (`onec-sync-provider.tsx:4688-4715`). This directly contradicts the workspace hard rule "Never use exchange=true on a 1C write".

**Write gates and concurrency.**
- Inside the adapter, a single thread per worker means each request is atomic per base (monthly find-or-create relies on this).
- Connector: a global `beginWrite()` gate for ~18 write actions (`onec-sync-provider.tsx:14884-14901`) parks sync. The gateway lanes are `write`, `writePrep`, `urgent` and `normal`.
- Router: an optional per-base `writeWorker` split. The TS side does not stamp `X-AIBA-Lane` (per repo CLAUDE.md).

**Connection lifecycle.**
- One `V83.ComConnector` per process (`ПолучитьОбщийКоннектор :1817`), never reset on a single base's failure (`:2429-2441`).
- Lazy connect when enabled bases > `lazyConnectThreshold` (default 3) (`:2474`). Ownership mode connects eagerly.
- Liveness = `Соединение.Метаданные.Имя` (`СоединениеЖиво :1064`), checked once per request, with auto-reconnect and a circuit-breaker cooldown (`ПодключениеВОстывании :2271`).
- File-base connects are serialized machine-wide by a lock file (`ЗахватитьБлокировкуКоннекта :2287`; config `connectLock`).
- LRU eviction of COM sessions above `МаксЖивыхCOMСоединений=25` (`:1939`).
- There is **no explicit connect/disconnect/reset route**. Reset = kill and respawn the worker (router/Rust).

**Caching (process lifetime, never invalidated except by restart or LRU).**
- `КэшСхемДокументов` (schema per base|doc). A config change in 1C keeps the stale schema until restart.
- `КэшСтруктурыХранения`, `КэшКурсораSQL`.
- `КэшСтатистикиСубконто` and `КэшПрактикиСубконто` (history-learned predictions).
- `КэшГруппСправочников` (a live COM ref, flushed per base on release).

**Error classification.**
- Connect errors: `COM_VERSION_MISMATCH` vs `COM_CONNECT_FAILED`, parsing versions from the text (`:20866-21033`).
- CLI test: `КлассифицироватьОшибку1С`. Note that "не найден" and "not found" both map to `network`.
- Write errors: 422 plus 1C text with `fillDiagnostics`.

**Event log.** There is **no event-log route in the adapter.** Real ЖР access is the Rust side (`src-tauri/src/handlers/eventlog.rs`, 4,588 lines, and `eventlog_discovery.rs`), which reads files directly. `/changes` is a document-date scan.

**Version info.**
- Adapter build: `adapterStamp` in `/api/status` and `/api/worker`.
- Configuration name/version: `/metadata`.
- Platform version: not exposed. Inferred only from COM errors or the router's `comVersion`.
- Root `/` reports a hard-coded "v3.0".

## 5. Cloud-reachable vs local-only

The cloud reaches the adapter **only indirectly**: WebSocket command → `dispatchControlCommand` (`onec-sync-provider.tsx:14905-15138`) → fixed adapter helpers. There is no generic path, query or SQL passthrough from cloud payloads (grep found no `payload.path/query/url`).

| Cloud action | Adapter routes it drives |
|---|---|
| `ping`/`server_ping`, `get_bases`, `connector_status_request` | none (local state) |
| `sync_now`/`pull_now` | the whole sync read set (#16, #22–24, #33, #37–39, #9, #47 via orchestrator) |
| `check_exists` | #22 (documents list, filters), #17 |
| `write_to_1c`/`write_doc_to_1c` | #12, #33 (Хозрасчетный), #16 (Банки), #45 (/query), #2, #22, **#25 POST documents** (+ `_idempotencyMarker`, `_monthlyUpsert`, `exchange` for HR), #18 (catalog find-or-create inside the adapter) |
| `post_1c_document`, `unpost_1c_document`, `update_1c_document`, `delete_1c_document` | #24, #26, #28, #29, **#27 with `hard` from payload** |
| `delete_from_1c`/`delete_doc_from_1c` | #16, #18, #29, #27 (AIBA-marked HR docs) |
| `employee_write_to_1c`, `employee_fire_from_1c` | #12, #16, #18, #25 (hire/fire docs, exchange for Увольнение) |
| `department_*`, `push_to_1c`/`create_in_1c` (departments/schedules), `list_resource` (departments/employees/schedules) | #16, #17, #18, #20 (soft), #40, #41 |
| `holiday_push/delete/check` | #39, #40, #41 |
| `get_list`, `refdata_get` | #16, #31, #34, #16 Банки |
| `create_ref` | **#18 with cloud `extras` spread raw into the body** (`onec-sync-provider.tsx:7220`) |
| `catalog_update` | #17, #19 |
| `document_read` | #22 with `refs=objects`, ≤200 rows (`onec-read-commands.ts:57-84`) |
| `catalog_read` | #16, #17 |

Local-only callers (UI/background, not cloud-triggered): #6, #9, #13, #42, #43, #47, #49, the `documents/page.tsx` manual write page (#25 via `adapterPost`), and export-data-service.

Routes with no caller anywhere in this checkout: #1, #5 (HTTP form), #7, #8, #10 (dead, 501), #11, #14, #15, #21, #30, #32, #35, #36, #44, #46, #48, #50–57, and the hard variant of #20.

## Hidden / surprising findings

1. **HEAD `main.os` is not stamped.** `28e98b1` (2026-09-30 13:38, group-matcher fix) changed `main.os` without bumping `adapter.version` (last bumped in `bb84bf3`, 08:58 the same day). Under the repo's own rule (`ensure_adapter_dir` force-copies only when the stamp differs), a release built from HEAD will **not** deliver that fix to existing installs. It may have been left unbumped on purpose (unverified change). I did not recompute the hash.
2. **FORCE-posted bank documents with no movements.** In `СоздатьДокумент`, when posting an **incoming bank document** fails with an accounting-policy error, or whenever `exchange=true`, the adapter sets `Posted=True` under `DataExchange.Load=True` and writes, then tries `Write(Проведение, Неоперативный)` in load mode (`main.os:17194-17252`). The result is a document flagged posted that most likely carries no register movements. This contradicts the "провёл with real проводки" expectation.
3. **HR documents are always written in exchange/load mode.** `DOCS_REQUIRING_EXCHANGE_MODE` (`onec-sync-provider.tsx:4688-4715`) sets `exchange=true` for payroll, vacation, sick-leave, return-to-work and dismissal. Posting in Load mode skips `ОбработкаПроведения`. The adapter's own comment at `main.os:17011-17013` says forced exchange "провёл бы его «пустым» (без движений)". This breaks the workspace hard rule. The real movement effect is **unverified** live.
4. **A failed "create+post" leaves a draft in the client base.** The fallback `Write(Запись)` keeps it (`main.os:17051-17062`), and the response now returns its id. Any caller that ignores `fallbackDocument` accumulates junk.
5. **`/changes` is not the event log.** It scans every document type by document date, `events` is ignored, and it bubble-sorts. `GET /api/changes` without a base is hard-disabled (501).
6. **`/query` swallows errors** (`ВыполнитьЗапрос :19311`). A broken query returns `200 success:true data:[]`. This is the "silent-empty" class already in memory. A strict twin `ВыполнитьЗапросСтрого` exists but only turnover uses it.
7. **Catalog creates always run in `DataExchange.Load=True`** (`:7138`), so configuration BeforeWrite/OnWrite handlers and checks are skipped for every catalog element the connector creates (counterparties, nomenclature, employees, …).
8. **The `_postCreateMethod` bug.** For `Module.Method` the common-module procedure is invoked **twice**: once with no args, then with the ref (`:7165-7170`).
9. **Half the write routes still lie about failure.** Catalog POST/PUT/DELETE, info-register POST/DELETE and document DELETE return `200/201 success:true` with `data.error` (they do not use `СформироватьОтветЗаписи`). TS helpers must inspect `data.error`.
10. **Requests without a base prefix silently run against base #0** (`ПолучитьСоединение :2954`). Combined with the router's base-less `meta_worker`, a missing prefix hits whichever base that worker loaded first.
11. **Substring matching in the dispatcher.** The service-param exclusion (`СтрНайти(СлужебныеПараметры, Ключ)`) and the connect-gate list both match substrings. Legitimate short filter keys (`o`, `to`, `ab`, …) are dropped silently.
12. **The router hides the worker's `/api/status`.** In pool mode (the default) per-base COM error texts, `connectLock` and `httpRead` are never visible to callers. Only router health is.
13. **Idempotency lookup fails open.** An exception in the marker search is logged and the write proceeds as a new create (`:12764-12766`). The single-doc lookup also matches **deletion-marked** documents (no `НЕ ПометкаУдаления`, unlike the monthly variant), so a retry can "reuse" a document the accountant deleted.
14. **`turnover?periodicity=` is accepted but ignored**; only day periodicity exists.
15. **`sotuv/istochnik-map` and `db-structure`/`sql-dump` have no caller in this checkout.** sql-dump shells out to `bcp` through `WScript.Shell` with Windows trusted auth.
16. **Repo hygiene.** The memory note describing `main.os` as ~10.8k lines with modular `src/Модули` and `modules/` trees is stale; only the monolith exists. The `1uz-adapter/README.md` still says port 55899, but code spawns it on 55900 (`filesystem.rs:1563`). `adapter-router.exe` is a tracked binary with no build link to its source.
17. **"Enable auth" is a trap.** The adapter supports a bearer token, but no connector code sends one, so setting `enableAuth:true` breaks the connector.

## Security concerns

1. **The adapter is network-exposed with no authentication.** The router binds `[::]:55899` on all interfaces (`main.rs:2717-2725`). The app installs an inbound firewall **allow** rule for TCP 55899 with no remote-IP or profile scope (`filesystem.rs:1250-1253`). Auth is off by default and unused. CORS is `*`. So, in code, any LAN host (and any web page via CORS) can:
   - run arbitrary 1C queries (`POST /api/db/{b}/query`), which reads all accounting data
   - read any constant
   - list every base's connection string on the machine (`/api/databases`)
   - **create, post, unpost, update and hard-delete documents and catalog items**
   - write and delete information-register records
   - call `/healScan`

   Live reachability is **unverified** (another firewall policy may block it).
2. **The cloud can invoke arbitrary 1C common-module procedures.** `create_ref` spreads cloud `extras` raw into the catalog POST body (`onec-sync-provider.tsx:7220`). `СоздатьЭлементСправочника` honours `_postCreateMethod`: `"Module.Method"` → `ОбщиеМодули.<Module>.<Method>()` and `(<ref>)`, or a method on the new object (`main.os:7157-7179`). Any server-side procedure with 0 or 1 args in the client's config becomes callable by whoever controls the cloud payload, or by any LAN host through the open port.
3. **The cloud can hard-delete documents.** `delete_1c_document` forwards `payload.hard` to `DELETE …?hard=true`, which does a physical `Delete()` with no reference check. Per CLAUDE.md, `onec_delete_document` is excluded from MCP grants, but the WebSocket path has no such exclusion.
4. **Query-text concatenation.**
   - Catalog and document filter **keys** go into the 1C query text (`ПолучитьЭлементыСправочника :6262+194`). Document and table names are concatenated in many queries.
   - The connector interpolates INN and counterparty names into `/query` text (`onec-sync-provider.tsx:4791`, `:12159`; the name goes through `q1c()`, the INN is digit-stripped).
   - The 1C query language is read-only, so the impact is data exposure and DoS, not mutation.
5. **sql-dump runs a shell command line** (`bcp "<SQL>" queryout …` through `WScript.Shell.Run`, `:21955`). The SQL is built from storage-map physical names plus cached cursor literals, so it is not directly user text, but the literals come from DB values (a string key containing `"` could break out). The route is unused but compiled in and reachable over HTTP.
6. **Credentials.**
   - 1C user and password are kept plaintext in `%APPDATA%\aiba.uz\1c-adapter\config.json`. `--add-db`/`--configure` take them on argv, which shows up in process listings.
   - The connection string adds `Pwd="…"` without escaping (`:2369`).
   - `/api/status` (worker) and `databases/status` return full COM error texts. These normally do not contain the password (not verified for every platform message).
   - The tracked stub has `token:"your-secret-token"` (a placeholder, not a secret).
7. **HS proxy (`/hs`, `/http`)** forwards arbitrary method, path and query to configured cloud 1C bases with stored credentials (unused, but compiled in and reachable).

## Open questions

1. Is `28e98b1` intentionally unstamped (unverified fix), or will the next release stamp it? Do client installs currently run the `bb84bf3` content?
2. Do the HR documents written with `exchange=true` actually have register movements in client bases? Check one hire, payroll and dismissal written by the connector against `Хозрасчетный` and the payroll registers.
3. How many incoming bank documents in client bases went through the `[CREATE][FORCE]` path (grep `adapter.log` for `[CREATE][FORCE]`)? Each one is posted with no movements.
4. Who calls `GET /api/db/{b}/sotuv/istochnik-map` and `/changes`? The adapter comment says "the cloud polls per-base /changes", yet the cloud has no direct route to the adapter. Is there a branch (`onec-next`, `feat/onec-read-refs`) or a cloud relay that uses them?
5. Is the inbound firewall rule plus the `[::]` bind intended (LAN/RDP topology where other machines call the adapter), or should both be localhost-only? Does any deployment rely on remote access to 55899?
6. Does the cloud (backend/1c or aiba-next onec) ever send `_postCreateMethod` inside `create_ref.extras`, or `hard:true` in `delete_1c_document`? If not, both should be stripped or refused on the connector side.
7. Is `adapter-router.exe` at `b70cf6c` built from `adapter-router/src` at `0e4ea95`? There is no checksum or build link in the repo.
