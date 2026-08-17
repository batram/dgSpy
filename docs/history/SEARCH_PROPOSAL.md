# Proposal: a first-class `search` tool

**Status: implemented historical proposal.** Retained as rationale because
`DGSPY_REFERENCE.md` cites it as the standing record of why this ports dnSpy's matching rules instead of
consuming `IDocumentSearcher`. That reasoning is still current; the plan below is not a pending one.

One design point changed during implementation. Section 2 assumed a module seen through both the debug
session and the Assembly Explorer could be de-duplicated by identity and MVID. Against a live Unity
player neither joins the two views -- the debugger's metadata service and the Assembly Explorer return
different `ModuleDef` instances, and the MVIDs do not match either -- so a `scope: "all"` search walked
all 54 modules twice while every offline suite stayed green. `CollectSearchModules` now keys on instance,
MVID and module name together. `tests\run-search-smoke.ps1` asserts it, and that is the reason that smoke
exists.

## 1. What dnSpy's Search actually is

dnSpy's Search window is four pieces:

| Piece | File | Visibility |
| --- | --- | --- |
| `IDocumentSearcherProvider` / `IDocumentSearcher` | `dnSpy.Contracts.DnSpy/Search/` | **internal** |
| `ISearchComparer` + `SearchComparerFactory` | `dnSpy.Contracts.DnSpy/Search/SearchComparers.cs` | **internal** |
| `VisibleMembersFlags`, `FlagsDocumentTreeNodeFilter` | `dnSpy.Contracts.DnSpy/Search/` | public |
| `FilterSearcher` (the traversal and the match predicates) | `dnSpy/dnSpy/Search/FilterSearcher.cs` | internal to `dnSpy` |

`IDocumentSearcher.Start` takes `IEnumerable<DsDocumentNode>` — **Assembly Explorer tree nodes** — walks
them on a `Dispatcher`, and reports results incrementally through `OnNewSearchResults` as `ISearchResult`
objects carrying WPF-classified text and image references.

### Why we do not drive `IDocumentSearcher` directly

1. **It is not reachable.** `dnSpy.Contracts.DnSpy` has a closed `InternalsVisibleTo` list
   (`dnSpy`, `dnSpy.Debugger.x`, `dnSpy.AsmEditor.x`, `dnSpy.Roslyn`, `dnSpy.Scripting.Roslyn.x`,
   `dnSpy.Debugger.DotNet.x`). `dgSpy.Extension.x` is not on it. Consuming the service means editing
   upstream `AssemblyInfo.cs` — i.e. modifying dnSpy in order to "avoid modifying dnSpy".
2. **Its input is the wrong object.** It searches tree nodes. Every dgSpy decompiler tool searches
   `DbgModule` → `DbgMetadataService.TryGetMetadata` → `ModuleDef`. A session module that is dynamic or
   in-memory has metadata but often has no Assembly Explorer node at all, so routing session scope
   through the tree would silently lose modules `list_documents` reports.
3. **Its output is UI state.** `ISearchResult` exposes `RefreshUI()`, syntax-highlight flags and image
   references, and the searcher is push-based and unbounded-in-time. Everything on the MCP surface is a
   single bounded request/response. The adapter would be most of the work anyway.

### What we take instead

The valuable part of dnSpy's Search is not the plumbing, it is three algorithms, all small and stable:

- **`FilterSearcher.CheckMatch`** — the per-member candidate strings. This is the fix for the reported
  bug. For a member dnSpy tests, in order: escaped name, name, `FixTypeName(declaringType) + "." + name`,
  `FixTypeName(declaringType) + "::" + name`, then the full signature form. For a type it tests the
  namespace-qualified full name **with `/` rewritten to `.` and `` `1 `` stripped**, then the short name.
  That is exactly why `GameState.ChatSystem` resolves in the GUI and returns nothing from `search_symbols`.
- **`SearchComparerFactory`** — `/regex/` detection, space-separated terms combined with AND (or OR),
  whole-word, case sensitivity, and the literal comparers (integer incl. `0x`, double, `"quoted"` string,
  `/regex/` over string literals).
- **`SearchControlVM`'s `SearchType` → `VisibleMembersFlags` table** — the 24-entry "Search For" taxonomy.

These are ported into the extension with attribution, as a **pure, dnlib-free** query object
(`SymbolSearchQuery`) plus a dnlib traversal (`RpcHost.Search.cs`). The pure half compiles into
`tests/dgSpy.Extension.Tests` the same way `MemberPagination` and `ExpressionAddressability` do, which is
the only way this repo can unit-test extension logic at all.

## 2. Scope decision

`search` takes `scope`:

| `scope` | Module set | `session_id` |
| --- | --- | --- |
| `session` (**default**) | `DbgManager` processes → runtimes → modules, via `DbgMetadataService`. Identical to `list_types`, `get_csharp`, `find_references`. | required |
| `documents` | `IDsDocumentService.GetDocuments()` — everything in the Assembly Explorer. This is the set dnSpy's own Search window searches. | not required |
| `all` | Union of both, de-duplicated by MVID. | required |

Rationale: the default has to stay `session`, because a result is only useful if the identifiers in it
work in the next call, and `module` + `token` only round-trip into `get_il` / `set_il_breakpoint` /
`find_references` for a module the debug session has loaded. But the Assembly Explorer scope is the one
capability the GUI has that we did not, and it is the difference between "I can read this assembly" and
"module_not_found". So it is offered explicitly, and **every hit carries `in_session`** so a caller knows
before it tries whether the session tools will accept the module. This is the only read-only tool on the
surface where `session_id` is optional, and the description says so.

## 3. Boundedness and the resume cursor

`search` reports `total`, `truncated`, `scanned`, `scan_truncated` like its neighbours, and adds the
resume cursor the surface is currently missing:

- Traversal order is deterministic: documents in a stable order, `ModuleDef.GetTypes()`, then each type's
  methods, fields, properties, events in declaration order.
- `max_scan` bounds the number of **symbol slots inspected**, not results produced.
- The response returns `next_scan_offset`. Passing it back as `scan_offset` skips exactly the slots
  already inspected and continues. `scan_truncated=false` means the scope was exhausted and there is
  nothing left to resume.

That makes the whole of a 7227-method module reachable in bounded steps, which `analyze_symbol`'s
`max_methods=5000` cap did not allow. The cursor here became the reference implementation: `search_text`
and `analyze_symbol` now share it through `ScanCursor`, and `analyze_symbol`'s bound is `max_scan` in the
same units.

## 4. Tool schema

```
search(
  pattern            string   required   dnSpy syntax: terms AND-ed, /regex/, "quoted" in literal mode
  kinds              string[]            default ["type","member"]
  scope              string              session (default) | documents | all
  session_id         string              required unless scope=documents
  module             string              substring filter on module name or path
  count              1..500              default 100
  max_scan           1..2000000          default 200000 symbol slots
  scan_offset        integer             resume cursor from a previous next_scan_offset
  case_sensitive     bool                default false
  whole_word         bool                default false
  match_any_word     bool                default false  (true = OR the terms)
  compiler_generated bool                default true   (matches dnSpy; needed to find k__BackingField)
)
```

### `kinds` → dnSpy's "Search For" dropdown

Every entry in dnSpy's dropdown has a name here. dnSpy's flags are the authority; ours are the
snake_case spelling.

| `kinds` value | dnSpy "Search For" | `VisibleMembersFlags` |
| --- | --- | --- |
| `assembly` | Assembly | `AssemblyDef` |
| `module` | Module | `ModuleDef` |
| `namespace` | Namespace | `Namespace` |
| `type` | Type | `TypeDef` |
| `field` | Field | `FieldDef` |
| `method` | Method | `MethodDef` |
| `property` | Property | `PropertyDef` |
| `event` | Event | `EventDef` |
| `parameter` | Parameter | `ParamDef` |
| `local` | Local | `Local` |
| `parameter_or_local` | Parameter/Local | `ParamDef \| Local` |
| `assembly_ref` | AssemblyRef | `AssemblyRef` |
| `module_ref` | ModuleRef | `ModuleRef` |
| `resource` | Resource | `Resource \| ResourceElement` |
| `generic_type` | Generic Type | `GenericTypeDef` |
| `non_generic_type` | Non-Generic Type | `NonGenericTypeDef` |
| `enum` | Enum | `EnumTypeDef` |
| `interface` | Interface | `InterfaceTypeDef` |
| `class` | Class | `ClassTypeDef` |
| `struct` | Struct | `StructTypeDef` |
| `delegate` | Delegate | `DelegateTypeDef` |
| `member` | Member | method \| field \| property \| event |
| `any` | All of the Above | `TreeViewAll \| ParamDef \| Local` |
| `literal` | Number/String | `MethodBody \| FieldDef \| ParamDef \| PropertyDef \| Resource` |

`literal` switches the comparer to dnSpy's literal comparers and searches constant values, `ldc.*` /
`ldstr` operands, and resource names. It is not combinable with name kinds; asking for both is
`invalid_arguments` rather than a silently reinterpreted query. Custom-attribute arguments (dnSpy's
`Attributes` flag) are **not** searched — stated in the description rather than implied.

### Result shape, and round-tripping

```
hits[]: {
  kind, module, module_path, in_session,
  token,               -> get_il / get_csharp / find_references / set_il_breakpoint
  name,
  full_name,           -> accepted back as this tool's `pattern`
  declaring_type,      -> accepted by list_members(type:) and get_csharp(type:)
  namespace,
  location,            -> the GUI's Location column: declaring type, or namespace for a type
  match_context        -> why it matched, when that is not the name: "ldstr IL_0042: \"...\"", "local 'buf'"
}
```

This is the `3fcbacd73` rule applied again: `full_name` for a member is
`FixTypeName(declaringType) + "." + name`, which is precisely one of the strings `CheckMatch` tests, so
pasting any returned `full_name` back in as `pattern` re-finds that symbol. `declaring_type` uses the
same `FixTypeName` form, which is what `FindType` accepts. There is no field an agent must parse.

## 5. Changes to the existing specific tools

Nothing is removed. `search_symbols` stays, and its description gains a first line pointing at `search`.
The rest of the family gets a consistent "you should have arrived here from `search`" framing:

| Tool | Description change |
| --- | --- |
| `search_symbols` | Prefix: *"Prefer `search`. This matches the simple name only — it will not find `Type.Member`, and cannot search properties, events, literals or parameters."* |
| `list_types` | Prefix: *"Use when you already know the module and want to enumerate it. To find a type by name, use `search`."* |
| `list_members` | Prefix: *"Drill-in tool: `type` must be a **type**, not a path like `A.B.C` where `B` is a field. Use `search` to identify the type first."* |
| `find_references` / `find_implementations` | Prefix: *"Takes a module + token, which `search` returns."* |
| `analyze_symbol` | Prefix: *"Drill-in tool for a symbol `search` already found."* |
| `get_metadata` | Prefix: *"Module-level facts. To locate a symbol, use `search`."* |
| `search_text` | Prefix: *"Heavier than `search`: decompiles method bodies. Use `search` with `kinds:["literal"]` for string and number constants — it reads IL and is far cheaper."* |

Plus one targeted fix for the measured wrong answer: `FindType`'s `type_not_found` error now detects the
`A.B` case where `A` is a type and `B` is a member of it, and says so
(*"'GameState.ChatSystem' is not a type. 'ChatSystem' is a field on type 'GameState' of type
'ChatDisplay' — pass that type instead."*) rather than the flat "No type ... Use list_types" that led an
agent to conclude the member did not exist.

`get_workflow_help` / `get_started` text puts `search` first in the discovery sequence.
