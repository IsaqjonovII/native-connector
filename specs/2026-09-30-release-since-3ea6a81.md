# Porting spec: Connector `release` since main.os 3ea6a81

Date: 2026-09-30. Status: spec only. Nothing in the rewrite is changed yet.

Note 2026-10-02: P7 and U5 target the milestone 5.9 sync engine (`SyncEngine`, `SyncScheduler`,
`SupervisorSource`, `SyncTests`, its `GET /v1/sync`), which was removed on 2026-10-02 (D48). The
current Sync engine (`SYNC_ENGINE_ARCHITECTURE.md`) skips missing tables (§22) and dropped the
night lane (D-8); D39 is superseded by D42/D48.

## Scope and method

- Old system: `D:\aiba\connector`, branch `release`, HEAD `503addf` (== `origin/release`).
  The rewrite mapped the adapter at `3ea6a81` (D38 table header, DECISIONS.md:666).
- Read: `git log 3ea6a81..release` (37 commits, 6 merges), then `git show <sha> -- <path>` for
  every commit that touches the adapter or a caller of it.
- `main.os` changed in exactly five commits: `a925cca`, `6653c1b`, `ef5c250`, `0f779df`,
  `86b73d6`. The merges add nothing: `main.os` at `d375f15` equals `a925cca`, and at `release`
  equals `86b73d6` (both `git diff` empty).
- Router source and Rust: only `sidecar.rs` changed (bank signer). Router binary rebuilt, source
  unchanged.
- Line numbers: `main.os` = the release worktree (identical to the blob; the worktree's only
  local changes are `Cargo.toml` and `1uz-adapter/adapter.version`). TS = release worktree.
  Rewrite = `D:\aiba\1c-arch` working tree.
- No 1C access, no call to :55899. Every claim below cites a diff or a file:line.

## Summary

