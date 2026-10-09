# F — Reference presentations go stale after a catalog rename

Read-only audit, 2026-10-08. Checkouts read: `1c-arch/src` (working tree), `next-modules/_wt-onec-sync-v1` @ `4172845`,
`aiba-next/backend` @ `20604869`, `connector` (working tree), `backend/1c` `origin/production` @ `9b4a50b`,
`kansler/backend` (not a git checkout here; working files only).

## Verdict

- On every sync read path, the Host writes each reference as its **display text** (Наименование → Код → Номер → GUID).
  The only stable GUIDs in a row are: the row's own `id`, `orgRef`, `recorderRef`, and `naturalKey`
  (independent information registers only).
- The old Connector (`main.os`) does exactly the same, so the old Python path has the **same staleness**.
  The old Connector also uploads documents and register rows only when their key is new.
- Nothing in the Rust module, the Python backend or the Sync re-resolves names after a rename. The Rust `cp_key` is
  built from the **name** (falls back to `Контрагент_Key`, which the connector never writes).
- Real wrong results follow (§3): sotuv's duplicate-order guard misses, the match index loses the INN, recon splits
  balances, payroll splits people, the contract lookup misses, and kansler debts refuses the row.
- Recommended path (§6): (a) the Host emits `<Field>_Key` (plus `<Field>_Type` for composite fields), behind an
  opt-in flag. Then (b): consumers key on the GUID and read the display name from the catalog at read time.
  One-time backfill. No rescan of documents on any later rename.

---

## 1. Host: how each read path writes a reference

All paths render values through `LegacyValue.Read` (`OneC.Host/LegacyValue.cs:53-120`):
- empty reference → `null`, else Наименование → trimmed Код → Номер → XML string.
- The XML string is the GUID for a ref, or the value name for an enum.
- Wide or composite types (>4 ref types, `MetadataShapes.cs:162-163,192-193`) go per value through `RefBatch.Defer`
  (`RefBatch.cs:46-71`). That holds `DeferredRef(Table, Guid)` and is later **replaced** by the looked-up name
  (`RefBatch.cs:81-96`, `:117-121`). So the GUID is known inside the Host and then thrown away.
- For `PureRef` attributes the raw column is not even read (`LegacyValue.cs:71-76`). It is still selected in the
  query text (`LegacyValue.cs:32`).

| Path (sync caller) | Host code | GUIDs in the row | Presentation only |
|---|---|---|---|
| Document list (`SupervisorReader.DocumentPageAsync`, `SupervisorReader.cs:39-52`: `withVersion`, no ref flag) | `DocumentReadService.ExecuteList` `:129-220` → `ReadRows` `:240-265` | `id` `:252`, `orgRef` (GUID of the Организация attribute) `:259-260` | every other header attribute `:257-258`; every tabular-section column (`QueryKit.cs:143-144`) |
| Document by ids (`ByIdsAsync`, `SupervisorReader.cs:87-88`) | `DocumentReadService.ByIds` `:91-127`, same `ReadRows` and `TabularSections.Load` | `id`, `orgRef` | same as list |
| Catalog page / by id (`SupervisorReader.cs:28-37`, `:89-97`) | `CatalogReadService.ReadRows` `:135-181` | `id` `:157`, `orgRef` `:168-169` | `parent` `:162`, `Владелец` (`ПРЕДСТАВЛЕНИЕ`, `:165`, `:195`), every attribute `:166-167`, tabular sections |
| Register page (`RegisterPageAsync`, `SupervisorReader.cs:54-68`, `syncKeys=true`) | `RegisterReadService.ReadRows` `:223-277` | `recorderRef` `:256-257`, `orgRef` `:262-263`, `lineNo` `:259-260`; accounting `СчетДтКод/СчетКтКод` (code, not GUID) `:253-254`, `:312` | `Регистратор` (as Номер), `СчетДт/СчетКт` (Наименование), `СубконтоДт/Кт1..3`, `ВидСубконто*`, `Номенклатура`, `Склад`, … (`:249`) |
| Movements by recorder (`MovementsAsync`, `SupervisorReader.cs:125-136`) | `RegisterReadService.ByRecorder` `:158-181` → same `ReadRows` | same as register page | same |
| Independent info register (`syncKeys`) | `RegisterKeyset.Page` | `naturalKey` = `Type:XMLText` per dimension, so ref dimensions as GUID (`RegisterKeyset.cs:12-17`, `:77-78`) | the dimension/resource columns themselves |
| `ДокументыФизическихЛиц.Физлицо` | `RegisterReadService.Person` `:283-298` | embedded catalog row with `id` | its attributes |

