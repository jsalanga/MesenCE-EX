# MesenCE MCP server: investigation and plan

Status: **approved; implementation in progress** on branch `feature/mcp-server`.

> **Change during implementation (transport):** the server uses a small HTTP/1.1 handler on
> `TcpListener` bound to `127.0.0.1` and `::1` instead of `HttpListener`. On Windows `HttpListener`
> goes through http.sys, which listens on every interface (unless an administrator configures the
> IP listen list) and filters only by the `Host` header, so it cannot guarantee a loopback-only socket.
> The custom handler guarantees it on every OS, behaves identically on Windows/Linux/macOS, and lets
> the server detect a client disconnect (which the spec requires to be treated as cancellation).
> JSON is built with `System.Text.Json.Nodes` (`JsonNode`), which is AOT-safe and needs no
> source-generated context because no POCOs are (de)serialized.

## TL;DR

- **Where it lives:** a C# service in the UI project under `UI/Mcp/`. It uses `System.Net.HttpListener` and System.Text.Json source generation. It needs no new NuGet packages and no new native dependencies. The code agrees with your expectation.
- **Protocol:** I'll hand-write a small JSON-RPC layer that supports both MCP eras.
  - **Modern `2026-07-28`** is stateless: `server/discover`, per-request `_meta`, and the `Mcp-Method`/`Mcp-Name` headers.
  - **Legacy `2025-11-25`** (also `2025-06-18` and `2025-03-26`) uses the `initialize` handshake and `Mcp-Session-Id`.
  - The spec moved to a stateless core on 2026-07-28, so real clients will be on either side for a while.
  - I recommend against the official C# SDK: its HTTP transport requires ASP.NET Core/Kestrel.
- **Threading:** requests run on thread-pool threads.
  - Core reads go straight through `DebugApi`, which is already safe from any thread.
  - Label and breakpoint changes are marshalled to the Avalonia UI thread. That is the only thread those managers are used from, and it makes open debugger windows refresh.
  - The emulation-thread notification callback only signals waiters. It never blocks.
- **Two lifecycle gotchas decide much of the design:**
  1. When the last debug window closes, `ReleaseDebugger()` wipes breakpoints, labels, trace settings and input overrides.
  2. `BreakpointManager` only pushes breakpoints for CPUs that have an open debugger window.

  The MCP server has to register as a debugger client for both. That needs two small, backward-compatible changes to existing files (see "Changes to existing files").
- **Needs C++ for full fidelity:** ROM CRC32 and the SNES cart header/map mode are not exported to C#. Phase 2 works around both in C#. A tiny optional export is proposed but not required.

---

## 1. Debugger functionality: C# wrappers mapped to the requested operations

Every `DebugApi` export goes through `WrapDebuggerCall` (`InteropDLL/DebugApiWrapper.cpp:30-52`). It calls `Emulator::GetDebugger(true)`, which **creates the debugger on demand**. With no ROM loaded, or while a load is in progress (`BlockDebuggerRequests`), calls return zeroed or default data instead of failing. `EmuApi`, `InputApi` and `RecordApi` do not need the debugger.