| commit | what changed | relevance | item |
|---|---|---|---|
| `6653c1b` | ВидОперации enum type read from the attribute's metadata first, not a list of document names | already equivalent | — |
| `6653c1b` | enum value that cannot be applied = refusal, not a WARN | already equivalent (code name differs) | P5 |
| `6653c1b` | a pinned `id` that resolves to nothing is fatal for every catalog | already equivalent (rewrite is stricter) | — |
| `6653c1b` | after Write, re-read the document and report sent references that did not survive (`attribute_not_persisted`) | **port** + **needs user decision** | P3, U1 |
| `6653c1b` | document list `refs=objects`: references as `{id, type, name}` | **port** | P1 |
| `6653c1b` | document list reference filter `?<Field>={"ref":guid,"type":…}` | **port** | P2 |
| `6653c1b` | `refs` is a control word on every route, never a filter | **port** (edge part) | P1 |
| `6653c1b` | history fallback for ВидОперации no longer sets a string | not relevant (history fallback not ported, D38) | — |
| `ef5c250` | a sent ВидОперации «Товары»/«Услуги» is mapped to the base's equivalent (ПокупкаКомиссия …) | **needs user decision** (D38: a sent value is never changed) | U2 |
| `ef5c250` | row-based ВидОперации reconciliation gets the same equivalents | **port** (fill-empty only) | P4 |
| `a925cca` | `catalog_update` compare-and-set (`_expected`) | **needs user decision** (rewrite has no catalog writes; D18) | U3 |
| `86b73d6` | `_monthlyUpsert` legacy row key (`legacyRowKeyColumn` / `legacyRowKey`) | **needs user decision** (D38 refuses `_monthlyUpsert`) | U4 |
| `0f779df` (adapter) | a group name that matches two live groups is not picked as Родитель | not relevant (rewrite never resolves a group by name) | note in Q6 |
| `0f779df` (connector) | tables the base lacks are cached as absent for 6 h; sync policy (ЧекККМ manual-only, accounting registers night-only); targeted refresh helper | **port** (absent table) + **needs user decision** (policy) | P7, U5 |
| `0f779df` (connector) | Номенклатура projection hash gains ИКПУ/Артикул | not relevant (rewrite hashes whole rows, no probe-scan) | — |
| `0f779df` (connector) | `targeted-refresh.ts` | not relevant (defined, not called anywhere; the rewrite's feed already re-reads a written document, D39) | — |
| `ebd7b02` | new relay commands `document_read` (list + `refs=objects`) and `catalog_read` (by-id GETs) | `document_read` → P1/P2; `catalog_read` already equivalent except one trap | P6 |
| `503addf` | `document_read` takes `organization` → sent as a reference filter `Организация={…}` | **port** (same as P2) | P2 |
| `697f6f4` | write heartbeat; write lane backstop 300 s → 600 s | already equivalent (edge max deadline is 600 s) — caller note | — |
| `57b28cf` | connector stops putting the ИКПУ into Артикул | not relevant (rewrite never matches on Артикул) | note in Q6 |
| `782d09b` | one machine = one connector id on the relay | not relevant (rewrite has no relay client) | — |
| `a2f60b4` | account-chart helper moved to its own file | not relevant (refactor) | — |
| `2b0c835` `a2afb33` `fec4b62` `b9581e2` `ac911a6` `0bd11a4` | bank signer / bank pages | not relevant (D40) | — |
| `d325f2a` `c34891a` `979fda6` `f2bb349` `8986215` `9e3e029` | CI, upload script | not relevant | — |
| `4138817` `bfcef7b` `53cdc32` `d30214e` `b70cf6c` `08a3ced` `d991294` | version bumps and stamps | not relevant | — |

Checked and equivalent, with the evidence:

- **Enum type from metadata** (`6653c1b`, main.os:473-480, 16162-16168, 17595-17596). The
  rewrite has no name list: every field type comes from the attribute's `Тип` via
  `XMLТип(...).ИмяТипа` (WriteSchema.cs:89-121), and a string for an enum field is resolved
  against that enum's names and synonyms (RefResolver.cs:174-200, 375-376).
- **Enum miss refuses** (`6653c1b`, main.os:16187-16212). Rewrite: an enum miss is an ERROR
  diagnostic and the whole document is refused before any write (RefResolver.cs:197, 371;
  DocumentWriter.cs:145). Only the code name differs (P5).
- **Pinned id** (`6653c1b`, main.os:599-617). Old: a stale id falls back to name/code and
  fails only if everything misses. Rewrite: a stale id is a WARN, then the other keys; a miss
  on them is an ERROR; nothing is ever auto-created (RefResolver.cs:204-236).
- **Write deadline** (`697f6f4`, adapter-gateway.ts:396). The connector now waits up to 600 s
  for a write. The rewrite caps a request at 600 000 ms but defaults to 60 000 ms
  (Envelope.cs:55-56). A caller must send `X-AIBA-Deadline-Ms: 600000` on writes. A deadline
  that passes during `Записать` answers 504 while 1C finishes the write (IPC_CONTRACT §5); the
  marker makes the retry idempotent (DocumentWriter.cs:100-101).

---

## P1 — Document list: `refs=objects`

**Source.** `6653c1b` (adapter). Caller: `ebd7b02` `document_read`
(onec-read-commands.ts:57-84; onec-sync-provider.tsx:13343-13452).

**Old behaviour.**
- `refs` is a control word on the document route and in the generic list, never a filter
  (main.os:22690, 22184).
- `refs=objects` (trimmed, case-insensitive) turns the mode on. Any other value = the normal
  rows (main.os:22708-22711).
- The mode turns the XDTO bulk path off (main.os:7706).
- Header attributes and tabular columns of the list go through
  `ПреобразоватьЗначениеПоРежиму` (main.os:8250, 8356).
- Rule (main.os:3220-3251):
  - Неопределено, boolean, number, string, date: the normal value.
  - a value that has a GUID (catalog, document, chart references): empty → `null`; else
    `{ "id": XMLСтрока(GUID), "type": XMLТипЗнч(v).ИмяТипа, "name": <the normal value> }`,
    keys in that order. `type` looks like `CatalogRef.Контрагенты`.
  - no GUID (enums, value storage, 1C NULL, УникальныйИдентификатор values): the normal value.
- By-id and batch routes do not support the mode (onec-read-commands.ts:17).
- Without the parameter the rows are byte-identical to before, so sync hashes do not move.

**Rewrite now: missing.**
- `refs` is not in `DocumentControlParams` (EdgeServer.cs:314-317), so it becomes a filter on
  a field named `refs` (EdgeServer.cs:339-342; DocumentFilters.cs:51-62) and the read fails.
- Catalog routes treat it the same way (EdgeServer.cs:372-376, 395-398).
- Every value is rendered by `LegacyValue.Read` (DocumentReadService.cs:253; QueryKit.cs:144).

**Change.**
1. Edge: add `refs` to `DocumentControlParams` and copy it to `args.refs`. Add `refs` to the
   catalog `ControlParams`; accept and ignore it there, as the old generic list did.
2. `Operations.DocumentOp` (Operations.cs:195-209): absent or `""` → normal; `objects` →
   objects mode; anything else → `Validation` 400. With `id` / `ids`, ignore `refs` (old).
3. `DocumentQuery` gets a refs mode. `ExecuteList` passes it to `ReadRows` and
   `TabularSections.Load` (DocumentReadService.cs:188, 207; QueryKit.cs:114).
4. Rendering in objects mode (a new `LegacyValue` entry beside `Read`, LegacyValue.cs:53):
   - read the raw column (already selected, LegacyValue.cs:32); not a COM value → `Scalar`;
   - XML type: for a single-type reference, from the attribute's metadata (AttributeShape
     knows `EmptyRefOf`, MetadataShapes.cs:198; map `Справочник.` → `CatalogRef.` etc. as
     RefBatch.cs:24-29 does); for a composite type, `XMLТипЗнч(v).ИмяТипа` per value
     (as `RefBatch.Defer` already does, RefBatch.cs:48);
   - `EnumRef.*` or a type with no table kind → the normal value, unchanged;
   - GUID = `XMLСтрока(v)`; the zero GUID → `null`;
   - `name` = the normal value, computed exactly as today (query deref columns, or
     `RefBatch.Defer` for wide types);
   - result `{id, type, name}` in that order.
   - `RefBatch.PatchOne` (RefBatch.cs:81-96) must also replace a `DeferredRef` that sits in
     the `name` of such an object.
   - Do not reuse `LegacyValue.RefObject` (LegacyValue.cs:172-187): it sends
     `type: "COMОбъект"`, the old `ПреобразоватьСсылкуВОбъект` quirk; objects mode sends the
     real XML type.
   - No reference field is read over COM (AGENT.md). `XMLСтрока` / `XMLТипЗнч` on the
     reference itself are allowed (D30: 22–27 µs per GUID).