**Existing ref options**
- `syncKeys` (`Operations.cs:315`, `RegisterReadService.cs:33-38`) only adds `recorderRef`, `lineNo` and `orgRef`.
- `withVersion` only adds `dataVersion`.
- The only GUID-mode switch is `refs=text|guid|both` on the generic `Ops.Read` (`Operations.cs:178-184`,
  `ReadService.cs:64-90`). It is a different row shape (`{ref,text}` objects, `ПРЕДСТАВЛЕНИЕ`), and the sync never
  calls it.

**The `orgRef` mechanism generalises directly.**
- `QueryKit.OrgGuid(cursor, alias, ctx)` (`QueryKit.cs:66-73`) does one COM `Get` of the raw column plus one
  `XMLСтрока` (`OneCValue.RefGuid`, `OneCValue.cs:36-40`).
- Every reference attribute already has its raw column under the same alias.
- So `<field>_Key` = `OrgGuid(cursor, "a"+i)` at `DocumentReadService.cs:258` / `CatalogReadService.cs:167` /
  `QueryKit.cs:144` / `RegisterReadService.cs:249`.
- For `RefBatch` values, the GUID is already in `DeferredRef` and needs no extra call.
- Cost: +1–2 COM calls per filled, non-enum ref value on the `DerefName` path. The Host was built to avoid per-value
  COM calls (`RefBatch.cs:9-20`), so measure on KAN before turning it on.

**D33 shape.**
- Sync rows are meant to stay byte-identical to the old adapter (`DECISIONS.md:445`;
  `RegisterReadService.cs:36` "Off by default so the old adapter's rows stay byte-identical").
- A new key must therefore be opt-in, the same way `syncKeys` and `withVersion` are.

## 2. Old Connector (`connector/.../1c-adapter/main.os`)

- Value conversion: `ПреобразоватьЗначение` `main.os:3053-3140` returns `Строка()` → Наименование → Код → Номер →
  GUID → XML. Presentation only.
- Documents, per-cell path: `main.os:8250` (every attribute through `ПреобразоватьЗначениеПоРежиму` with an empty
  mode, which is plain presentation). The bulk XDTO path keeps GUIDs only for `id` and `orgRef`
  (`БулкГуидПоля("id","orgRef")`, `main.os:8138`). The bulk path is presentations only (`main.os:7704-7708`).
- `orgRef` is added next to the name at `main.os:8255-8266`, `8548-8563`, `8696-8705`.
- Catalogs: `orgRef` only (`main.os:6438`, `6510`, `6639`).
- Accounting register:
  - bulk SQL `main.os:19146-19175`: `Регистратор.Номер` + `recorderRef` GUID, `СчетДт.Наименование` + `СчетДтКод`,
    `СубконтоДт1..3` resolved to names, `ПРЕДСТАВЛЕНИЕ(Организация)` + `orgRef`.
  - per-cell `main.os:19305-19334` adds `recorderRef` / `orgRef` only.
- `refs=objects` (`{id,type,name}`) exists (`main.os:3220-3250`, list route only `main.os:23122-23130`). It is used
  only by the EDO-draft `document_read` relay command (`src/providers/onec-read-commands.ts:49-63`). The sync never
  sends it (comment at `main.os:23123-23124`).