| Operation | C# API (UI/Interop) | Notes / gaps |
|---|---|---|
| ROM name, console, path | `EmuApi.GetRomInfo()` (`EmuApi.cs:67`) → `RomInfo` (`RomPath`, `Format`, `ConsoleType`, `CpuTypes`, `GetRomName()`) | `CpuTypes` tells us which CPUs exist (SNES + SPC + SA-1/GSU/…). |
| SHA1 | `EmuApi.GetRomHash(HashType.Sha1)` (`:98`) | |
| CRC32 | **C++ only** (`Emulator::GetCrc32`, `Core/Shared/Emulator.cpp:638`) | Workaround: compute CRC32 in C# over `GetMemoryState(<console>PrgRom)`. That is the PRG ROM CRC, not the file CRC; label it as such. Optional: 5-line `EmuApi.GetRomCrc32` export. |
| SNES header / map mode | **C++ only** (`SnesCartInformation`, `Core/SNES/CartTypes.h:5`). `DebugApi.GetRomHeader` is NES-only. | Workaround: read the 32-byte header at `SnesMemory` $00FFC0 and decode title, map mode, ROM type, ROM/SRAM size, region and checksum in C#. That is exactly what the core parses. |
| Running / paused | `EmuApi.IsRunning()`, `EmuApi.IsPaused()` (`:47,53`) | With a debugger attached, `IsPaused` reports the debugger break state. `DebugApi.IsExecutionStopped`/`IsDebuggerRunning` are exported natively (`DebugApiWrapper.cpp:67,72`) but **not declared in C#**; add the `DllImport`s in `UI/Mcp` (C# side only). |
| Frame count | `EmuApi.GetTimingInfo(cpu).FrameCount` (`:80`) | Also gives scanline, cycle and master clock. |
| Pause / resume | `EmuApi.Pause/Resume` (`:51-52`); `DebugApi.ResumeExecution` (`DebugApi.cs:24`) | With a debugger, Pause = `Step(mainCpu,1,Step,BreakSource.Pause)`. All of these are ignored while a netplay client is connected. |
| Step | `DebugApi.Step(cpu, count, StepType)` (`:25`) | `StepType`: Step, StepOver, StepOut, CpuCycleStep, PpuStep, PpuScanline, PpuFrame, SpecificScanline, RunToNmi, RunToIrq, StepBack. Support per CPU comes from `GetDebuggerFeatures(cpu)` (`:199`). StepOut/StepOver ignore `count`, so we loop. |
| Reset / power cycle / reload | `EmuApi.ExecuteShortcut(ExecReset / ExecPowerCycle / ExecReloadRom)` (as `LoadRomHelper.Reset` does) | There is no direct export. |
| Save / load state | `EmuApi.SaveStateFile/LoadStateFile(path)`, `SaveState/LoadState(slot)` (`:107-110`) | |
| Load ROM | `EmuApi.LoadRom(path, patch)` (`:61`) | Run it off the UI thread, as `LoadRomHelper` does. |
| Input injection | `DebugApi.SetInputOverrides(port, DebugControllerState)` (`:228`), `GetAvailableInputOverrides()` (`:231`) | This is the same mechanism as the debugger's "Controller input" panel. It applies on every input poll and **persists until cleared**. An all-false state means "pass real input through". The SNES implementation overwrites all 12 buttons. |
| CPU state | `DebugApi.GetCpuState<T>(cpu)` (`:100`), `SetCpuState` (`:163`), `GetProgramCounter` (`:182`) | SNES/SA-1: `SnesCpuState` (A, X, Y, SP, D, PC, **K**=PB, **DBR**, `PS` flags incl. `MemoryMode8`/`IndexMode8`, `EmulationMode`). Also `SpcState`, `GsuState`, `Cx4State`, `NecDspState`, `ArmV3CpuState` (ST018), plus the other consoles' structs. |
| Read / write memory | `GetMemoryValues(type, start, endInclusive)` (`:258`), `GetMemorySize` (`:202`), `SetMemoryValues` (`:205`) | `MemoryType` enum (`:587`) covers CPU views (`SnesMemory`, `SpcMemory`, `Sa1Memory`, …) and physical regions (`SnesPrgRom`, `SnesWorkRam`, `SnesSaveRam`, `SnesVideoRam`, `SnesCgRam`, `SnesSpriteRam`, `SpcRam`, …). |
| Call stack | `DebugApi.GetCallstack(cpu)` (`:430`) → `StackFrameInfo` (source/target/return, NMI/IRQ flags) | |
| Trace tail | `SetTraceOptions(cpu, opts)` (`:30`), `GetExecutionTrace(offset, max)` (`:35`), `GetExecutionTraceSize` | Tracing must be **enabled per CPU** first (30,000-row ring). `UseLabels` gives label substitution. The Trace Logger window writes the same options, so the two can overwrite each other (see Risks). |
| Disassembly | `GetDisassemblyOutput(cpu, address, rows)` (`:58`) → `CodeLineData` (Text with labels, ByteCode, Address, AbsoluteAddress, EffectiveAddress, Value, Comment, Flags); `GetDisassemblyRowAddress` (`:76`) | This is the output the debugger window renders. `EffectiveAddress` is computed from *current* CPU state, as the UI shows it. |
| CDL | `GetCdlData(offset, len, type)` (`:503`), `GetCdlStatistics(type)` (`:520`), `GetCdlFunctions(type)` (`:523`) | SNES flags: Code 0x01, Data 0x02, JumpTarget 0x04, SubEntryPoint 0x08, **IndexMode8 0x10 (X)**, **MemoryMode8 0x20 (M)**, Gsu 0x40, Cx4 0x80. The C# `CdlFlags` enum lacks 0x40/0x80; we decode raw bytes ourselves. |
| Find references | **No xref API.** `FindOccurrences(cpu, text, opts)` (`:80`, 500 results max) searches the label-resolved disassembly text; `GetMemoryAccessCounts` (`:490`) gives read/write/exec counters | See the tool design below. It is static text search plus optional runtime counters. |
| Search memory | **Not in the core.** `MemorySearchViewModel` does it in C# over `GetMemoryState` | Reimplement in C#: byte pattern with wildcards. |
| Expressions | `DebugApi.EvaluateExpression(expr, cpu, out resultType, useCache)` (`:197`) | Also validates breakpoint conditions (`EvalResultType.Invalid`). |
| Labels (core) | `DebugApi.SetLabel/ClearLabels` (`:223-224`) | **Do not call these directly.** Go through `LabelManager` (below). |
| Breakpoints (core) | `DebugApi.SetBreakpoints(InteropBreakpoint[])` (`:226`) | **Replaces the whole set.** Go through `BreakpointManager`. |

Only in C++, with no export: CRC32, the SNES cart header struct, a cross-reference index, and memory search. All of them have C# workarounds.

## 2. Labels, comments, breakpoints, CDL and `.mlb`

**Labels** (`UI/Debugger/Labels/LabelManager.cs`, static)
- `CodeLabel` { `Address`, `MemoryType`, `Label`, `Comment`, `Length`, `Flags` }. It is keyed per byte by `address | memType<<32`. A comment is a label with an empty name.
- `SetLabel(label, raiseEvent)`, `DeleteLabel`, `SetLabels` and `GetLabels(cpu)` push to the core through `DebugApi.SetLabel` and raise `OnLabelUpdated`. The debugger window handler refreshes the disassembly, label list, call stack and function list. It also re-derives `assert()` breakpoints from comments.
- **Validation is only in the edit dialog** (`LabelEditViewModel.cs:55-110`): name regex `^[@_a-zA-Z]+[@_a-zA-Z0-9]*$`, unique name, range inside memory, length 1..65536, no `\x1` in comments. `LabelManager.SetLabel` silently *removes* any other label that has the same name or overlaps. The MCP layer must repeat these checks and return errors instead of clobbering.
- **No locking.** All existing mutation happens on the UI thread.

**`.mlb` files** (`MesenLabelFile.cs`)
- `Import(path, showResult)` goes through `CodeLabel.FromString`. It respects the `Integration` import filters and calls `LabelManager.SetLabels`. `Export(path)` sorts by memory type and address.
- Row format: `MemType:ADDR[-END]:label[:comment]`.
- The debugger menu routes imports through `DebugWorkspaceManager.LoadSupportedFile(path, true)`. That also handles `.dbg`, `.sym`, `.cdb`, `.elf` and `.fns`, and optionally resets labels first (`ResetLabelsOnImport`).