**Contract.** IPC_CONTRACT §3 `document` row: add `refs?` (`"objects"`, list only) and the
value shape. §3 document paragraph: one sentence on objects mode. §7 documents route: add
`refs`. §7 catalogs route: `refs` accepted, ignored. D33 list of deliberate differences: an
unknown `refs` value is 400 (old: ignored).

**Tests.**
- Unit: extend `DocumentUnitTests.EdgeQueryStringMapsLikeTheOldDocumentRoute`
  (DocumentTests.cs:108): `refs=objects` → `args.refs`, absent from `filters`. Same for the
  catalog mapping test: `refs` is not a filter.
- Live, read-only, KAN and bilim: `ObjectsModeRendersReferencesAsIdTypeName`. Read one page
  (KAN `РеализацияТоваровУслуг`, limit 20; bilim a type with reference columns in its tabular
  sections) twice: normal and objects, same ids. For every header value and every tabular
  value: normal `null` ⇒ objects `null`; objects map ⇒ `id` parses as a GUID, `type` starts
  with `CatalogRef.` / `DocumentRef.` / `ChartOfAccountsRef.` / `ChartOfCharacteristicTypesRef.`,
  `name` equals the normal value; otherwise equal. Spot-check 5 `CatalogRef` ids through the
  `catalog` by-id op: same `name`.
- Live error path: `refs=bogus` → 400.
- COM leak: `ComRef.Live` delta 0, `WrongThreadReleases` delta 0.
- `docparity` must stay 525/525 (normal rows untouched).
- If the old adapter runs, compare a `limit=5` page read-only (AGENT.md parity rule).

**Risk.** Low for existing callers: only the new mode changes. Cost: one or three extra COM
calls per reference value in the mode; fine for the caller's 200-row cap.