- **No `<Field>_Key` anywhere** in `main.os`. `_Key` comes only from the clobus/cloud **OData** sync
  (`kansler/.../debts/onec_source.rs:673-676`; `backend/1c` `reconciliation.py:729-744` on `origin/production`).
- Change detection in the old connector makes it worse:
  - documents upload on **new key only** (`features/accounting/lib/sync/change-detect.ts:10-13`);
  - registers use presence-only diff (`:45-48`);
  - only `Catalog_Контрагенты/Банки/Номенклатура` are hash-diffed (`sync-orchestrator.ts:130-152`).
  - So the old Python path keeps renamed names in documents and movements forever, the same as the new path.

## 3. Consumers that read reference presentations from document or register rows

Effect of a stale name: **WRONG** = wrong answer or wrong write, **FAIL-CLOSED** = refused or blocked,
**DISPLAY** = shows the old text.

| Consumer | Field | What it does | Stale effect |
|---|---|---|---|
| sotuv `order_index_for_buyer` (`aiba-next accounting/sotuv.rs:3417-3470`) → Rust `by-type` filter `raw->>'Контрагент' = name` (`_wt-onec-sync-v1/src/entity.rs:1204-1206`) | `Document_ЗаказПокупателя.Контрагент` | INN → **current** catalog name (`onec_name_for_inn`, `sotuv.rs:3335`), then exact name equality on orders | **WRONG.** Orders under the old name are not found and read as "no order". The code says "a miss is what licenses a write" (`sotuv.rs:3196-3198`, `:3207-3208`). Duplicate-order risk for orders entered in 1C (our own writes are covered by `km.prixod_dispatch`). |
| Match index build (`entity.rs:1957-2020`) → `avtoprovodka_1c::find_match` (`avtoprovodka_1c.rs:497-537`) | sales/purchase/order `Контрагент` | name → INN through the counterparty catalog (`entity.rs:2015-2020`); the match is INN+sum+date only | **WRONG.** The old name is not in the catalog, so INN is "" and the invoice is never matched ("1C da yo'q"). If the old name now belongs to another card, the INN is wrong. GUID fallback is dead: documents have no `Контрагент_Key`, and the catalog map reads `raw->>'Ref_Key'` (`entity.rs:1959`) while Host catalog rows carry `id`. |
| Bank PO scan: `avtoprovodka_1c.rs:2030-2040`, `kansler_bank_1c.rs:2075-2097` (rows from `bank_index.rs:113-117`) | `PAYMENT_ORDER_*.Контрагент` | name-prefix → INN, else name_norm match with the bank txn | **WRONG.** Incoming orders lose the INN (only outgoing orders have `ИННПолучателя`), so a false "not in 1C". |
| recon (`_wt-onec-sync-v1/src/recon.rs`) | AR `СубконтоДт/Кт1..3` where `ВидСубконто*` contains "Контрагент" (`:393-440`, `:694-722`); document `Контрагент` via `cp_key` (`:525-531`, stored column `store.rs:169`) | `GROUP BY cp`, catalog lookup by name (`load_catalog` `:470-499`, `resolve_cp` `:502`); per-counterparty filter = current name + GUIDs (`cp_match_values` `:512-520`, used `:927`, `:1369`) | **WRONG.** One counterparty's balance splits into an old-name group (no INN, not linked) and a new-name group. The statement for the current name drops all old-name movements, so the сальдо is wrong. |
| last-by-counterparty (`entity.rs:1350-1400` whitelist; direct `:1529-1540` on `cp_key = name`; scan `GROUP BY cp_key` `:1773-1812`) → sotuv history (`sotuv.rs:5855-5880`, `6045-6075`) | key `Контрагент`; values `Склад`, `ДоговорКонтрагента`, `ТипЦен`, `ПодразделениеОрганизации`, `ИсточникИнформации…`, `КонтактноеЛицо`, `ТорговаяТочка` (names) | buyer's last-used defaults, then name → ref through the catalog (`sotuv.rs:6852-6865`) | **FAIL-CLOSED / lost history.** A renamed buyer finds no history. A renamed Склад/ТипЦен/source name does not resolve, so a Miss → blocker. It cannot auto-create, by design. |
| Rust `resolve_contract` (`write.rs:455-534`) | **catalog** `ДоговорыКонтрагентов.Владелец` (`ПРЕДСТАВЛЕНИЕ`, Host `CatalogReadService.cs:195`) | owner name equality with the counterparty's current `Наименование` (`write.rs:486-495`) | **WRONG.** "has no contract", and the connector falls back to «Основной договор». Same staleness, catalog → catalog. |
| osv payroll (`aiba-next surface/osv.rs:2272-2273`, `:2402-2420`) | payroll lines `ФизическоеЛицо/Сотрудник/…` | groups `(fio, month)` by name | **WRONG.** A renamed employee becomes two people across months. |
| kansler debts `identifies` (`kansler api/debts/onec_source.rs:678-690`, `:724-747`) | document `Контрагент` (or `_Key`) | folded name must equal the row's counterparty name or code | **FAIL-CLOSED.** The document is refused as "another counterparty's". |
| `po_to_entry` (`aiba-next accounting/avtoprovodka.rs:2610`, `kansler-bank/kansler_bank.rs:2615`) | `Контрагент` | `counterparty_name` for the SPA | **DISPLAY** |
| Warehouse stock (`warehouse.rs:5690-5711`, `warehouse_stock.rs:1170-1190`) | `STOCK_BALANCE.Номенклатура/Склад` (1uz service) | aggregate by name | Different source (snapshot aggregate, main.os:19976); rebuilt whole each time, so it is low-risk. |

