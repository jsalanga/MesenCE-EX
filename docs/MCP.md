# MCP server

Mesen has a built-in [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server. It lets an AI agent
(Claude Code, Claude Desktop or any other MCP client) drive the debugger: inspect CPU state and memory, disassemble,
step, set breakpoints and run until they're hit, press controller buttons, and record its findings as labels and
comments in the debugger workspace.

It was designed for reverse engineering and disassembly of SNES games, but the same tools work for every console
the debugger supports (NES, Game Boy/GBC, GBA, PC Engine, SMS/Game Gear, WonderSwan).

## Enabling the server

1. Open **Debug > Settings** and select the **Integration** tab.
2. In the **MCP server** section, check **Enable the MCP server**. The default port is `8765`.
3. Optionally, check **Allow write access** (see [Write access](#write-access)).

The server is off by default. **Debug > MCP Server** shows its status, the URL (`http://127.0.0.1:8765/mcp`) and a
ready-to-paste `claude mcp add` command, with copy buttons.

The server doesn't do anything until a client calls a tool. The first call attaches the debugger, exactly as if a
debugger window had been opened: the debugger workspace (labels, breakpoints) is loaded and the code/data logger
starts recording. The debugger stays attached until the server is disabled or Mesen is closed, even if debugger
windows are opened and closed in the meantime.

## Client setup

### Claude Code

```
claude mcp add --transport http mesen http://127.0.0.1:8765/mcp
```

Then ask Claude to use the `mesen` tools, e.g. *"Use the mesen MCP server to find the routine that reads the
controller on the title screen and label it"*.

### Claude Desktop

Claude Desktop starts MCP servers as local processes, so the HTTP endpoint is bridged with
[`mcp-remote`](https://www.npmjs.com/package/mcp-remote) (requires Node.js). In `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "mesen": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://127.0.0.1:8765/mcp"]
    }
  }
}
```

### Other clients

Any client that supports the Streamable HTTP transport can connect to `http://127.0.0.1:<port>/mcp`.
The server supports protocol version `2026-07-28` (stateless, `server/discover`) and the earlier
handshake-based versions `2025-11-25`, `2025-06-18` and `2025-03-26` (`initialize` and `Mcp-Session-Id`).
It only provides tools (no resources, prompts or subscriptions) and always answers with `application/json`.

## Security

- **Loopback only.** The server listens on `127.0.0.1` and `::1`, never on a network interface, so other computers
  can't connect to it. It uses a small HTTP handler on a loopback-bound socket rather than `HttpListener`, which on
  Windows can accept connections on every interface.
- **DNS rebinding protection.** Requests must have a `Host` header of `127.0.0.1:<port>`, `localhost:<port>` or
  `[::1]:<port>`. When a request has an `Origin` header (browsers always send one), it must be a loopback origin.
  Otherwise the request is rejected with `403`. Requests must use `Content-Type: application/json`, which a website
  can't send cross-origin without a CORS preflight, and the server never allows one.
- **No authentication.** Any program running on the computer can use the server while it's enabled (just as it could
  read Mesen's memory). Only enable it while you use it.
- **Off by default**, and write access is a separate setting, also off by default.
- **Files.** Tools only read or write files at paths the client gives explicitly (`load_rom`, `import_labels`,
  `export_labels`, save states). Paths must be absolute, exports only write `.mlb` files and save states only
  `.mss` files, and existing files are only replaced with `overwrite: true`. No tool returns the content of
  arbitrary files.
- **Resource limits.** 1 MB request bodies, 4 concurrent requests, 32 connections, capped result sizes, and
  `run_until`/`step` wait for at most 60 seconds.

## Write access

These tools change the emulated state and only work when **Allow write access** is enabled: `write_memory`,
`set_cpu_register`, `reset`, `load_rom`, `save_save_state`, `load_save_state`. When it's disabled they're still listed,
so the agent can tell you what to enable, but they return an error.

Everything else works without write access: the read-only tools, execution control (pause, resume, step, `run_until`,
`set_input`), and labels, comments and breakpoints. These are debugger metadata rather than emulated state, and they're
the agent's main output.

## Concepts

**Addresses.** Arguments accept JSON numbers (decimal) or strings, which are always hexadecimal: `"$8000"`,
`"0x8000"`, `"8000"`, or bank:address `"$80:8000"`. Results show addresses as hex strings (e.g. `"$808000"`).

**Memory types** use Mesen's names. CPU address spaces (`SnesMemory`, `SpcMemory`, `Sa1Memory`, `NesMemory`, ...) are
what a CPU sees, including mirrors and I/O registers. Physical memory types (`SnesPrgRom`, `SnesWorkRam`,
`SnesSaveRam`, `SnesVideoRam`, `SnesCgRam`, `SnesSpriteRam`, `SpcRam`, `NesPrgRom`, ...) are offsets into that memory.
`get_status` lists the memory types available for the loaded ROM, with their sizes.

**Labels and comments** are stored like the debugger's: an address in a CPU address space is converted to its
physical location (e.g. `$80:8000` becomes `SnesPrgRom $0000`), so the label applies wherever that memory is mapped.
Labels, comments and breakpoints are saved in Mesen's debugger workspace (`Debugger/<rom name>.json` in Mesen's
folder) a few seconds after each change, and open debugger windows refresh immediately.
`export_labels`/`import_labels` use the same `.mlb` format as the debugger's **File > Export/Import labels**.