**Workspace persistence** (`DebugWorkspaceManager.cs`)
- `<Home>/Debugger/<RomName>.json` holds labels, breakpoints and watches per CPU.
- It is loaded when the first debug window opens and on `GameLoaded`. It is saved when the last debug window closes, and by `AutoSave()`, which label and breakpoint edits call (throttled to once per 60 s).
- Companion `.mlb`/`.sym`/`.cdl` files next to the ROM are auto-loaded if enabled.

**Breakpoints** (`UI/Debugger/Breakpoints`)
- `Breakpoint` { `CpuType`, `MemoryType`, `StartAddress`, `EndAddress`, `BreakOnRead`/`Write`/`Exec`, `Forbid`, `Enabled`, `MarkEvent`, `IgnoreDummyOperations`, `Condition` }.
- `BreakpointManager.AddBreakpoint/RemoveBreakpoint/RefreshBreakpoints` raise `BreakpointsChanged` (disassembly margin and breakpoint list refresh) and push the full list.
- The core breakpoint id is an index into [user, asserts, temporary]. `GetBreakpointById` maps a `CodeBreak` back to the object.
- Condition syntax is checked with `EvaluateExpression(... ) != Invalid`. The core silently ignores unparsable conditions, so we must validate first.

**CDL**
- The core auto-loads `<Debugger>/<rom>.cdl` when the debugger is created and saves it when the debugger is destroyed (e.g. `SnesDebugger.cpp:74,93`).
- UI actions: `ResetCdl`, `LoadCdlFile`, `SaveCdlFile`, and ROM strip via `SaveRomToDisk`.
- CDL persistence already works through the debugger lifecycle, as long as the debugger isn't released and recreated needlessly.

## 3. Threading

| Thread | Role |
|---|---|
| **Emulation thread** (`Emulator::Run`, recreated per ROM load) | Runs frames. When the debugger breaks, **this thread blocks** inside `Debugger::SleepUntilResume`, spin-sleeping until resumed. |
| **Avalonia UI thread** | Windows, view models, `LabelManager`/`BreakpointManager` mutations. |
| **Other** | Core init runs in `Task.Run` from `MainWindow.OnOpened`. `LoadRom` is called via `Task.Run`. Native shortcut, video and netplay threads also send notifications. |

**Notifications**
- `NotificationManager` invokes every listener **synchronously on the sending thread**, usually the emulation thread.
- `NotificationListener` can be instantiated more than once; each instance registers its own native callback. The MCP server gets its own.

**Pause, break and step**
- Emulator "pause" is a frame-boundary flag. With a debugger attached, `EmuApi.Pause()` becomes a one-instruction debugger step, and `IsPaused()` becomes the debugger's break flag.
- A break sets `_executionStopped` and sends **`CodeBreak`** with `BreakEvent { Source (Breakpoint/Pause/CpuStep/PpuStep/Irq/Nmi/BreakOnBrk/…), SourceCpu, Operation, BreakpointId }`. It then blocks, and `DebuggerResumed` is sent when it continues.
- **Steps and "run one frame" also finish with `CodeBreak`** (Source `CpuStep`/`PpuStep`). One "wait for CodeBreak" mechanism therefore serves `step`, `run_until` and frame stepping.

**How existing code reads state safely**
- `DebugApi` calls are safe from any thread against teardown (`DebuggerRequest` refcount plus `BlockDebuggerRequests` during load).
- Mutating calls (`SetBreakpoints`, `Step`, `SetMemoryValues`, `SetCpuState`, CDL, labels, trace fetch, callstack) take a `DebugBreakHelper`. It briefly halts the emulation thread at an instruction boundary and resumes it.
- Plain reads (`GetCpuState`, `GetMemoryValues`) copy live state. They are consistent when execution is stopped and may tear while running. Tools that need a coherent snapshot while running (e.g. `get_cpu_state`) state that in the result (`"running": true`).
- Existing tool windows already call `DebugApi` from the emulation-thread callback (`ToolRefreshHelper`) and from `Task.Run`, then post to the UI.

**What happens when a breakpoint hits**
1. `CodeBreak` goes, on the emulation thread, to `MainWindow` → `DebugWindowManager.ProcessNotification` → each open debug window.
2. `DebuggerWindow` marshals the event and `Dispatcher.UIThread.Post`s the refresh.
3. The emulation thread stays parked until `ResumeExecution`/`Step`/`Run`.

**Deadlock rule** (from `ToolRefreshHelper`): never block the emulation-thread callback waiting on another thread that makes a `DebugBreakHelper` call. The MCP listener only sets a `TaskCompletionSource` or channel and returns.

**Lifecycle hazards the server must handle**
- **`ReleaseDebugger()` on last window close** (`DebugWindowManager.cs:75-95`) destroys the core debugger: breakpoints, labels, trace options, input overrides and scripts. It also saves the workspace and CDL. A later `DebugApi` call recreates an empty debugger.
- **`BreakpointManager._activeCpuTypes`** is a plain `HashSet` filled only by `DebuggerWindowViewModel`. With no debugger window open, `SetBreakpoints()` sends **nothing**. If the MCP server added a CPU type and the user then opened and closed a debugger window, `RemoveCpuType` would drop it.
- **ROM load:** `BeforeGameLoad` → debugger destroyed → `GameLoaded` → debugger recreated (if one was active) → workspace reloaded (labels and breakpoints re-sent). Requests that arrive during a load see default data. Tools check `EmuApi.IsRunning()` and a "loading" flag we track from `BeforeGameLoad`/`GameLoaded`/`GameLoadFailed`, and return a clear error instead of zeros.
- **Shutdown:** `MainWindow.CloseEmu` (`MainWindow.axaml.cs:156-192`). The server must be stopped there, before `EmuApi.Stop()`/`Release()`.

## 4. Native AOT constraints