**Consumers that already read `_Key` from document rows**

| Consumer | What it reads |
|---|---|
| `kansler_bank_write::learn_from_history` (`aiba-next kansler-bank/kansler_bank_write.rs:1905-1990`, helper `raw_key` `:2003-2011`) | `Контрагент_Key`, `ДоговорКонтрагента_Key`, `СтатьяДвиженияДенежныхСредств_Key`, … on `PAYMENT_ORDER_*` |
| recon `cp_key()` (`recon.rs:525-526`) | fallback only |
| store `cp_key` generated column (`store.rs:169`) | fallback only |
| kansler debts (`onec_source.rs:678-690`) | fallback only |
| Python `_cp_key_expr` (`reconciliation.py:729-744`) | fallback only |

- Only the clobus OData sync writes those fields. On Connector-synced bases (old or new) they are absent, so
  `learn_from_history` always matches nothing (`Learned::default()`).
- Enum-valued fields (`СтавкаНДС`, `ВидОперации`) are value names, not catalog presentations, so renames do not
  affect them.

## 4. Rust module: GUIDs stored for references

- `onec.entity_data` columns are `raw`, `data_hash`, `ref_id`, `row_key`, `row_hash`, `source_version`,
  `recorder_key` (`store.rs:110-122`, `:218-221`).
- `ref_id` is the row's own `id`/`Ref_Key` (`entity.rs:118-121`). `recorder_key` is the recorder GUID.
- **No column or index holds referenced GUIDs.**
- `cp_key` = `NULLIF(btrim(COALESCE(raw->>'Контрагент', raw->>'Контрагент_Key')),'')` STORED (`store.rs:169`), so it
  is **name-based** for every Connector row.
- Changing that expression rewrites the table (`store.rs:164-168`: 3 m 32 s / 3.5 M rows, ACCESS EXCLUSIVE).
- Upsert behaviour (`sync.rs:589-611`):
  - only `version < stored` is refused as stale;
  - same version with a different hash is an update;
  - so a forced re-read of an unchanged document is accepted.

## 4b. How many rows one rename makes stale (measured 2026-10-08, local Rust copy of KAN)

KAN in local Rust (connections 30/31/32; documents and the register windowed from 2026-06-01, so about
4 months of history — a full history multiplies these):