**Execution.** `run_until` is the main exploration primitive. It resumes execution and returns when a breakpoint
(or another break) hits, or when a frame or time limit is reached. It always returns with execution paused at an
instruction boundary, with the stop reason, the breakpoint and memory operation that triggered it, and the CPU state.
Execution-control tools from several clients are serialized. The server never blocks the emulation thread: waits are
driven by the emulator's notifications. If a client disconnects while waiting, execution is paused and the request is
cancelled.

**Code/data logger (CDL).** While the debugger is attached, Mesen records which bytes are executed as code or read as
data (and, for SNES code, the M/X flags used). `get_cdl`, `get_cdl_stats` and `list_functions` expose this information,
and `disassemble` marks each row as `code`, `data` or `unknown`. Mesen saves the CDL data automatically.

### Typical workflow (SNES)

1. `get_status`: ROM, mapping, vectors, CPUs, memory types.
2. `reset` (or `load_rom`) with `pause: true`, then `disassemble` from the reset vector.
3. `add_breakpoint` on an interesting address (e.g. a write to a WRAM variable, or the NMI handler), then `run_until` to
   reach it. `get_call_stack`, and `get_trace_tail` after `set_trace`, show how execution got there.
4. `set_input` to press buttons and reach new code, and `get_cdl_stats` to measure coverage.
5. `set_label`/`set_comment` to record findings, and `export_labels` to save them to a `.mlb` file.

## Limitations

- Breakpoint conditions are validated with Mesen's expression evaluator, which accepts some incomplete expressions
  (e.g. `a ==`). The debugger's breakpoint editor has the same behavior.
- `find_references` searches the disassembly text and effective addresses. Effective addresses are computed with the
  CPU's *current* register state (DB, D, bank), and indirect or computed accesses can't be found statically.
  `runtime_access_counts` shows whether the address was accessed at runtime.
- `disassemble` decodes code that hasn't been executed yet speculatively (`kind: unknown`). For the 65816, it uses the
  currently known M/X flags, which may not be the ones the code expects.