- `UI.csproj`: `net10.0`, `IsAotCompatible=true`, **`JsonSerializerIsReflectionEnabledByDefault=false`**, `TreatWarningsAsErrors=true` (IL trim/AOT warnings are excluded from errors, but we must not add any).
- AOT publish comes from `-p:PublishAot=true` (the `Release.pubxml` profile, the Linux/macOS `makefile` `USE_AOT`, and the Windows CI matrix).
- **System.Text.Json source generation is already used and works under AOT.** `UI/Utilities/JsonHelper.cs` defines `MesenSerializerContext` and `MesenCamelCaseSerializerContext`, called as `JsonSerializer.Deserialize(json, typeof(T), MesenSerializerContext.Default)`.
- **Plan:** a dedicated `McpJsonContext : JsonSerializerContext` in `UI/Mcp/`, camelCase, `DefaultIgnoreCondition = WhenWritingNull`, not indented. It covers every request/response DTO.
  - JSON-RPC envelopes and dynamic `arguments` are handled with `JsonNode`/`JsonElement`. Both are AOT-safe.
  - Tool input schemas are hand-written JSON literals, validated at startup in a debug build.
  - No reflection and no `dynamic`.
- `System.Net.HttpListener` is part of the shared framework and trim-safe. It is not on the AOT-incompatible list. Phase 2's first commit verifies an AOT publish with the listener running.
- **Checked on this machine:** a non-admin process can bind `http://127.0.0.1:<port>/` and `http://localhost:<port>/` with `HttpListener` (http.sys) with no URL ACL. .NET 10.0.401 SDK and VS 2022/"18" are installed; `dotnet` is not on PATH (`C:\Program Files\dotnet\dotnet.exe`).
- CI runs `dotnet format --verify-no-changes`. The code follows `.editorconfig`: tabs, `if(` with no space, no `var`, `_camelCase` private fields, block-scoped namespaces.

## 5. Where the server should live: recommendation

**Your expectation holds.** A C# service in `UI/Mcp/`, `HttpListener`, source-generated JSON, no new C++.

Why not in C++ (`Core`/`InteropDLL`):
- Labels, breakpoints, `.mlb` import/export, workspace persistence and condition validation all live in **C#** (`LabelManager`, `BreakpointManager`, `DebugWorkspaceManager`, `MesenLabelFile`).
- A C++ server would bypass them. The UI would go stale, persistence would be lost, and the server would fight the UI over `SetBreakpoints`' replace-all semantics.
- It would also need a native HTTP/JSON dependency, which the brief rules out.

Why `HttpListener` rather than a raw `TcpListener` mini-HTTP server:
- It handles HTTP/1.1 parsing, keep-alive and chunked bodies correctly.
- It binds loopback without admin rights on Windows (verified). On Linux/macOS it is the managed implementation, which binds exactly the prefix host.

Fallback if the Phase 2 AOT spike surprises us: a ~200-line loopback `TcpListener` HTTP/1.1 handler. The transport sits behind a small interface so it can be swapped.

**Changes to existing files** (kept minimal and backward-compatible):
1. `DebugWindowManager`: add `AcquireDebugSession()` / `ReleaseDebugSession()`, which increment and decrement the same `_debugWindowCounter` as windows do. While MCP holds a session, closing the last window does not release the debugger, and the workspace is loaded exactly as if a debugger window were open. Releasing the MCP session when no windows are open runs the existing save-and-release path.
2. `BreakpointManager`: make `_activeCpuTypes` reference-counted (`Dictionary<CpuType,int>`) so the MCP server and debugger windows can register the same CPU independently. The existing `AddCpuType`/`RemoveCpuType` signatures stay.
3. Config: `DebuggerConfig` or `IntegrationConfig` gains `McpServerEnabled`, `McpServerPort` and `McpAllowWriteAccess` (see §8).
4. Main window: start and stop the server in `MainWindow.OnOpened` (after `InitializeEmu`) and in `CloseEmu`. Add a menu item in `MainMenuViewModel.InitDebugMenu`, an `ActionType` value, and localization strings.
5. `JsonHelper.cs` stays untouched; MCP gets its own context.

Everything else (≈15 files) is new under `UI/Mcp/`.

## 6. MCP protocol: what we implement, and SDK vs. hand-written

### Spec status (checked 2026-09-22)

- The **current revision is `2026-07-28`**. It is breaking: the protocol core is stateless.
  - **No `initialize`.** Every request carries `_meta["io.modelcontextprotocol/protocolVersion"]`, `…/clientCapabilities` and optionally `…/clientInfo`.
  - Results carry `resultType: "complete"` and `_meta["io.modelcontextprotocol/serverInfo"]`.
  - **`server/discover`** is mandatory. It returns `supportedVersions`, `capabilities` and `instructions`.
  - **No `Mcp-Session-Id`** and **no GET stream**. Server→client change notifications move to `subscriptions/listen`. `ping` and `logging/setLevel` are removed.
  - Streamable HTTP POSTs **must** carry `MCP-Protocol-Version`, `Mcp-Method` and, for `tools/call`, `Mcp-Name`. The server must check that they match the body (`HeaderMismatch` = **-32020**, HTTP 400). A `Mcp-Name` of the form `=?base64?…?=` is decoded first.
  - An unsupported version returns **-32022** `UnsupportedProtocolVersion` with `data: {supported, requested}` (HTTP 400). An unknown method returns **HTTP 404 + -32601**.
  - `tools/list` results carry `ttlMs` and `cacheScope`, and the tool order should be deterministic.
  - Origin: **must** validate `Origin`; if it is present and invalid → **403**. Servers **should** bind to 127.0.0.1.
- **Legacy `2025-11-25`** and earlier: `initialize` → `InitializeResult{protocolVersion, capabilities, serverInfo, instructions}`, then `notifications/initialized` (→ 202). The server may mint `Mcp-Session-Id`, which the client echoes; an unknown session → 404, and DELETE ends it. GET for an SSE stream may return 405. `MCP-Protocol-Version` is required after init; if it is missing, assume `2025-03-26`.
- The spec explicitly allows a **dual-era server**. A request with modern `_meta` is served statelessly; an `initialize` selects legacy semantics for that HTTP session. Clients (including Claude Code during the transition) may be on either side, so **we implement both**.