| What | Measured |
|---|---|
| Documents with a `Контрагент` (3 types, 1 683 docs) | 462 distinct names; rows per name p50 1, p95 12, **max 124** |
| `Хозрасчетный` register `Субконто*` values (27 141 lines → 162 846 values) | 4 594 distinct; rows per value p95 59, **max 32 131** |
| bilim (R9, 2026-10-08) | one `Номенклатура` rename → 20 `ПоступлениеТоваровУслуг` rows wrong |

So a rescan per rename (option c) is bounded per counterparty but unbounded for common subconto values
(one value sits in 32k register lines of a 4-month window), which is why it is not the recommendation.
Rows are not wrong in place: they hold the name as of their last write in 1C, which only becomes
wrong when consumers compare it with the CURRENT catalog name.

Verified by the main thread (not only by the agent): `RefBatch.cs:88` replaces a known GUID with the
name; `store.rs:169` `cp_key` is name-first; `sotuv.rs:3205-3208` ("a miss is what licenses a write").

## 5. Python `backend/1c` (`origin/production`)

- No re-resolution on a catalog rename: no `update_many` on `rawData.<ref>`, and no rename handling.
- The grouping key is `_cp_key_expr` = `$ifNull[rawData.Контрагент, rawData.Контрагент_Key]` (`reconciliation.py:729-744`).
- The catalog is indexed by both GUID and name (`reconciliation.py:560-575`).
- `match_index.py:426` and `doc_rows.py:183` read `cp`/`cpk` the same way.
- So it has the same staleness for Connector bases. The `_Key` paths only help clobus.

## 6. Options

