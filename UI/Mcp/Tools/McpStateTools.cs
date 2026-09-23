using Mesen.Config;
using Mesen.Debugger.ViewModels;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp.Tools
{
	public static class McpStateTools
	{
		public const int MaxReadLength = 4096;
		public const int MaxSearchResults = 1000;
		public const int MaxTraceRows = 500;

		private static HashSet<CpuType> _tracedCpus = new();

		public static void Register(McpToolRegistry registry)
		{
			registry.Add(new McpTool(
				"get_cpu_state",
				"Returns the registers of a CPU as hex strings. For the SNES 65816 (cpu Snes or Sa1): pc (24-bit, PB:PC), a, x, y, sp, d, db, pb, p, " +
				"decoded flags n v m x d i z c plus e (emulation mode), and accumulator_8bit/index_8bit (the effective M/X register widths). " +
				"For Spc: pc, a, x, y, sp, psw and flags. Other CPUs return all their state fields. " +
				"Also returns the PPU position (scanline/cycle/frame) of the console. Values are only fully consistent while execution is paused.",
				McpSchema.Create()
					.String("cpu", "CPU to read (Snes, Spc, Sa1, Gsu, Cx4, NecDsp, St018, Nes, Gameboy, Gba, Pce, Sms, Ws). Default: the console's main CPU.")
					.Build(),
				GetCpuState
			) { ReadOnly = true });

			registry.Add(new McpTool(
				"read_memory",
				$"Reads up to {MaxReadLength} bytes from a memory type. CPU address spaces (SnesMemory, SpcMemory, Sa1Memory, NesMemory, ...) are read " +
				"the way the CPU sees them (without side effects on I/O registers); physical regions (SnesPrgRom, SnesWorkRam, SnesSaveRam, SnesVideoRam, SnesCgRam, SnesSpriteRam, SpcRam, ...) use offsets within that memory. " +
				"Returns 'hex' (space-separated bytes). With format=rows, returns 16-byte rows with ASCII, like a hex editor.",
				McpSchema.Create()
					.String("memory_type", "Memory type to read, e.g. SnesMemory (65816 address space), SnesPrgRom, SnesWorkRam, SpcRam. See get_status for the list.", true)
					.Address("address", "Start address (offset within the memory type).", true)
					.Integer("length", $"Number of bytes to read (1-{MaxReadLength}, default 256).", false, 1, MaxReadLength)
					.String("format", "Output format: hex (default) or rows.", false, new[] { "hex", "rows" })
					.Build(),
				ReadMemory
			) { ReadOnly = true });

			registry.Add(new McpTool(
				"search_memory",
				$"Searches a memory type for a byte pattern or text and returns the matching addresses (max {MaxSearchResults}). " +
				"Pattern bytes are hex, space separated, with ?? as wildcard (e.g. \"A9 ?? 8D 00 21\"). Multi-byte values are little-endian in memory.",
				McpSchema.Create()
					.String("memory_type", "Memory type to search, e.g. SnesPrgRom, SnesWorkRam.", true)
					.String("pattern", "Hex byte pattern with optional ?? wildcards, e.g. \"20 ?? 80\".")
					.String("text", "ASCII text to search for (alternative to pattern).")
					.Address("start", "Start address of the search (default 0).")
					.Address("end", "End address of the search, inclusive (default: end of memory).")
					.Integer("max_results", $"Maximum number of results (default 100, max {MaxSearchResults}).", false, 1, MaxSearchResults)
					.Build(),
				SearchMemory
			) { ReadOnly = true });

			registry.Add(new McpTool(
				"evaluate",
				"Evaluates an expression with Mesen's debugger expression syntax (the same syntax as breakpoint conditions and watch expressions), " +
				"e.g. \"a\", \"x + y\", \"[$7E0010]\" (byte read), \"{$7E0010}\" (word read), \"pc\", \"scanline\", or a label name. " +
				"Useful to check a breakpoint condition before using it. Returns the value (decimal and hex) and its type.",
				McpSchema.Create()
					.String("expression", "Expression to evaluate.", true)
					.String("cpu", "CPU context for registers (default: main CPU).")
					.Build(),
				Evaluate
			) { ReadOnly = true });

			registry.Add(new McpTool(
				"get_call_stack",
				"Returns the call stack of a CPU (innermost frame first) as tracked by the debugger: for each frame, the function entry address, the call site, " +
				"the return address, labels when available, and whether the frame is an NMI/IRQ handler.",
				McpSchema.Create()
					.String("cpu", "CPU (default: main CPU).")
					.Build(),
				GetCallStack
			) { ReadOnly = true });

			registry.Add(new McpTool(
				"set_trace",
				"Enables or disables the execution trace logger for a CPU (it records every executed instruction in a 30000-row ring buffer, with labels). " +
				"Tracing slows emulation down a little. An optional condition (debugger expression) only logs matching instructions. " +
				"Note: the Trace Logger window uses the same settings and overrides them when it's opened or closed.",
				McpSchema.Create()
					.Boolean("enabled", "Enable (true) or disable (false) tracing.", true)
					.String("cpu", "CPU to trace (default: main CPU).")
					.String("condition", "Optional condition, e.g. \"pc >= $808000 && pc < $809000\".")
					.Boolean("clear", "Clear the trace buffer (default true when enabling).")
					.Build(),
				SetTrace
			) { ReadOnly = true });

			registry.Add(new McpTool(
				"get_trace_tail",
				$"Returns the most recently executed instructions from the trace logger in chronological order (max {MaxTraceRows} rows). " +
				"Each row has the CPU, PC, byte code and the formatted log text (disassembly with labels, effective address and registers). " +
				"Tracing must first be enabled with set_trace.",
				McpSchema.Create()
					.Integer("count", $"Number of rows (1-{MaxTraceRows}, default 50).", false, 1, MaxTraceRows)
					.Build(),
				GetTraceTail
			) { ReadOnly = true });
		}

		private static Task<JsonObject> GetCpuState(McpArgs args, CancellationToken ct)
		{
			CpuType cpu = McpHelpers.ResolveCpu(args);
			JsonObject state = McpHelpers.GetCpuState(cpu);
			JsonObject result = new JsonObject() {
				["cpu"] = cpu.ToString(),
				["paused"] = EmuApi.IsPaused()
			};
			foreach(KeyValuePair<string, JsonNode?> kvp in state.ToList()) {
				state.Remove(kvp.Key);
				result[kvp.Key] = kvp.Value;
			}

			uint pc = DebugApi.GetProgramCounter(cpu, true);
			McpHelpers.AddLabelInfo(result, cpu.ToMemoryType(), pc, "pc_label");

			TimingInfo timing = EmuApi.GetTimingInfo(cpu);
			result["frame_count"] = timing.FrameCount;
			JsonObject? ppu = GetPpuPosition(cpu);
			if(ppu != null) {
				result["ppu"] = ppu;
			}
			return Task.FromResult(result);
		}

		public static JsonObject? GetPpuPosition(CpuType cpu)
		{
			try {
				CpuType ppuCpu = cpu.GetConsoleType().GetMainCpuType();
				BaseState ppuState = DebugApi.GetPpuState(ppuCpu);
				JsonObject all = McpHelpers.BoxedStructToJson(ppuState);
				JsonObject result = new JsonObject();
				foreach(string key in new[] { "scanline", "cycle", "h_clock", "frame_count" }) {
					if(all.TryGetPropertyValue(key, out JsonNode? value) && value != null) {
						all.Remove(key);
						result[key] = value;
					}
				}
				return result.Count > 0 ? result : null;
			} catch(Exception) {
				return null;
			}
		}

		private static Task<JsonObject> ReadMemory(McpArgs args, CancellationToken ct)
		{
			MemoryType memType = McpHelpers.ResolveMemoryType(args, "memory_type", null);
			UInt32 address = args.GetRequiredAddress("address");
			int length = (int)args.GetInt("length", 256, 1, MaxReadLength);
			string format = args.GetString("format") ?? "hex";

			int size = DebugApi.GetMemorySize(memType);
			McpHelpers.ValidateRange(memType, address, (uint)length);
			bool truncated = false;
			if(address + length > size) {
				length = (int)(size - address);
				truncated = true;
			}

			byte[] data = DebugApi.GetMemoryValues(memType, address, (uint)(address + length - 1));
			JsonObject result = new JsonObject() {
				["memory_type"] = memType.ToString(),
				["address"] = McpHelpers.FormatAddress(memType, address),
				["length"] = data.Length
			};

			if(format == "rows") {
				JsonArray rows = new JsonArray();
				for(int i = 0; i < data.Length; i += 16) {
					int count = Math.Min(16, data.Length - i);
					string ascii = new string(data.Skip(i).Take(count).Select(b => b >= 0x20 && b < 0x7F ? (char)b : '.').ToArray());
					rows.Add((JsonNode)(McpHelpers.FormatAddress(memType, address + i) + ": " + McpHelpers.HexBytes(data, i, count).PadRight(47) + "  " + ascii));
				}
				result["rows"] = rows;
			} else {
				result["hex"] = McpHelpers.HexBytes(data);
			}

			if(truncated) {
				result["truncated"] = true;
				result["note"] = $"Read stopped at the end of {memType} (size {McpHelpers.FormatAddress(memType, size)}).";
			}
			return Task.FromResult(result);
		}

		private static Task<JsonObject> SearchMemory(McpArgs args, CancellationToken ct)
		{
			MemoryType memType = McpHelpers.ResolveMemoryType(args, "memory_type", null);
			string? patternStr = args.GetString("pattern");
			string? text = args.GetString("text");
			int maxResults = (int)args.GetInt("max_results", 100, 1, MaxSearchResults);

			int?[] pattern;
			if(patternStr != null) {
				string[] parts = patternStr.Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
				if(parts.Length == 1 && parts[0].Length > 2 && parts[0].Length % 2 == 0) {
					//Accept "A9008D" style patterns
					parts = Enumerable.Range(0, parts[0].Length / 2).Select(i => parts[0].Substring(i * 2, 2)).ToArray();
				}
				pattern = new int?[parts.Length];
				for(int i = 0; i < parts.Length; i++) {
					string part = parts[i].TrimStart('$');
					if(part == "??" || part == "?") {
						pattern[i] = null;
					} else if(byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)) {
						pattern[i] = b;
					} else {
						throw new McpToolException($"Invalid pattern byte '{parts[i]}'. Use hex bytes separated by spaces, with ?? as wildcard.");
					}
				}
			} else if(text != null) {
				pattern = Encoding.ASCII.GetBytes(text).Select(b => (int?)b).ToArray();
			} else {
				throw new McpToolException("Either 'pattern' or 'text' is required.");
			}

			if(pattern.Length == 0 || pattern.All(p => p == null)) {
				throw new McpToolException("The pattern must contain at least one non-wildcard byte.");
			}

			int size = DebugApi.GetMemorySize(memType);
			UInt32 start = args.GetAddress("start") ?? 0;
			UInt32 end = Math.Min(args.GetAddress("end") ?? (UInt32)(size - 1), (UInt32)(size - 1));
			if(start > end) {
				throw new McpToolException("'start' must be lower than or equal to 'end'.");
			}

			byte[] data = DebugApi.GetMemoryValues(memType, start, end);
			JsonArray matches = new JsonArray();
			int total = 0;
			for(int i = 0; i + pattern.Length <= data.Length; i++) {
				bool match = true;
				for(int j = 0; j < pattern.Length; j++) {
					if(pattern[j] != null && data[i + j] != pattern[j]) {
						match = false;
						break;
					}
				}
				if(match) {
					total++;
					if(matches.Count < maxResults) {
						JsonObject entry = new JsonObject() { ["address"] = McpHelpers.FormatAddress(memType, start + i) };
						McpHelpers.AddLabelInfo(entry, memType, start + i);
						matches.Add((JsonNode)entry);
					}
				}
			}

			JsonObject result = new JsonObject() {
				["memory_type"] = memType.ToString(),
				["match_count"] = total,
				["matches"] = matches
			};
			if(total > matches.Count) {
				result["truncated"] = true;
				result["note"] = $"Only the first {matches.Count} of {total} matches are listed. Narrow the range with start/end.";
			}
			return Task.FromResult(result);
		}

		private static Task<JsonObject> Evaluate(McpArgs args, CancellationToken ct)
		{
			string expression = args.GetRequiredString("expression");
			CpuType cpu = McpHelpers.ResolveCpu(args);
			Int64 value = DebugApi.EvaluateExpression(expression.Replace(Environment.NewLine, " "), cpu, out EvalResultType resultType, false);

			return Task.FromResult(resultType switch {
				EvalResultType.Invalid => throw new McpToolException($"Invalid expression: {expression}"),
				EvalResultType.DivideBy0 => throw new McpToolException("Division by zero."),
				EvalResultType.OutOfScope => throw new McpToolException("The expression references a label or value that is out of scope."),
				EvalResultType.Boolean => new JsonObject() { ["type"] = "boolean", ["value"] = value != 0 },
				_ => new JsonObject() { ["type"] = "numeric", ["value"] = value, ["hex"] = "$" + value.ToString("X") }
			});
		}

		private static Task<JsonObject> GetCallStack(McpArgs args, CancellationToken ct)
		{
			CpuType cpu = McpHelpers.ResolveCpu(args);
			MemoryType cpuMemType = cpu.ToMemoryType();
			StackFrameInfo[] frames = DebugApi.GetCallstack(cpu);

			JsonArray result = new JsonArray();
			//Innermost frame first
			for(int i = frames.Length - 1; i >= 0; i--) {
				StackFrameInfo frame = frames[i];
				JsonObject entry = new JsonObject() {
					["function"] = McpHelpers.FormatCpuAddress(cpu, frame.Target),
					["called_from"] = McpHelpers.FormatCpuAddress(cpu, frame.Source),
					["return_address"] = McpHelpers.FormatCpuAddress(cpu, frame.Return)
				};
				McpHelpers.AddLabelInfo(entry, cpuMemType, frame.Target, "function_label");
				McpHelpers.AddLabelInfo(entry, cpuMemType, frame.Source, "called_from_label");
				if(frame.AbsTarget.Address >= 0) {
					entry["function_absolute"] = McpHelpers.AddressInfoToJson(frame.AbsTarget);
				}
				if(frame.Flags != StackFrameFlags.None) {
					entry["type"] = frame.Flags.ToString();
				}
				result.Add((JsonNode)entry);
			}

			return Task.FromResult(new JsonObject() {
				["cpu"] = cpu.ToString(),
				["current_pc"] = McpHelpers.FormatCpuAddress(cpu, DebugApi.GetProgramCounter(cpu, true)),
				["frames"] = result
			});
		}

		private static Task<JsonObject> SetTrace(McpArgs args, CancellationToken ct)
		{
			CpuType cpu = McpHelpers.ResolveCpu(args);
			bool enabled = args.GetBool("enabled", true);
			string condition = args.GetString("condition") ?? "";
			bool clear = args.GetBool("clear", enabled);

			if(condition.Length > 0) {
				DebugApi.EvaluateExpression(condition, cpu, out EvalResultType resultType, false);
				if(resultType == EvalResultType.Invalid) {
					throw new McpToolException($"Invalid condition: {condition}");
				}
			}

			TraceLoggerCpuConfig cfg = ConfigManager.Config.Debug.TraceLogger.GetCpuConfig(cpu);
			InteropTraceLoggerOptions options = new InteropTraceLoggerOptions() {
				Enabled = enabled,
				UseLabels = true,
				IndentCode = false,
				Format = Encoding.UTF8.GetBytes(TraceLoggerOptionTab.GetAutoFormat(cfg, cpu)),
				Condition = Encoding.UTF8.GetBytes(condition)
			};
			Array.Resize(ref options.Condition, 1000);
			Array.Resize(ref options.Format, 1000);
			DebugApi.SetTraceOptions(cpu, options);

			lock(_tracedCpus) {
				if(enabled) {
					_tracedCpus.Add(cpu);
				} else {
					_tracedCpus.Remove(cpu);
				}
			}

			if(clear) {
				DebugApi.ClearExecutionTrace();
			}

			return Task.FromResult(new JsonObject() {
				["cpu"] = cpu.ToString(),
				["enabled"] = enabled,
				["condition"] = condition,
				["cleared"] = clear
			});
		}

		private static Task<JsonObject> GetTraceTail(McpArgs args, CancellationToken ct)
		{
			int count = (int)args.GetInt("count", 50, 1, MaxTraceRows);
			TraceRow[] rows = DebugApi.GetExecutionTrace(0, (uint)count);

			JsonArray result = new JsonArray();
			//Rows are returned newest first, output them in chronological order
			for(int i = rows.Length - 1; i >= 0; i--) {
				result.Add((JsonNode)new JsonObject() {
					["cpu"] = rows[i].Type.ToString(),
					["pc"] = McpHelpers.FormatCpuAddress(rows[i].Type, rows[i].ProgramCounter),
					["bytes"] = rows[i].GetByteCodeStr(),
					["log"] = rows[i].GetOutput().Trim()
				});
			}

			JsonObject response = new JsonObject() {
				["row_count"] = result.Count,
				["rows"] = result
			};
			if(result.Count == 0) {
				lock(_tracedCpus) {
					response["note"] = _tracedCpus.Count == 0 ? "The trace is empty. Enable tracing with set_trace, then let the game run (resume, step or run_until)." : "The trace is empty. Let the game run (resume, step or run_until) to record instructions.";
				}
			}
			return Task.FromResult(response);
		}
	}
}