---

## P2 — Document list: reference filters

**Source.** `6653c1b` (adapter). Caller: `503addf` — `document_read` sends `organization` as
`Организация={"ref":"<guid>","type":"CatalogRef.Организации"}`, key and value
percent-encoded, also together with a `number` lookup (onec-read-commands.ts:78-82;
onec-sync-provider.tsx:13391).

**Old behaviour.**
- A filter value is a reference filter when it is a map with `ref`, or a string that starts
  with `{` (after trim) and contains `"ref"` (main.os:3256-3264).
- It is checked **before** the special keys, so `ДоговорКонтрагента={"ref":…}` is a real
  equality, not the text post-filter (main.os:7914-7918 for the WHERE, 8066-8071 for the
  parameter).
- Field = the alias map `date→Дата, posted→Проведен, deletionMark→ПометкаУдаления,
  number→Номер`, else the key.
- Value: JSON parsed; `type` must start with `DocumentRef.`, `CatalogRef.` or
  `ChartOfAccountsRef.`; the reference is `<manager>.GetRef(<GUID>)` (main.os:3270-3304).
- Bad JSON, no `ref`, another type → exception → HTTP 500.
- A GUID that does not exist → a valid reference → 0 rows.
- The count covers the window only, not filters (D33) — unaffected.

**Rewrite now: missing, and it fails silently.** `DocumentFilters.Parse`
(DocumentFilters.cs:36-63) turns the key into `Организация = &f0` with the JSON **text** as
the parameter. A reference never equals a string: 0 rows, no error. The EDO page would read
that as "no sales that day". Over the pipe, `Operations.Scalars` (Operations.cs:374-390)
refuses an object value, so a caller cannot pass `{ref, type}` as JSON either.

**Change.**
1. `DocumentFilters.Parse`: before the special-key `switch`, detect a reference filter (the
   old test, plus a `JsonObject` with `ref` from the pipe). Parse `type` and `ref`.
   - `type` not one of the three prefixes → `ArgumentException` (400);
   - `ref` not a GUID → 400; bad JSON → 400.
   - Field through the old alias map; `ReadService.ValidateIdentifier(field)`.
   - The term's value is a small record `(managers, name, guid)`.
2. `DocumentReadService.ExecuteList` (DocumentReadService.cs:163): resolve that record with
   `QueryKit.RefByGuid` before `SetParameter`. A type name the configuration lacks → 400 with
   a clear message.
3. `Operations.Scalars`: allow an object filter value only when it has `ref`.

**Contract.** IPC_CONTRACT §3 document paragraph: "A filter value
`{"ref": guid, "type": "CatalogRef.X" | "DocumentRef.X" | "ChartOfAccountsRef.X"}` (an object
over the pipe, JSON text in the query string) is a reference equality on that field; it wins
over the special keys." §7 documents route: same note. Deliberate difference (D33 list): a bad
reference filter is 400 (old 500).

**Tests.**
- Unit (`DocumentUnitTests`): string form and object form → one term with the reference
  record; `ДоговорКонтрагента={"ref":…}` → a term, `ContractNumber` stays null; bad JSON,
  missing `ref`, `EnumRef.X`, a non-GUID → `ArgumentException`; a plain string filter
  unchanged.
- Unit (edge): the exact query string `buildDocumentReadPath` builds (percent-encoded
  Cyrillic key and JSON value, onec-read-commands.ts:78-82) maps to
  `filters["Организация"]` = the JSON text.
- Live, read-only, KAN: take an `orgRef` from an unfiltered one-day page; the filtered page
  has only rows with that `orgRef`, and as many as the unfiltered page has with it. A random
  GUID → 0 rows, 200. Same through the edge with the encoded query. If the local bases hold
  one organisation only, the equality check still holds; say so in MIGRATION_STATUS.
- COM leak check as in P1.

**Risk.** A wrong term returns 0 rows silently. The count cross-check test is the guard.

---

## P3 — After a create, check that the sent references survived the write

**Source.** `6653c1b`.

**Old behaviour.**
- While building the document, every header enum (ВидОперации) and header catalog reference
  taken from the payload is remembered (main.os:15808, 16215, 16323).