- `set_trace` uses the same per-CPU settings as the Trace Logger window. Opening or closing that window overrides them.
- `set_input` requires a controller in the chosen port (see Mesen's input settings).
- Only one Mesen instance can use a given port. A second instance shows "Port ... is already in use" in
  **Debug > MCP Server**.

## Testing

`UI/Mcp/tests/test_mcp.py` is an end-to-end test that uses the official MCP Python SDK:

```
pip install -r UI/Mcp/tests/requirements.txt
python UI/Mcp/tests/test_mcp.py --rom "C:/path/to/game.sfc"
```

Without `--rom`, it generates and loads a small homebrew SNES test ROM. It needs the server enabled with write access
(or use `--no-load` with a ROM already loaded in Mesen).

## Tool reference

Results are compact JSON objects (in `structuredContent`, and also as text). Large results are capped. When a result
is truncated, it contains `"truncated": true` and a note, or a `next_offset` for paged tools.

#### `get_status`

Returns the emulator status: loaded ROM (name, path, console, SHA1, PRG ROM CRC32, size), decoded cartridge header (SNES: title, map mode, chipset, ROM/SRAM size, region, checksum, interrupt vectors; NES: iNES header), CPUs present with their current PC, whether execution is paused (and why it last stopped), frame count, every available memory type with its size in bytes, and whether write access is enabled. Call this first.

#### `get_cpu_state`

Returns the registers of a CPU as hex strings. For the SNES 65816 (cpu Snes or Sa1): pc (24-bit, PB:PC), a, x, y, sp, d, db, pb, p, decoded flags n v m x d i z c plus e (emulation mode), and accumulator_8bit/index_8bit (the effective M/X register widths). For Spc: pc, a, x, y, sp, psw and flags. Other CPUs return all their state fields. Also returns the PPU position (scanline/cycle/frame) of the console. Values are only fully consistent while execution is paused.

| Argument | Type | Description |
|---|---|---|
| `cpu` | string | CPU to read (Snes, Spc, Sa1, Gsu, Cx4, NecDsp, St018, Nes, Gameboy, Gba, Pce, Sms, Ws). Default: the console's main CPU. |

#### `read_memory`

Reads up to 4096 bytes from a memory type. CPU address spaces (SnesMemory, SpcMemory, Sa1Memory, NesMemory, ...) are read the way the CPU sees them (without side effects on I/O registers); physical regions (SnesPrgRom, SnesWorkRam, SnesSaveRam, SnesVideoRam, SnesCgRam, SnesSpriteRam, SpcRam, ...) use offsets within that memory. Returns 'hex' (space-separated bytes). With format=rows, returns 16-byte rows with ASCII, like a hex editor.

| Argument | Type | Description |
|---|---|---|
| `memory_type` *(required)* | string | Memory type to read, e.g. SnesMemory (65816 address space), SnesPrgRom, SnesWorkRam, SpcRam. See get_status for the list. |
| `address` *(required)* | address | Start address (offset within the memory type). JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `length` | integer | Number of bytes to read (1-4096, default 256). |
| `format` | string: `hex`, `rows` | Output format: hex (default) or rows. |

#### `search_memory`

Searches a memory type for a byte pattern or text and returns the matching addresses (max 1000). Pattern bytes are hex, space separated, with ?? as wildcard (e.g. "A9 ?? 8D 00 21"). Multi-byte values are little-endian in memory.

| Argument | Type | Description |
|---|---|---|
| `memory_type` *(required)* | string | Memory type to search, e.g. SnesPrgRom, SnesWorkRam. |
| `pattern` | string | Hex byte pattern with optional ?? wildcards, e.g. "20 ?? 80". |
| `text` | string | ASCII text to search for (alternative to pattern). |
| `start` | address | Start address of the search (default 0). JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `end` | address | End address of the search, inclusive (default: end of memory). JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `max_results` | integer | Maximum number of results (default 100, max 1000). |

#### `evaluate`

Evaluates an expression with Mesen's debugger expression syntax (the same syntax as breakpoint conditions and watch expressions), e.g. "a", "x + y", "[$7E0010]" (byte read), "{$7E0010}" (word read), "pc", "scanline", or a label name. Useful to check a breakpoint condition before using it. Returns the value (decimal and hex) and its type.

| Argument | Type | Description |
|---|---|---|
| `expression` *(required)* | string | Expression to evaluate. |
| `cpu` | string | CPU context for registers (default: main CPU). |

#### `get_call_stack`

Returns the call stack of a CPU (innermost frame first) as tracked by the debugger: for each frame, the function entry address, the call site, the return address, labels when available, and whether the frame is an NMI/IRQ handler.

| Argument | Type | Description |
|---|---|---|
| `cpu` | string | CPU (default: main CPU). |

#### `set_trace`

Enables or disables the execution trace logger for a CPU (it records every executed instruction in a 30000-row ring buffer, with labels). Tracing slows emulation down a little. An optional condition (debugger expression) only logs matching instructions. Note: the Trace Logger window uses the same settings and overrides them when it's opened or closed.

| Argument | Type | Description |
|---|---|---|
| `enabled` *(required)* | boolean | Enable (true) or disable (false) tracing. |
| `cpu` | string | CPU to trace (default: main CPU). |
| `condition` | string | Optional condition, e.g. "pc >= $808000 && pc < $809000". |
| `clear` | boolean | Clear the trace buffer (default true when enabling). |

#### `get_trace_tail`

Returns the most recently executed instructions from the trace logger in chronological order (max 500 rows). Each row has the CPU, PC, byte code and the formatted log text (disassembly with labels, effective address and registers). Tracing must first be enabled with set_trace.

| Argument | Type | Description |
|---|---|---|
| `count` | integer | Number of rows (1-500, default 50). |

#### `disassemble`

Disassembles code the way Mesen's debugger shows it (max 200 instructions). Each row has the CPU address, the absolute location (e.g. SnesPrgRom offset), byte code, instruction text with labels substituted, effective address (plus the value on the current instruction) (computed with the CPU's current register state, so only reliable at the current PC), label, comment, and kind: code (executed/verified by the code/data logger), data (verified data), or unknown (not yet executed; disassembled speculatively, for 65816 using the currently known M/X flags, so REP/SEP changes aren't followed: run or step through the code for an exact listing). Returns next_address to continue. Defaults to the current PC.

| Argument | Type | Description |
|---|---|---|
| `cpu` | string | CPU (default: main CPU). |
| `address` | address | Start address in the CPU's address space (default: current PC). If memory_type is a physical memory type (e.g. SnesPrgRom), this is an offset in that memory and is converted to the CPU address where it's currently mapped. JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `memory_type` | string | Optional memory type of 'address' (default: the CPU's address space). |
| `count` | integer | Number of instructions (1-200, default 30). |
| `before` | integer | Number of rows to include before the address (0-50, default 0), useful to see context before the PC. |
| `speculative` | boolean | Also disassemble bytes the code/data logger hasn't identified yet (default true). When false, unexplored regions are collapsed like in the debugger's default view. |

#### `get_cdl`

Returns code/data logger (CDL) information for a range: which bytes were executed as code, read as data, or never accessed (unknown). The summary is a list of contiguous runs {start, end, kind: code|data|code+data|unknown}; for SNES code runs it includes m8/x8 (the 65816 M and X flags recorded when the code was executed, i.e. 8-bit accumulator/index) and the coprocessor (Gsu/Cx4) when applicable. Also lists function entry points (subroutine targets) and jump targets in the range. Summary range max 262144 bytes; include_raw adds the raw flag bytes (max 4096 bytes; bits: 01=code 02=data 04=jump target 08=sub entry 10=X8 20=M8 40=GSU 80=Cx4 on SNES).

| Argument | Type | Description |
|---|---|---|
| `memory_type` | string | Memory type with CDL data (default: PRG ROM, e.g. SnesPrgRom). CPU address spaces are also accepted. |
| `address` | address | Start address/offset (default 0). JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `length` | integer | Number of bytes (1-262144, default 4096). |
| `include_raw` | boolean | Include the raw CDL flag bytes as hex (default false). |

#### `get_cdl_stats`

Returns code/data logger coverage for each memory type that supports it (PRG ROM, and CHR ROM for NES): bytes and percentage identified as code, data and still unknown, plus the number of functions and jump targets found. Use it to measure exploration progress.

#### `list_functions`

Lists function entry points found by the code/data logger (targets of JSR/JSL/calls, and interrupt handlers), with their CPU address (where currently mapped), absolute offset and label. Paged: max 2000 per call.

| Argument | Type | Description |
|---|---|---|
| `memory_type` | string | PRG ROM memory type (default: the console's PRG ROM). |
| `offset` | integer | Index of the first function to return (default 0). |
| `limit` | integer | Maximum number of functions (1-2000, default 500). |
| `unlabeled_only` | boolean | Only return functions that don't have a label yet (default false). |

#### `find_references`

Finds code that references an address: searches the whole disassembly for instructions whose operand is the address (hex), whose label is used, or whose effective address (computed with the current register state) is the address. Also returns the runtime access counters (how many times the address was read/written/executed since power on) and its CDL flags. Limitations: only code that the disassembler shows (known code, or speculative disassembly) is searched, and indirect/computed accesses can be missed. Each result has a match type: effective_address (the instruction's effective address, computed with the current D/DB/bank state, is the target), operand or label (the operand is the target's full address or label), operand_16bit (same 16-bit operand, bank not confirmed, only with search_16bit), operand_16bit_other_bank (same 16-bit operand, but the effective address is elsewhere). Mirrors are merged. Max 200 results.

| Argument | Type | Description |
|---|---|---|
| `address` *(required)* | address | Target address. JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `memory_type` | string | Memory type of the address (default: the CPU's address space). Physical types (SnesPrgRom, SnesWorkRam...) are converted to the CPU address. |
| `cpu` | string | CPU whose code is searched (default: main CPU). |
| `search_16bit` | boolean | Also match 16-bit operands with the same low 16 bits (default true for 24-bit CPUs). |
| `include_unexplored` | boolean | Also search code that hasn't been executed yet, disassembled speculatively (default true). Such results have kind=unknown and may be data misread as code. |

#### `pause`

Pauses execution (breaks into the debugger at an instruction boundary) and returns where execution stopped: stop reason, CPU state and the current instruction. Does nothing if already paused.

#### `resume`

Resumes execution. Returns immediately; use run_until instead to resume and wait for a breakpoint.

#### `step`

Steps execution and waits until it stops again. Types: instruction (default), over (step over subroutine calls), out (run until the current subroutine returns), cycle (CPU cycles), ppu_cycle, scanline, frame, nmi (run until the next NMI handler starts), irq (until the next IRQ), back_instruction/back_scanline/back_frame (step backward using rewind data; count ignored). A breakpoint hit during the step stops it early. Returns the stop reason, CPU state and current instruction.

| Argument | Type | Description |
|---|---|---|
| `type` | string: `instruction`, `over`, `out`, `cycle`, `ppu_cycle`, `scanline`, `frame`, `nmi`, `irq`, `back_instruction`, `back_scanline`, `back_frame` | Step type (default instruction). |
| `count` | integer | Number of instructions/cycles/scanlines/frames, or repetitions for over/out (default 1, max 100000; frame max 3600). |
| `cpu` | string | CPU to step for instruction-level steps (default: main CPU). Other CPUs keep running normally. |
| `timeout_ms` | integer | Maximum wall-clock time to wait (default 10000, max 60000). On timeout, execution is paused. |

#### `run_until`

The main exploration primitive: resumes execution and waits until execution stops (breakpoint hit, break on NMI/IRQ if requested, or another break such as BRK), or until a timeout in frames or milliseconds is reached, in which case execution is paused. Always returns with execution paused at an instruction boundary (unless the ROM was unloaded), with: stop_reason (breakpoint, nmi, irq, step, pause, brk/cop/..., timeout_frames, timeout_ms, rom_unloaded), the breakpoint that was hit, the memory operation that triggered it (address/value/type), frames and milliseconds elapsed, CPU state and the current instruction. Optionally adds a temporary breakpoint (break_address, ...) that only exists for this call; existing breakpoints stay active (see add_breakpoint).

| Argument | Type | Description |
|---|---|---|
| `timeout_frames` | integer | Stop after this many frames (default 600 = ~10 seconds of game time, max 36000). |
| `timeout_ms` | integer | Stop after this wall-clock time in milliseconds (default 10000, max 60000). |
| `until` | string: `nmi`, `irq` | Optional: nmi or irq to stop at the start of the next NMI/IRQ handler. |
| `break_address` | address | Optional temporary breakpoint address (in break_memory_type). JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `break_end_address` | address | Optional end address (inclusive) to make the temporary breakpoint a range. JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `break_type` | string: `exec`, `read`, `write`, `rw` | Temporary breakpoint type: exec (default), read, write, or rw. |
| `break_memory_type` | string | Memory type of break_address (default: the CPU's address space, e.g. SnesMemory). |
| `break_condition` | string | Optional condition for the temporary breakpoint, in Mesen's expression syntax (e.g. "a == $10"). |
| `break_exact_cpu_address` | boolean | Only match the exact CPU address, not its mirrors (default false: like add_breakpoint, the physical location is used so all mirrors match). |
| `cpu` | string | CPU for the temporary breakpoint (default: main CPU). |

#### `set_input`

Holds controller buttons for a number of frames (max 3600), then releases them, to drive gameplay and reach new code. If execution is paused, it advances exactly that many frames and stays paused; if running, it keeps running. A breakpoint hit during that time stops early (buttons are always released). Returns frames elapsed and the stop state. Buttons: a, b, x, y, l, r, up, down, left, right, select, start (u/d are extra buttons on some controllers). An empty button list holds nothing (lets time pass with no input).

| Argument | Type | Description |
|---|---|---|
| `buttons` *(required)* | string[] | Buttons to hold, e.g. ["start"] or ["right", "b"]. |
| `frames` | integer | Number of frames to hold the buttons (default 1, max 3600). |
| `port` | integer | Controller port (0 = player 1, default 0). |

#### `get_labels`

Lists labels and comments (sorted by memory type and address), optionally filtered. Each entry has the memory type, address, end address (for multi-byte labels), label, comment and, for code/ROM labels, the main CPU address where it's currently mapped. Paged: max 2000 per call.

| Argument | Type | Description |
|---|---|---|
| `memory_type` | string | Only return labels in this memory type (e.g. SnesPrgRom, SnesWorkRam, SnesRegister). |
| `start` | address | Only return labels at or after this address (in memory_type). JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `end` | address | Only return labels at or before this address (in memory_type). JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `search` | string | Only return labels whose name or comment contains this text (case-insensitive). |
| `offset` | integer | Index of the first label to return (default 0). |
| `limit` | integer | Maximum number of labels (1-2000, default 200). |

#### `set_label`

Creates or updates a label (and optional comment) at an address, exactly like the debugger's label editor, and saves it in Mesen's workspace. The address can be given in a CPU address space (e.g. SnesMemory $80:8000): it's converted to the underlying physical location (e.g. SnesPrgRom offset $0000 or SnesWorkRam offset) so the label applies wherever that memory is mapped. Label names must match [@_a-zA-Z][@_a-zA-Z0-9]* and be unique. If a label already starts at this address it's replaced (omitted label/comment arguments keep their current value); a label overlapping the range at another address is an error. Use length > 1 for multi-byte data (tables, variables); references inside it show as label+offset.

| Argument | Type | Description |
|---|---|---|
| `address` *(required)* | address | Address of the label. JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `memory_type` | string | Memory type of the address (default: the main CPU's address space, e.g. SnesMemory). |
| `label` | string | Label name (empty string to only have a comment). |
| `comment` | string | Comment (use \n for multiple lines). Omit to keep the existing comment. |
| `length` | integer | Number of bytes covered by the label (default 1, max 65536). |

#### `set_comment`

Sets (or clears, with an empty string) the comment at an address, keeping any label there. Comments are shown in the disassembly. A comment line containing assert(condition) creates an assert breakpoint, as in the debugger. The address can be given in a CPU address space (e.g. SnesMemory $80:8000): it's converted to the underlying physical location (e.g. SnesPrgRom offset $0000 or SnesWorkRam offset) so the label applies wherever that memory is mapped. 

| Argument | Type | Description |
|---|---|---|
| `address` *(required)* | address | Address of the comment. JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `memory_type` | string | Memory type of the address (default: the main CPU's address space). |
| `comment` *(required)* | string | Comment text (use \n for multiple lines, empty string to remove). |

#### `delete_label`

Deletes the label (and its comment) by name, or the label at an address.

| Argument | Type | Description |
|---|---|---|
| `label` | string | Name of the label to delete. |
| `address` | address | Address of the label to delete (alternative to 'label'). JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `memory_type` | string | Memory type of the address (default: the main CPU's address space). |

#### `import_labels`

Imports labels/symbols from a file on disk, using the same importers as the debugger's File > Import menu: .mlb (Mesen labels), .dbg (ca65), .sym (WLA-DX, RGBDS, bass, PCEAS), .cdb (SDCC), .elf, .fns (NESASM). By default this follows the user's 'Reset labels on import' setting (enabled by default: existing labels are replaced). For .mlb files, merge=true keeps the existing labels and adds/overwrites the imported ones.

| Argument | Type | Description |
|---|---|---|
| `path` *(required)* | string | Absolute path of the file to import. |
| `merge` | boolean | Only for .mlb files: keep existing labels (default false: follow the user's setting). |

#### `export_labels`

Exports all labels and comments to a Mesen label file (.mlb), the same format as the debugger's File > Export labels. Format: one label per line, MemoryType:Address[-EndAddress]:Label[:Comment] (hex addresses). Refuses to overwrite an existing file unless overwrite=true.

| Argument | Type | Description |
|---|---|---|
| `path` *(required)* | string | Absolute path of the .mlb file to write. |
| `overwrite` | boolean | Overwrite the file if it already exists (default false). |

#### `list_breakpoints`

Lists the debugger's breakpoints (the same list as the debugger's Breakpoints panel): id, cpu, memory type, start/end address, type (exec/read/write), condition, enabled, and the label at the address. Asserts created from assert() comments are included with assert=true.

#### `add_breakpoint`

Adds a breakpoint, exactly like the debugger's breakpoint editor; it is saved in Mesen's workspace and shown in the debugger. Execution stops when the CPU executes (exec), reads (read) or writes (write) an address in the range and the optional condition is true. Addresses can be given in a CPU address space (e.g. SnesMemory $4218) or a physical memory type (e.g. SnesPrgRom/SnesWorkRam offset). By default, a CPU address that maps to physical memory or an I/O register is stored as that physical location (e.g. SnesRegister $4218, SnesWorkRam $0010), so the breakpoint matches every mirror (e.g. $00:4218 and $81:4218); the result shows the stored memory type and address. Use exact_cpu_address=true to only match that exact CPU address. Use run_until to run until it's hit. Condition syntax is Mesen's expression syntax, e.g. "a == $10", "x > 3 && [$7E0010] == 0", "value == $80" (value read/written), "address == $2118".

| Argument | Type | Description |
|---|---|---|
| `address` *(required)* | address | Start address. JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `end_address` | address | Optional end address (inclusive) for a range. JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `type` | string: `exec`, `read`, `write`, `rw`, `exec+read`, `exec+write`, `exec+read+write` | exec (default), read, write, rw (read+write), or combinations like exec+read. |
| `memory_type` | string | Memory type of the address (default: the CPU's address space, e.g. SnesMemory). |
| `cpu` | string | CPU the breakpoint applies to (default: main CPU; e.g. Spc for SPC700 breakpoints, or use memory_type SpcMemory). |
| `condition` | string | Optional condition. |
| `exact_cpu_address` | boolean | Only match accesses through this exact CPU address, not its mirrors (default false). |
| `enabled` | boolean | Enabled (default true). |
| `mark_event` | boolean | Only mark the event in the event viewer instead of breaking (default false). |

#### `remove_breakpoint`

Removes a breakpoint by id (from list_breakpoints/add_breakpoint), or all breakpoints with all=true.

| Argument | Type | Description |
|---|---|---|
| `id` | integer | Breakpoint id. |
| `all` | boolean | Remove all breakpoints (default false). |

#### `write_memory` (write access)

Writes bytes to a memory type (max 4096 bytes), e.g. to patch code in SnesPrgRom, change a variable in SnesWorkRam, or write through the CPU's address space. The write can be undone with the debugger's undo. Returns the previous bytes.

| Argument | Type | Description |
|---|---|---|
| `memory_type` *(required)* | string | Memory type to write, e.g. SnesWorkRam, SnesPrgRom, SnesMemory. |
| `address` *(required)* | address | Start address. JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `hex` *(required)* | string | Bytes to write, as hex separated by spaces (e.g. "EA EA") or contiguous ("EAEA"). |

#### `set_cpu_register` (write access)

Sets a CPU register while execution is paused. 65816 (cpu Snes/Sa1): a, x, y, sp, d, db, pb, pc (16-bit, in the current bank; or 24-bit to change the bank), p. SPC700 (Spc): a, x, y, sp, pc, psw. Other CPUs: pc only.

| Argument | Type | Description |
|---|---|---|
| `register` *(required)* | string | Register name. |
| `value` *(required)* | address | New value. JSON number (decimal), or string (always hex): "$8000", "0x8000", "8000" or bank:address "$80:8000". |
| `cpu` | string | CPU (default: main CPU). |

#### `reset` (write access)

Resets the console: reset (soft reset, like the reset button), power_cycle (hard reset: RAM is reinitialized), or reload (reloads the ROM file from disk). With pause=true (default), execution is paused on the first instruction after the reset so the startup code can be traced (pause=false: the game keeps running); the result then contains the CPU state at the reset vector.

| Argument | Type | Description |
|---|---|---|
| `type` | string: `reset`, `power_cycle`, `reload` | reset (default), power_cycle or reload. |
| `pause` | boolean | Pause on the first instruction after the reset (default true). |

#### `load_rom` (write access)

Loads a ROM file (SNES .sfc/.smc, NES .nes, GB/GBC, GBA, PCE, SMS/GG, WS, or a .zip/.7z archive, which loads its first ROM) and attaches the debugger. The debugger workspace (labels, breakpoints) for that ROM is loaded automatically, as are .mlb/.dbg/.sym/.cdl files next to the ROM if enabled in Mesen. With pause=true (default), execution is paused on the first instruction (pause=false: the game starts running).

| Argument | Type | Description |
|---|---|---|
| `path` *(required)* | string | Absolute path of the ROM file. |
| `patch_path` | string | Optional absolute path of an IPS/UPS/BPS patch to apply. |
| `pause` | boolean | Pause on the first instruction (default true). |

#### `save_save_state` (write access)

Saves the complete emulator state, either to a .mss file (absolute path) or to one of Mesen's save slots (1-10). Save states let you return to an interesting point (e.g. before a boss or a menu) repeatedly.

| Argument | Type | Description |
|---|---|---|
| `path` | string | Absolute path of the .mss file to write. |
| `slot` | integer | Save slot (1-10), alternative to path. |
| `overwrite` | boolean | Overwrite the file if it exists (default false). |

#### `load_save_state` (write access)

Loads a save state from a .mss file (absolute path) or from a save slot (1-10). The paused/running state is kept. Returns the CPU state after loading.

| Argument | Type | Description |
|---|---|---|
| `path` | string | Absolute path of the .mss file. |
| `slot` | integer | Save slot (1-10), alternative to path. |