### What we implement

| Item | Modern (2026-07-28) | Legacy (2025-03-26 … 2025-11-25) |
|---|---|---|
| Discovery / handshake | `server/discover` | `initialize` (negotiate: echo the client's version if supported, else our latest legacy one), `notifications/initialized` → 202 |
| Sessions | none; ignore `Mcp-Session-Id` | mint `Mcp-Session-Id` (random 128-bit, in memory, idle expiry 30 min); unknown id → 404; `DELETE` → 200 |
| `tools/list` | + `ttlMs`, `cacheScope:"private"`, `resultType` | plain |
| `tools/call` | `CallToolResult{content:[{type:"text",text}], structuredContent, isError}` + `resultType` | same without `resultType` |
| `ping` | → -32601 (removed) | → `{}` |
| Header checks | `MCP-Protocol-Version` / `Mcp-Method` / `Mcp-Name` vs body → -32020 | `MCP-Protocol-Version` if present must equal the negotiated version |
| GET / DELETE on `/mcp` | 405 | GET → 405 (no server-initiated messages); DELETE → end session |
| Responses | always `application/json` (no SSE needed; see below) | same |

- **Capabilities:** `{ "tools": { "listChanged": false } }`. No resources, prompts, sampling, logging or subscriptions in v1. Rejecting `subscriptions/listen` with -32601 is compliant, because we don't advertise it.
- **Tool errors:** input and domain errors (bad address, no ROM loaded, write access disabled) are returned as `isError: true` tool results with a readable message, so the model can recover. Protocol errors (malformed JSON, unknown tool, bad params shape) use JSON-RPC errors: -32700, -32600, -32601, -32602, -32603.
- **Why no SSE:** we have no server-initiated messages. Long calls (`run_until`) are bounded by a wall-clock timeout well under typical client timeouts (default 10 s, max 60 s). If the HTTP client disconnects mid-`run_until`, the server pauses at the timeout as usual; nothing is left running. Progress notifications over SSE can be added later without breaking changes.
- **Batching:** JSON-RPC batches are not allowed in either era; reject with -32600.

### SDK vs. hand-written: **hand-written**

The official C# SDK (`ModelContextProtocol` 2.2.0, Aug 2026) supports 2026-07-28 and is AOT-aware. It still doesn't fit here:
- Its **Streamable HTTP server transport is `ModelContextProtocol.AspNetCore`**. That pulls ASP.NET Core and Kestrel into a desktop app, adds a lot of AOT binary size, and brings DI and hosting infrastructure the UI doesn't use.
- `ModelContextProtocol.Core` alone has no HTTP server transport. We would still write the HTTP layer and adapt it to the SDK's transport abstractions, and we would add `Microsoft.Extensions.AI.Abstractions` and logging dependencies.
- The surface we need is small: one endpoint, three to five methods, header validation, and an optional legacy session map. It is roughly 600–800 lines. Maintainers can review it in one sitting, and it has zero new packages.

The cost of hand-writing is tracking future spec changes ourselves. The protocol code is isolated in `McpProtocol.cs` so a later switch to the SDK stays contained.

## 7. Architecture

```
UI/Mcp/
  McpServer.cs            // lifecycle: Start(port)/Stop(), HttpListener loop, Origin/Host checks, status for the UI
  McpHttpHandler.cs       // POST/GET/DELETE handling, header validation, era detection, legacy sessions
  McpProtocol.cs          // JSON-RPC parse/dispatch, error codes, discover/initialize/tools/list/tools/call
  McpJsonContext.cs       // [JsonSerializable] source-gen context for all DTOs
  McpTool.cs              // tool descriptor: name, description, input schema literal, write-gated flag, handler
  McpToolRegistry.cs      // deterministic ordered list of tools
  McpDebugSession.cs      // holds the debugger (AcquireDebugSession), registers CPU types, own NotificationListener,
                          // break/frame waiters, "loading" state, execution lock
  McpArgs.cs              // typed argument readers (address parsing "$8000"/"0x8000"/"8000h"/decimal, enums by name)
  Tools/
    SessionTools.cs       // get_status, pause, resume, step, run_until, reset, load_rom, save/load_save_state, set_input
    StateTools.cs         // get_cpu_state, read_memory, write_memory, get_call_stack, get_trace_tail, evaluate
    AnalysisTools.cs      // disassemble, get_cdl, get_cdl_stats, find_references, search_memory
    LabelTools.cs         // get_labels, set_label, delete_label, set_comment, import_labels, export_labels
    BreakpointTools.cs    // list_breakpoints, add_breakpoint, remove_breakpoint
  SnesHeader.cs           // header decode at $00FFC0 (and $40FFC0 for ExHiROM probing)
  Crc32.cs                // tiny table-driven CRC32 (no System.IO.Hashing package)
  tests/
    test_mcp.py, requirements.txt, README.md
UI/Debugger/Windows/McpStatusWindow.axaml(.cs)   // status, URL, copy button, client count, last error
```

### Concurrency model

- **HTTP loop:** `HttpListener.GetContextAsync()` in a background task. Each request is handled on the thread pool, and at most 4 run concurrently (a semaphore), so a misbehaving client can't starve anything. Request bodies are capped at 1 MB.
- **Execution lock:** execution-control tools (`pause`, `resume`, `step`, `run_until`, `reset`, `load_rom`, state load/save, `set_input`) are serialized by an async `SemaphoreSlim` in `McpDebugSession`. Two clients can't interleave a `step` and a `run_until`; the second waits (bounded) or gets "busy". Read-only tools don't take it.
- **Reads** (`read_memory`, `get_cpu_state`, `disassemble`, `get_cdl`, …) call `DebugApi` directly from the pool thread, the same pattern `ToolRefreshHelper` and `MemorySearchViewModel` use.
- **Label and breakpoint mutations and `.mlb` import/export** run through `Dispatcher.UIThread.InvokeAsync(...)`. That is the thread these managers are always used from. It makes `OnLabelUpdated`/`BreakpointsChanged` fire where open windows expect them, then calls `DebugWorkspaceManager.AutoSave()`.
- **Notifications:** `McpDebugSession` owns a `NotificationListener`. On `CodeBreak` it copies the `BreakEvent` struct and completes the current break waiter. On `PpuFrameDone` it increments a frame counter and wakes frame waiters. It also tracks `BeforeGameLoad`/`GameLoaded`/`GameLoadFailed`/`EmulationStopped`/`BeforeGameUnload`. The callback never calls into `DebugApi` and never blocks.
- **Emulation is never slowed:**
  - The server adds one notification callback that does O(1) work.
  - When idle, it does nothing on the emulation thread.
  - Mutating `DebugApi` calls briefly halt emulation through `DebugBreakHelper`, exactly as the same actions in the debugger UI do.
  - Trace logging is only enabled when the agent asks for it (tracing does cost CPU).

### Debugger session lifecycle

- **Server start:** starts the listener only. The debugger is **not** attached yet, so enabling the setting has zero effect until a client calls a tool.
- **First tool call needing the debugger:** `McpDebugSession.Ensure()`.
  1. It marshals to the UI thread and calls `DebugWindowManager.AcquireDebugSession()`. That loads the workspace, labels and breakpoints, and the core auto-loads CDL.
  2. It calls `BreakpointManager.AddCpuType(cpu)` for every CPU in `RomInfo.CpuTypes`, then `BreakpointManager.SetBreakpoints()`.
  3. It calls `ConfigApi.SetDebuggerFlag(<cpu>DebuggerEnabled, true)` so that break-on-BRK and similar options behave as in the debugger window. That is optional and follows the existing debugger config.
- **`GameLoaded`:** re-register CPU types for the new ROM. `DebugWorkspaceManager` already reloads the workspace on `GameLoaded`.
- **Release:** when the server is disabled or the app closes, the session is released (`ReleaseDebugSession()`), which saves the workspace and CDL like closing the last debug window. v1 doesn't release on client disconnect: HTTP is connectionless, so "disconnect" can't be detected reliably.
- **Input overrides** set by `set_input` are always cleared when the tool returns, including on error or timeout. No buttons get stuck.

### `run_until` (main exploration primitive)

Arguments:
- `timeout_frames` (default 600, max 36000)
- `timeout_ms` (default 10000, max 60000)
- optional `breakpoint`: a temporary exec/read/write breakpoint added for this call only
- optional `stop_on`: `["breakpoint","nmi","irq","frame"]`, mapped to `StepType.RunToNmi`/`RunToIrq`

Algorithm:
1. Take the execution lock. Require a loaded ROM and not loading. Call `Ensure()`.
2. If a temporary breakpoint was given, validate its condition (`EvaluateExpression`) and add it via `BreakpointManager.AddTemporaryBreakpoint` on the UI thread. It is removed in `finally`.
3. Arm a fresh break waiter and record the start frame and time. Resume (`DebugApi.ResumeExecution()`, or `Step(..., RunToNmi)` etc.).
4. Wait for the first of: `CodeBreak` (any source), frame budget reached (counted via `PpuFrameDone`), wall-clock timeout, `BeforeGameLoad`/`EmulationStopped`, or server shutdown.
5. On a timeout, call `EmuApi.Pause()` and wait up to 1 s for the resulting `CodeBreak(Source=Pause)`, so the call **always returns with execution stopped at an instruction boundary**. The only exception is a ROM unload, which returns `stop_reason:"rom_unloaded"`.
6. Return:
   - `stop_reason`: `breakpoint` | `step` | `pause` | `nmi` | `irq` | `brk` | `timeout_frames` | `timeout_ms` | `rom_unloaded` | `other:<BreakSource>`
   - `break_source`, `source_cpu`
   - `breakpoint`: the matched breakpoint via `GetBreakpointById`
   - `operation`: {type, address, value, memory_type}
   - frames and ms elapsed, the CPU state of `source_cpu` and the main CPU, and the current instruction disassembled with its label.

Edge cases:
- Already running when called: arm the waiter without resuming.
- A user click in the debugger window resumes or steps concurrently: the waiter sees the next `CodeBreak`, which may be the user's. That is reported honestly via `break_source`.
- Netplay client connected: pause and resume are ignored, so return an error up front.

`step` uses the same waiter. It calls `Step(cpu, count, type)` and waits for `CodeBreak` with a timeout. `StepOver`/`StepOut` repeat `count` times. `frame`/`scanline` map to `PpuFrame`/`PpuScanline` with count.

### Tools (v1)

Conventions:
- Addresses accept `$8000`, `0x8000`, `8000h` or decimal. Output addresses are hex strings (`"$00:8000"`-style for 24-bit SNES CPU addresses, `"$1234"` elsewhere).
- `memory_type` and `cpu` use Mesen's enum names (`SnesPrgRom`, `SnesMemory`, `Snes`, `Spc`, …). Each description lists the common values, and an unknown value error lists the valid ones for the loaded ROM.
- Every result is compact JSON in `structuredContent`, mirrored as a text block. Caps are stated in the description, and results say `"truncated": true` with the cap they hit.

| Tool | Gate | Backing |
|---|---|---|
| `get_status` | read | `GetRomInfo`, `GetRomHash`, PRG CRC32, `SnesHeader`, `IsRunning`/`IsPaused`/`IsExecutionStopped`, `GetTimingInfo`, CPU list, memory types + sizes, server write-access flag |
| `pause`, `resume` | read* | `EmuApi.Pause/Resume` + waiter |
| `step` | read* | `DebugApi.Step`; types `instruction`, `over`, `out`, `cycle`, `scanline`, `frame`, `nmi`, `irq`, `back` |
| `run_until` | read* | above |
| `reset` | write | `ExecuteShortcut(ExecReset / ExecPowerCycle)` |
| `load_rom` | write | `EmuApi.LoadRom` (waits for `GameLoaded`/`GameLoadFailed`) |
| `save_save_state`, `load_save_state` | write | `SaveStateFile/LoadStateFile` (path) or slot 1–10 |
| `set_input` | read* | `SetInputOverrides` for N frames (max 3600), then cleared; buttons by name; returns frames elapsed |
| `get_cpu_state` | read | `GetCpuState<T>` per CPU; 65816 returns A/X/Y/SP/D/DB/PB/PC, P as a byte **and** decoded flags N V M X D I Z C E |
| `read_memory` | read | `GetMemoryValues`; max 4096 bytes/call; hex string (+ optional ASCII) |
| `write_memory` | write | `SetMemoryValues` (undoable via the debugger's undo) |
| `get_call_stack` | read | `GetCallstack`, labels resolved |
| `set_trace` / `get_trace_tail` | read | `SetTraceOptions` (enable, condition, labels on) / `GetExecutionTrace`; max 500 rows |
| `evaluate` | read | `EvaluateExpression`. Cheap, and useful for the model to check conditions and read registers by name. |
| `disassemble` | read | `GetDisassemblyOutput`; max 200 rows; address, bytes, text (labels), effective address + value, label, comment, flags (code/data/unknown, M/X for SNES) |
| `get_cdl` | read | `GetCdlData`; raw flags hex (max 16 KB) + run-length summary `[{start,end,kind:code/data/unknown/mixed, m8, x8, jump_target, sub_entry}]` |
| `get_cdl_stats` | read | `GetCdlStatistics` per region + function count; percentages |
| `find_references` | read | Static: `FindOccurrences` for the label name and the hex operand forms (`$XXXX`, `$XX:XXXX`, bank-relative), filtered to lines whose operand or effective address resolves to the target. Optional dynamic: `GetMemoryAccessCounts` for the target (read/write/exec counts, "was accessed at runtime"). Limitations stated (indirect/computed accesses, and only disassembled code). |
| `search_memory` | read | `GetMemoryState` + byte pattern with `??` wildcards or text; max 256 hits |
| `get_labels` | read | `LabelManager.GetLabels`, filter by memory type / range / name substring; max 1000 |
| `set_label`, `delete_label`, `set_comment` | read** | `LabelManager` on the UI thread, with the dialog's validation; `AutoSave()` |
| `import_labels`, `export_labels` | read** | `DebugWorkspaceManager.LoadSupportedFile` / `MesenLabelFile.Export` on the UI thread |
| `list_breakpoints`, `add_breakpoint`, `remove_breakpoint` | read** | `BreakpointManager` on the UI thread; condition validated with `EvaluateExpression`; ids are stable GUID-ish handles we assign (the core ids shift) |

- **\*** Execution control (pause/resume/step/run_until/set_input) isn't write-gated. It is needed for read-only exploration and doesn't change game data beyond what normal play does. The brief lists `set_input` under session tools, not write-gated ones.
- **\*\*** Labels, comments and breakpoints are debugger metadata, not emulated state. They are the agent's main persistent output, so they are **not** write-gated. `import_labels`/`export_labels` touch the filesystem; export only writes `.mlb` (the extension is enforced), and paths are absolute.

**Write-gated** tools (`reset`, `load_rom`, `load_save_state`, `save_save_state`, `write_memory`, and later `set_cpu_state`) are always listed. When write access is off they return `isError` with "enable 'Allow write access' in Debugger settings". Listing them lets the model tell the user what to enable.

Please confirm the gate choices above, especially for `set_input` and the label tools.

## 8. Settings and UI

- **Config:** new properties in `IntegrationConfig`, because the Debugger config window's "Integration" tab is the natural home:
  - `McpServerEnabled` (false)
  - `McpServerPort` (int, default **8765**, range 1024–65535)
  - `McpAllowWriteAccess` (false)

  Persistence is automatic; `Configuration` is already in the source-gen context.
- **Debugger config window → Integration tab:** a "MCP server" `OptionSection` with the enable checkbox, port `MesenNumericUpDown`, write-access checkbox, and a one-line security note. Changes take effect on OK/Apply: the server restarts if the port changed.
- **Debug menu:** a new "MCP Server…" item near "Debug Log" (`ActionType.OpenMcpServer`) opens `McpStatusWindow`, which shows:
  - status (Off / Listening / Error: port in use …)
  - the URL `http://127.0.0.1:<port>/mcp` in a read-only textbox with a **Copy** button
  - a copyable `claude mcp add --transport http mesen http://127.0.0.1:<port>/mcp` line
  - write-access state
  - active legacy sessions and the last request time
  - a "Settings…" button that opens the Integration tab

  The window doesn't count as a debug window, so it doesn't attach the debugger.
- Localization strings go in `resources.en.xml` only; other languages fall back to English, as upstream does.

## 9. Security model

- **Loopback only:** the `HttpListener` prefixes are `http://127.0.0.1:<port>/mcp/` and `http://localhost:<port>/mcp/`. Never `+` or `*`.
- **Host header check:** the server also requires `Host` ∈ {`127.0.0.1:<port>`, `localhost:<port>`, `[::1]:<port>`}. http.sys already rejects others; the check is defence in depth on the managed Linux/macOS implementation.
- **Origin check:** if an `Origin` header is present, it must be `http://127.0.0.1:<port>`, `http://localhost:<port>` or `http://[::1]:<port>`. Anything else, including `null`, gets **403**. Requests with no `Origin` are allowed; non-browser MCP clients don't send one. Together these block DNS rebinding and drive-by browser POSTs. A cross-site browser request to `/mcp` would also need a CORS preflight (JSON content type plus custom headers), and we never answer `OPTIONS` with CORS headers.
- **Content-Type:** POST must be `application/json`. That blocks "simple request" form posts from browsers.
- **Off by default.** Write access is a separate setting, also off by default.
- **Filesystem:**
  - `load_rom`, the save-state tools and `import_labels` read paths the agent supplies. `load_rom` and the state tools are write-gated.
  - `export_labels` and `save_save_state` write only files with the expected extension (`.mlb`, `.mss`) and refuse to overwrite unless `overwrite: true`.
  - No tool reads arbitrary files back to the client.
- **Resource caps:** request body 1 MB, concurrency 4, per-tool output caps, `run_until` wall-clock ≤ 60 s.
- **Out of scope:** authentication and non-loopback access. The docs will say plainly that any local process can talk to the port while it's enabled.

## 10. Implementation order (Phase 2 commits)

1. `Mcp: HTTP transport, JSON-RPC core, server/discover, initialize, tools/list`. Includes the empty tool registry, Origin/Host/header checks, legacy sessions, config properties, and start/stop wiring (enabled via config only). Verify: `dotnet build`, AOT publish, and curl smoke tests.
2. `Mcp: read-only state tools`: `get_status`, `get_cpu_state`, `read_memory`, `get_call_stack`, `evaluate`, `disassemble`, `get_cdl`, `get_cdl_stats`, `search_memory`, `find_references`, trace tools. Includes `DebugWindowManager.AcquireDebugSession`.
3. `Mcp: execution control`: `pause`, `resume`, `step`, `run_until`, `set_input`, plus the waiter/notification plumbing.
4. `Mcp: labels and breakpoints`: label, comment and breakpoint tools, `.mlb` import/export, and the ref-counted `BreakpointManager` CPU types.
5. `Mcp: write-gated tools`: `write_memory`, `reset`, `load_rom`, save states.
6. `Mcp: settings UI and Debug menu status window`.
7. `Mcp: Python test script`, then `docs/MCP.md`.

Each commit builds cleanly (`TreatWarningsAsErrors`), passes `dotnet format --verify-no-changes`, and keeps the app working with the server disabled. I'll build the Release x64 solution with MSBuild (the C++ core is needed at runtime) and run `dotnet publish -p:PublishAot=true` for the UI on Windows.

## 11. Verification plan (Phase 3)

- `UI/Mcp/tests/test_mcp.py` uses the official `mcp` Python package (streamable HTTP client) and takes `--url` and `--rom`. It checks:
  - connect and list tools
  - `load_rom` (needs write access)
  - `pause`, then `read_memory` of WRAM
  - `disassemble` the reset vector: read $00FFFC (native $00FFEA/…); for SNES use the emulation-mode reset vector
  - `add_breakpoint` exec at the reset vector target + N, then `reset` → `run_until` → assert `stop_reason == breakpoint`
  - `set_label` → `get_labels` contains it → `export_labels` to a temp `.mlb` → file contains the row
- Note: the Python `mcp` package may speak only one era at a given time. The script records which era was negotiated. I'll also run raw `curl` checks against both eras.
- Claude Code: `claude mcp add --transport http mesen http://127.0.0.1:8765/mcp`, then a short exploration session. I'll report what worked.
- Edge cases:
  - **No ROM loaded:** clear errors, no zeros.
  - **During a ROM load:** "loading" error or wait.
  - **Emulator closed mid-`run_until`:** the server stops, the request returns an error or the connection drops, and the app exits without hanging (shutdown cancels waiters before `EmuApi.Stop()`).
  - **Two clients:** concurrent reads work, and execution control is serialized.
  - **Debugger window open and closed while MCP is active:** the debugger isn't released and breakpoints survive.

## 12. Risks and open questions

1. **Tool gating:** the proposal above keeps `set_input`, stepping, labels and breakpoints ungated. Do you want labels and breakpoints gated too?
2. **CRC32 / SNES header:** is a C#-side PRG CRC32 plus header decode acceptable? The alternative is a small `InteropDLL` export (`GetRomCrc32`, and a `GetSnesCartInfo`), which is a native change but not a dependency. I'd defer it.
3. **Trace logger conflicts:** `set_trace` writes the same per-CPU options the Trace Logger window uses. If both are active, the last writer wins. Proposal: document it, and have `get_trace_tail` report whether tracing is enabled.
4. **`find_references` fidelity:** static references are text-based over label-resolved disassembly. They miss computed or indirect accesses, and depend on DB/D and bank for 65816 absolute addressing. A real xref index is a good follow-up (it pairs with the future exporter) but is out of v1 scope. It would need CDL-guided linear decode per CPU.
5. **Port default:** 8765 is arbitrary; tell me if you prefer something else. Only one Mesen instance can bind it. A second instance reports "port in use" in the status window.
6. **Upstreaming:** the two small changes to `DebugWindowManager` and `BreakpointManager` are the only behavioural edits outside `UI/Mcp`. Both are backward-compatible.

## Sources

- MCP versioning, current revision `2026-07-28`: https://modelcontextprotocol.io/specification/versioning
- 2026-07-28 changelog: https://modelcontextprotocol.io/specification/2026-07-28/changelog
- Streamable HTTP (2026-07-28): https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http
- Versioning and backward compatibility: https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning
- `server/discover`: https://modelcontextprotocol.io/specification/2026-07-28/server/discover
- C# SDK packages: https://www.nuget.org/packages/ModelContextProtocol.Core, https://www.nuget.org/packages/ModelContextProtocol.AspNetCore, https://github.com/modelcontextprotocol/csharp-sdk/releases