- After `Write`, the document is re-read from the base (`Ссылка.ПолучитьОбъект()`) and each
  remembered attribute is compared by GUID, enums by text (main.os:390-455; called at 16927).
- A mismatch → ERROR diagnostic `attribute_not_persisted`, message "sent X, the base has Y
  after the write". Not an exception: the document stays written; HTTP 201, `success: true`
  (main.os:415-417, 20974-20984). The connector shows ERROR diagnostics as warnings on a
  successful write (onec-sync-provider.tsx:12359-12376).
- The case that found it: `ПлатежныйОрдерПоступлениеДенежныхСредств` on Kanstik — the
  configuration refills header `СтатьяДвиженияДенежныхСредств` from `РасшифровкаПлатежа` at
  write time; with no rows it ends up empty (comment at main.os:15790-15807).
- Create only, header only, only values the caller sent.

**Rewrite now: missing.** `DocumentWriter.CreateIn` sets values, reads numbers back right
after `Set` (DocumentWriter.cs:381-384), writes once (164) and returns. Nothing is read back
after `Записать`. The same silent loss can happen.

**Change.**
1. In `CreateIn`, keep the header fields that came from `body.Header` whose converted value
   is a COM reference or enum. Fill-empty values are not checked (old).
2. After `WriteObject`, one query on the same session:
   `ВЫБРАТЬ ВЫБОР КОГДА Т.<f> = &e<i> ТОГДА ИСТИНА ИНАЧЕ ЛОЖЬ КОНЕЦ КАК ok<i>,
   ПРЕДСТАВЛЕНИЕ(Т.<f>) КАК t<i> … ИЗ Документ.<name> КАК Т ГДЕ Т.Ссылка = &r`.
   The comparison happens in the query; no reference field is read over COM (AGENT.md).
   Use `QueryKit.Field` for names (keyword columns, MIGRATION_STATUS 5.4).
3. Each false → `attribute_not_persisted`, level ERROR, field `<f>`, message
   "sent <sent value's text>, 1C kept '<t>' after the write".
4. What happens then depends on **U1**.
5. Beyond the old code, recommended: the same check in `Update` after its write
   (DocumentWriter.cs:237-241). Same code, cheap.

**Contract.** IPC_CONTRACT §3 `create`: new diagnostic code `attribute_not_persisted`. The
status (201 with the ERROR, or 422 with nothing written) per U1.

**Tests.**
- Live, KAN, `AIBA_REWRITE_` marker, cleanup counted: a ПТУ create with Контрагент, Склад
  and ВидОперации sent → no `attribute_not_persisted`.
- Live mismatch path: needs a type whose configuration rewrites a sent header reference on
  write. Candidate: `ПлатежныйОрдерПоступлениеДенежныхСредств` with
  `СтатьяДвиженияДенежныхСредств` and no `РасшифровкаПлатежа` rows. Seen on Kanstik; **not
  known** whether KAN or bilim has this type — check the schema read-only first. If no local
  type reproduces it, record the mismatch branch as NOT live-verified.
- If U1 = roll back: the event-log feed shows no committed change for the refused document
  (pattern of `ARolledBackWriteIsNotAChange`, D36), and `cleanup --dry-run` finds nothing.
- COM leak check.

**Risk.** A configuration that legitimately normalises a sent reference would now be
reported. With U1 = roll back, such writes would fail where they used to pass with a warning.

---

## P4 — ВидОперации fill-empty: the configuration's equivalents

**Source.** `ef5c250`, second half.

**Old behaviour.** `ПримиритьВидОперацииПоТабличнымЧастям` (main.os:12601-12653), first value
that exists in the enum wins:
- goods only: `Товары, ПокупкаКомиссия, ПродажаКомиссия, ТоварыУслугиКомиссия, ТоварыУслуги`;
- services only: `Услуги`, then the same four;
- both: `ПокупкаКомиссия, ПродажаКомиссия, ТоварыУслугиКомиссия, ТоварыУслуги`.

Why: on KAN and Kanstik `ВидыОперацийПоступлениеТоваровУслуг` has no Товары/Услуги; 43 308 of
43 319 KAN ПТУ are ПокупкаКомиссия (commit message; also onec-sync-provider.tsx:10775-10781).
The old procedure also overwrote a sent value (quirk Q4) — that part is not ported (D38).