| Option | What it takes | Verdict |
|---|---|---|
| **(a) Host emits stable refs** | Opt-in flag (`withRefs`) on `document`, `catalog`, `register` ops; SupervisorReader always sends it | **Do it.** It is the only option that gives the backend an identity to join on. |
| **(b) Resolve at read time** | Consumers key on `<Field>_Key` and read the current name from the catalog row by `id` | **Do it, on top of (a).** Catalog rows already stay fresh: a rename is one catalog row update. Without (a), (b) is impossible. |
| (c) Re-read only the rows that reference a changed GUID | Needs stored refs (a) plus a reverse lookup (d), plus a force flag past the version skip in `SyncObjectHandler` (`ObjectHandlers.cs:47-49`; a rename does not change the document's ВерсияДанных) | Only needed if `raw` presentations must be literally current. Not needed for the consumers in §3 once they use (b). |
| (d) Reference index table (GUID → rows) | `onec.entity_ref(onec_id, ref, entity_data_id)` kept in sync on every insert/update (N rows per document), or a GIN index on a generated refs array | Heavy write amplification for little gain. Skip unless (c) is required. |

### (a) Shape

**Field naming:** `<Field>_Key` = lower-case GUID, and `<Field>_Type` = XML type name (`CatalogRef.Контрагенты`) for
composite fields (`Субконто*`, `Регистратор`, "any ref" attributes).
- This is the OData convention that §3 consumers already read (`raw_key`, `cp_key` fallback, Python `_cp_key_expr`).
- `kansler_bank_write::learn_from_history` starts working on Connector bases with no code change.
- The name-first `COALESCE`s keep their current behaviour, because `Контрагент` is still present.

**Rules:**
- Refs only: no `_Key` for enums, primitives, or empty refs. Omit the key rather than send the zero GUID.
- Applies to header attributes, tabular-section lines, catalog `parent`/`Владелец`/attributes, and register columns.

**Compatibility impact:**
- **Row hashes.**
  - Rust `data_hash` (`sync.rs:514`) changes for every row, but only when the row is re-sent: one `u` update each.
  - The Sync's local register hashes (`RegisterRefresh.cs:28`, `ObjectState.cs:91-109`) change too: independent
    registers and charts re-upload once on the next refresh.
- **Backfill.**
  - Stored rows get the keys only when re-sent, and `VerifyRunner` is version-based (`VerifyRunner.cs:9-17`), so it
    will not do it.
  - One forced re-snapshot per base is needed. This is a one-time cost, not per rename.
  - Until then consumers must tolerate a missing `_Key`: the name-first fallback already does.
- **sync-verify** (`SyncVerify.cs:235-244`) compares every expected field: backend rows without `_Key` show as
  "wrong content" until the backfill. That is correct.
- **Parity.** Behind the flag, the `*ParityScenario` / `Legacy*Oracle` (D33) stay byte-identical.
- **Size.** About 40–90 bytes per ref. The batcher already counts bytes (`CanonicalMapper.cs:15-18`). The Rust
  body cap is 64 MiB decoded (`sync.rs:282-284`).
- **Python target.** Mongo just stores the extra keys.
- **Generic raw viewers.** Extra columns will show up there.

### Cheapest correct path: (a) + (b), no per-rename rescan

**Host**
- `Operations.cs` (`DocumentOp :223-247`, `CatalogOp :194-211`, `RegisterOp :301-318`): parse `withRefs`.
- `DocumentReadService.cs`: `DocumentQuery` flag; `ReadRows :257-260`.
- `QueryKit.cs`: `TabularSections.Load :143-144`.
- `CatalogReadService.cs`: `ReadRows :162-169`; select raw `Владелец` next to `ПРЕДСТАВЛЕНИЕ` at `:195`.
- `RegisterReadService.cs`: `ReadRows :245-265`.
- `RefBatch.cs`: keep the GUID and type of a `DeferredRef` and write `_Key`/`_Type` in `PatchOne :81-96`.
- `LegacyValue.cs`: expose a GUID read for ref-typed shapes. Use `AttributeShape.HasRefTypes` / `EnumNames` /
  `RefWithoutDeref` (`MetadataShapes.cs:22-39`) to skip enums.
- `IPC_CONTRACT.md:62`: document the flag.

**Supervisor**
- `SupervisorReader.cs:30-34, 42-45, 57-61, 88, 93, 128-132`: send `withRefs: true`.

**Sync**
- No mapper change (`CanonicalMapper` passes keys through).
- A one-time forced resend/re-snapshot per base.

**Rust module (`_wt-onec-sync-v1`)**
- `store.rs`: add a STORED `cp_ref` column from `raw->>'Контрагент_Key'` plus an index. Leave `cp_key` alone and
  avoid the rewrite there; a new stored column still costs one table rewrite.
- `entity.rs`:
  - `by-type`: add a `counterparty_ref` filter next to `:1204`;
  - `last-by-counterparty`: accept `cp_ref` (`:1529-1540`, `:1773-1812`) and return `<field>_Key` with each value;
  - match index: GUID first, catalog map keyed by `raw->>'id'` as well as `Ref_Key` (`:1957-2020`).
- `bank_index.rs:113-117`: add `Контрагент_Key`.
- `recon.rs`:
  - group by `Субконто*_Key` (`:393-440`, `:694-722`);
  - `cp_key()` GUID-first (`:525`);
  - `load_catalog` already maps GUID → name/INN (`:487-492`).
- `write.rs`: in `resolve_contract`, match `Владелец_Key` to the counterparty `id` (`:486-495`).

**aiba-next consumers**
- `sotuv.rs`:
  - `order_index_for_buyer :3424-3442`: query by counterparty ref;
  - history (`:5855-5880`, `:6852-6865`): use the returned `_Key` directly instead of name → ref.
- `avtoprovodka_1c.rs:2030-2040` and `kansler_bank_1c.rs:2075-2087`: map PO `Контрагент_Key` → INN by GUID.
- `surface/osv.rs:2402-2420`: group payroll by `ФизическоеЛицо_Key`/`Сотрудник_Key`; display name from the catalog.
- `avtoprovodka.rs:2610`, `kansler_bank.rs:2615`: optional, current name by GUID for display.
- `kansler_bank_write.rs:1905-2011`: no change; it starts working.

**Afterwards:** `raw` presentation strings mean "the name as of the document's last write in 1C". No consumer keys on
them, and a catalog rename only updates the one catalog row.