**Rewrite now: different.** `FillEmpty` (DocumentWriter.cs:406-417) tries only
`Товары` / `Услуги` / `ТоварыУслуги` through `TryEnum` (RefResolver.cs:102-108). On KAN none
exists, so ВидОперации stays empty and nothing says so.

**Change.**
- Move the candidate list into a pure static function with the old three lists, old order.
  First value `TryEnum` finds wins. This changes the rewrite's mixed-case first choice from
  `ТоварыУслуги` to `ПокупкаКомиссия` (old order) — only visible on a base that has both.
- INFO `operation_autofilled` names the value used (as today).
- None found → WARN `operation_not_filled` (new; today silent).
- Still only when the payload left ВидОперации out (D38 unchanged).

**Contract.** None, beyond the new WARN code.

**Tests.**
- Unit: the candidate function for goods / services / mixed.
- Live, KAN, marker, cleanup: ПТУ without ВидОперации — goods only → ПокупкаКомиссия;
  services only → ПокупкаКомиссия; read back by id.
- Live, bilim: if its enum has `Товары`, goods only → `Товары` (literal first).

**Risk.** Low. Only bodies without ВидОперации.

---

## P5 — Diagnostic code for an enum miss

**Source.** `6653c1b` (main.os:16196 `enum_type_unresolved`, 16208 `enum_value_not_found`).

**Rewrite now: different name.** An enum miss is `unresolved_reference`
(RefResolver.cs:197, 371). WriteBody.cs:7 says the rewrite keeps the old codes where the
meaning is the same.

**Change.** When every type of the field is an `EnumRef` and nothing matches, use
`enum_value_not_found` (ERROR). Do not add `enum_type_unresolved`: types always come from
metadata here (WriteSchema.cs:89-121), so that case cannot happen.

**Tests.** Live: body with `ВидОперации: "NoSuchValue"` → 422,
`fillDiagnostics[].code = enum_value_not_found`, nothing written (mirrors case 3 of
`scripts/adapter-tests/operation-kind-enum.ps1`).

**Risk.** A consumer keyed on `unresolved_reference` for enums. None in the connector (grep of
`apps/app/src`); the cloud side was not checked.

---

## P6 — Catalog by id: "no such element" vs "no such catalog"

**Source.** `ebd7b02` `catalog_read`.

**Old behaviour.** Unknown catalog → `error: "Справочник не найден: X"` (main.os:6739-6741).
Missing element → `error: "Элемент не найден"` (main.os:6848). `catalog_read` counts only the
second one (or an HTTP 404) as "missing"; any other failure fails the command so the cloud
retries and does not treat the card as deleted (onec-read-commands.ts:110-120).

**Rewrite now: the two look the same.** Both are 404, kind `notFound`
(CatalogReadService.cs:79; MetadataShapes.cs:92; Operations.cs:398-399). A misspelt catalog
would read as "every id missing". The sync has the same blind spot:
`SupervisorSource.CatalogByIdAsync` returns null (row gone) on any `notFound`
(SupervisorSource.cs:34).

**Change.** Keep 404 (contract). `Operations.CatalogOp` (Operations.cs:163-164) adds
`error.data.missing = "element"` or `"catalog"`. `SupervisorSource.CatalogByIdAsync` returns
null only for `"element"`. Do the same for `document` by id (`"element"` / `"document"`).

**Contract.** IPC_CONTRACT §4: additive `error.data.missing` on by-id 404s.

**Tests.** Live, read-only: random GUID on `Контрагенты` → 404, `missing = element`;
`NoSuchCatalog` → 404, `missing = catalog`. Unit/engine: a fake source that answers
"catalog" missing must not prune.

**Risk.** Low, additive.

---

## P7 — Sync: a table the base does not have must not stop the pass (5.9 engine, removed 2026-10-02, D48)

**Source.** `0f779df` (connector sync). The connector caches tables `/counts` reports absent
for 6 h and skips them (source-capability.ts:13, 73-84; sync-orchestrator.ts:772, 915-921).
Since the P0 fix, a table that fails is skipped, not fatal (connector `CLAUDE.md`).

**Rewrite now: missing.** `SyncEngine.RunOnceAsync` cold-reads the tables in order
(SyncEngine.cs:65-66). `SupervisorSource.Call` throws on any error (SupervisorSource.cs:71-76).
One missing document type throws out of the pass; the scheduler backs off and retries the whole
base (SyncScheduler.cs:80-88), so every table after it never syncs.

**Change.**
- `SupervisorSource` throws a typed "table absent" error when the host says the metadata
  object does not exist (P6's marker, extended to documents and registers:
  `missing = "catalog" | "document" | "register"`).
- `SyncEngine`: per table — absent → warning, remember `AbsentSince` in `BaseState`, skip for
  6 h, then probe again; any other table error → warning, go on with the next table; the
  pass reports partial success. A transport error is not evidence of absence.
- `GET /v1/sync` lists absent tables.

**Contract.** None beyond P6's marker.

**Tests.** Unit (`SyncTests`, fake source): one table absent → the others upload; the warning
is there; skipped within 6 h; probed again after (injected clock). Live: a sync config with a
non-existent document name on KAN → the pass completes the other tables.

**Risk.** Low. The real sync target is parked (D39), so this is low priority.

---

## Decisions for the user

### U1 — What happens when a sent reference did not survive the write (P3)

- **A. Old behaviour.** Keep the document. 201, ERROR `attribute_not_persisted` in
  `fillDiagnostics`.
- **B. D38 style.** Wrap the create in `НачатьТранзакцию` / `ОтменитьТранзакцию` on the
  connection; roll back on a mismatch; 422 `unprocessable`, nothing written.

Recommendation: **B.** It matches D38 ("a failure writes nothing") and 5.8. Cost: posting
inside an explicit transaction holds locks a little longer (on a file base the lock is
base-wide anyway), and it needs its own rollback test. Risk: any payload that today hits this
case starts failing instead of passing with a warning.

### U2 — A sent ВидОперации «Товары» / «Услуги» (`ef5c250`)

The connector always sends ВидОперации on ПТУ/Реализация and falls back to `"Товары"` /
`"Услуги"` when the cloud gives none (onec-sync-provider.tsx:10785-10793). On KAN and Kanstik
those values do not exist. Rewrite today: `unresolved_reference`, 422, every such write
refused. Old since `ef5c250`: the word is an intent; if the literal is missing, the first
existing of `ПокупкаКомиссия, ПродажаКомиссия, ТоварыУслугиКомиссия, ТоварыУслуги` is used
and the swap is only logged (main.os:244-281, 16202). D38 says a sent value is never changed.

- **A.** Accept the alias in the rewrite: only these three words, only when the literal is
  not a value of the field's enum; WARN `operation_kind_substituted` (visible, unlike the
  old log line).
- **B.** Keep D38 strict. Change the caller to leave ВидОперации out when it has none; P4's
  fill-empty then picks the same value from the rows.
- **C.** Both, A only during cut-over.

Recommendation: **B** as the end state; **A** only if the rewrite must serve today's
connector unchanged.

### U3 — Catalog writes, incl. `catalog_update` compare-and-set (`a925cca`)

The rewrite has no catalog write at all: no PUT/POST on `catalogs` (EdgeServer.cs:154-157),
and D38 covers documents only. `catalog_update` edits cards AIBA did not create (ИКПУ and
package fields), which conflicts with D18 (DECISIONS.md:180-191) if applied to catalogs.

If it is ported, the exact old CAS (main.os:7243-7292):
- body key `_expected` = `{attribute: value}`; never set as an attribute;
- per key: current value → `СокрЛП(Строка(v))`, `null` / Неопределено → `""`; expected the
  same way;
- a key that cannot be read → `error: "precondition_unreadable: <key>"`, nothing written;
- any mismatch → `error: "precondition_failed"`, `current: {key: value}`, nothing written;
- else `expectedChecked: true` in the result, then the normal update;
- old quirk: `Строка()` of a reference is "COMОбъект" in oscript, so a reference in
  `_expected` never matches. The rewrite should compare references by GUID or refuse them
  (400).
- The connector's own pre-read and `already_applied` answer are caller-side
  (catalog-update-cas.ts:55-72); the `catalog_update_cas` capability is advertised by the
  connector (onec-sync-provider.tsx:1713).

Options: **A** port catalog writes (with CAS) as a new milestone and a written exception to
D18 for named attributes; **B** leave catalog writes on the old adapter. Recommendation:
**B** for now; A is a new milestone, not a port item.

### U4 — `_monthlyUpsert` and its legacy row key (`86b73d6`)

The rewrite refuses `_monthlyUpsert` with 400 (WriteBody.cs:96-97; DECISIONS.md:677). The
corporate-cheque flow (one Авансовый отчёт per month) sends it. `86b73d6` adds
`legacyRowKeyColumn` / `legacyRowKey`: both or neither (else `monthly_directive_invalid`),
and rows matching the old key are removed together with the new-key rows before the new rows
are added, so a resend after a key-column change does not duplicate a cheque
(main.os:15543, 15577-15578, 15596, 15739-15748).

Options: **A** keep refusing — the flow stays on the old adapter; **B** port `_monthlyUpsert`
as its own milestone, legacy key included. Recommendation: **A** until the flow is scheduled;
B needs its own spec (main.os:15539-15760, 16850-16957, 17376-17405).

### U5 — Sync policy in the 5.9 `OneC.Sync` (`0f779df`; engine removed 2026-10-02, D48)

Connector rules (sync-policy.ts:30-66): `Document_ЧекККМ` is never synced automatically;
`AccountingRegister_*` only in the night lane; retail report tables belong to the reports
lane. The rewrite's scheduler syncs whatever its config lists, at any time (SyncScheduler.cs).
The feed-driven design (D39) makes steady-state register work cheap; "night only" matters
mostly for cold reads.

Options: **A** port the manual-only list and a night window for cold reads of accounting
registers; **B** leave it to the sync config. Recommendation: **A for the manual-only rule**
(a product rule); defer the night window until the real sync target is chosen.

---

## Implementation plan (in order)

1. **P2** reference filter — small, fixes a silent-empty read, unblocks the org-scoped read.
2. **P1** `refs=objects` — needs P2's edge work in the same routes.
3. **P6** not-found marker (+ `SupervisorSource`).
4. **P5** enum code.
5. **P4** fill-empty equivalents.
6. **P3** post-write check — after the user answers U1.
7. **P7** sync absent tables (low priority while D39 is parked).
8. U2–U5 per the user's answers; U3 and U4 become their own specs if chosen.

After each step: unit + live suite (`ONEC_TEST_BASES`), `catparity` / `docparity` still
identical, leftover-process and `cleanup --dry-run` checks, then update `IPC_CONTRACT.md`,
`MIGRATION_STATUS.md`, and one new decision entry listing the deliberate differences
(`refs` bad value 400; bad reference filter 400; `error.data.missing`; U1–U5 outcomes).

## Open questions

1. **How do the connector's callers reach the rewrite?** The rewrite serves `/v1/bases/...`
   with `X-AIBA-Token` and answers `{rows, totalCount, …}`. The connector calls
   `/api/db/...` and reads `{success, data, total_count, …}` (onec-read-commands.ts:57-84,
   145-170). Options: an `/api/db` facade in the edge, a connector change, or the rewrite
   taking over the relay commands (`document_read`, `catalog_read`, `catalog_update`,
   writes). This spec only covers the op/edge capability all three need.
2. **U1**: roll back, or keep the old "written + ERROR"?
3. **U2**: accept the «Товары»/«Услуги» alias, or fix the caller?
4. **U3 / U4**: are catalog writes and `_monthlyUpsert` in the rewrite's scope at all?
5. **U5**: port the ЧекККМ manual-only rule now?
6. Noticed, not from this range: on an explicit create the rewrite drops `Родитель` and
   `ПодобратьРодителяПоТексту` without a diagnostic (it sets only table attributes,
   RefResolver.cs:333-349), and the connector sends both for new Номенклатура
   (nomenclature-ref.ts:238, 270). Also the rewrite finds Номенклатура by exact name only;
   the old tries codes against КодИзКлассификатора and Артикул (main.os:10378). In scope?
