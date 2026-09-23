using Avalonia.Threading;
using Mesen.Debugger;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp.Tools
{
	public static class McpBreakpointTools
	{
		//Breakpoint IDs given to MCP clients: stable for the lifetime of the breakpoint objects
		//(the core's IDs are list indexes, which change when breakpoints are added/removed)
		private static ConditionalWeakTable<Breakpoint, StrongBox<int>> _ids = new();
		private static int _lastId = 0;

		public static void Register(McpToolRegistry registry)
		{
			registry.Add(new McpTool(
				"list_breakpoints",
				"Lists the debugger's breakpoints (the same list as the debugger's Breakpoints panel): id, cpu, memory type, start/end address, " +
				"type (exec/read/write), condition, enabled, and the label at the address. Asserts created from assert() comments are included with assert=true.",
				McpSchema.Create().Build(),
				ListBreakpoints
			) { ReadOnly = true });

			registry.Add(new McpTool(
				"add_breakpoint",
				"Adds a breakpoint, exactly like the debugger's breakpoint editor; it is saved in Mesen's workspace and shown in the debugger. " +
				"Execution stops when the CPU executes (exec), reads (read) or writes (write) an address in the range and the optional condition is true. " +
				"Addresses can be in a CPU address space (e.g. SnesMemory, which matches all mirrors the CPU uses) or a physical memory type " +
				"(e.g. SnesPrgRom offset, SnesWorkRam offset: matches that exact memory wherever it's mapped). Use run_until to run until it's hit. " +
				"Condition syntax is Mesen's expression syntax, e.g. \"a == $10\", \"x > 3 && [$7E0010] == 0\", \"value == $80\" (value read/written), \"address == $2118\".",
				McpSchema.Create()
					.Address("address", "Start address.", true)
					.Address("end_address", "Optional end address (inclusive) for a range.")
					.String("type", "exec (default), read, write, rw (read+write), or combinations like exec+read.", false, new[] { "exec", "read", "write", "rw", "exec+read", "exec+write", "exec+read+write" })
					.String("memory_type", "Memory type of the address (default: the CPU's address space, e.g. SnesMemory).")
					.String("cpu", "CPU the breakpoint applies to (default: main CPU; e.g. Spc for SPC700 breakpoints, or use memory_type SpcMemory).")
					.String("condition", "Optional condition.")
					.Boolean("enabled", "Enabled (default true).")
					.Boolean("mark_event", "Only mark the event in the event viewer instead of breaking (default false).")
					.Build(),
				AddBreakpoint
			));

			registry.Add(new McpTool(
				"remove_breakpoint",
				"Removes a breakpoint by id (from list_breakpoints/add_breakpoint), or all breakpoints with all=true.",
				McpSchema.Create()
					.Integer("id", "Breakpoint id.")
					.Boolean("all", "Remove all breakpoints (default false).")
					.Build(),
				RemoveBreakpoint
			));
		}

		private static async Task<JsonObject> ListBreakpoints(McpArgs args, CancellationToken ct)
		{
			return await Dispatcher.UIThread.InvokeAsync(() => {
				JsonArray result = new JsonArray();
				foreach(Breakpoint bp in BreakpointManager.Breakpoints) {
					result.Add((JsonNode)ToJson(bp));
				}
				foreach(Breakpoint bp in BreakpointManager.Asserts) {
					result.Add((JsonNode)ToJson(bp));
				}
				return new JsonObject() { ["breakpoints"] = result };
			});
		}

		private static async Task<JsonObject> AddBreakpoint(McpArgs args, CancellationToken ct)
		{
			CpuType cpu = McpHelpers.ResolveCpu(args);
			Breakpoint bp = CreateBreakpoint(args, cpu, "address", "end_address", "type", "memory_type", "condition", "exec");
			bp.Enabled = args.GetBool("enabled", true);
			bp.MarkEvent = args.GetBool("mark_event", false);

			JsonObject result = await Dispatcher.UIThread.InvokeAsync(() => {
				BreakpointManager.AddBreakpoint(bp);
				return ToJson(bp);
			});
			McpDebugSession.Instance.ScheduleWorkspaceSave();
			return result;
		}

		private static async Task<JsonObject> RemoveBreakpoint(McpArgs args, CancellationToken ct)
		{
			long? id = args.GetInt("id");
			bool all = args.GetBool("all", false);
			if(id == null && !all) {
				throw new McpToolException("Either 'id' or all=true is required.");
			}

			JsonObject result = await Dispatcher.UIThread.InvokeAsync(() => {
				if(all) {
					int count = BreakpointManager.Breakpoints.Count;
					BreakpointManager.ClearBreakpoints();
					DebugWorkspaceManager.AutoSave();
					return new JsonObject() { ["removed_count"] = count };
				}

				Breakpoint? bp = BreakpointManager.Breakpoints.FirstOrDefault(b => GetId(b) == id);
				if(bp == null) {
					throw new McpToolException($"No breakpoint with id {id}. Use list_breakpoints to see the current ids.");
				}
				BreakpointManager.RemoveBreakpoint(bp);
				return new JsonObject() { ["removed"] = ToJson(bp) };
			});
			McpDebugSession.Instance.ScheduleWorkspaceSave();
			return result;
		}

		public static int GetId(Breakpoint bp)
		{
			return _ids.GetValue(bp, _ => new StrongBox<int>(Interlocked.Increment(ref _lastId))).Value;
		}

		public static Breakpoint CreateBreakpoint(McpArgs args, CpuType cpu, string addressArg, string endAddressArg, string typeArg, string memTypeArg, string conditionArg, string defaultType)
		{
			MemoryType memType = McpHelpers.ResolveMemoryType(args, memTypeArg, cpu.ToMemoryType());
			if(memType.IsRelativeMemory() && memType != cpu.ToMemoryType()) {
				cpu = memType.ToCpuType();
				if(!EmuApi.GetRomInfo().CpuTypes.Contains(cpu)) {
					throw new McpToolException($"{memType} belongs to the {cpu} CPU, which isn't present in the loaded ROM.");
				}
			} else if(!memType.IsRelativeMemory() && !cpu.CanAccessMemoryType(memType)) {
				throw new McpToolException($"The {cpu} CPU can't access {memType}. Specify the matching cpu.");
			}

			if(!memType.SupportsBreakpoints()) {
				throw new McpToolException($"Breakpoints aren't supported on {memType}.");
			}

			UInt32 start = args.GetRequiredAddress(addressArg);
			UInt32 end = args.GetAddress(endAddressArg) ?? start;
			int size = DebugApi.GetMemorySize(memType);
			if(end < start) {
				throw new McpToolException($"'{endAddressArg}' must be greater than or equal to '{addressArg}'.");
			} else if(end >= size) {
				throw new McpToolException($"Address {McpHelpers.FormatAddress(memType, end)} is out of range for {memType} (size: {McpHelpers.FormatAddress(memType, size)} bytes).");
			}

			string type = args.GetString(typeArg) ?? defaultType;
			bool exec = type.Contains("exec");
			bool read = type.Contains("read") || type == "rw";
			bool write = type.Contains("write") || type == "rw";
			if(!exec && !read && !write) {
				throw new McpToolException($"Invalid breakpoint type '{type}'. Use exec, read, write or rw.");
			}
			if(exec && !memType.SupportsExecBreakpoints()) {
				throw new McpToolException($"Execution breakpoints aren't supported on {memType}. Use a CPU address space (e.g. {cpu.ToMemoryType()}) or the PRG ROM memory type.");
			}

			string condition = (args.GetString(conditionArg) ?? "").Trim();
			if(condition.Length > 0) {
				DebugApi.EvaluateExpression(condition, cpu, out EvalResultType resultType, false);
				if(resultType == EvalResultType.Invalid) {
					throw new McpToolException($"Invalid breakpoint condition: {condition}. Use Mesen's expression syntax, e.g. \"a == $10\", \"[$7E0010] > 3\", \"x & $80\".");
				}
			}

			return new Breakpoint() {
				CpuType = cpu,
				MemoryType = memType,
				StartAddress = start,
				EndAddress = end,
				BreakOnExec = exec,
				BreakOnRead = read,
				BreakOnWrite = write,
				Condition = condition,
				Enabled = true
			};
		}

		public static string GetTypeString(Breakpoint bp)
		{
			if(bp.Forbid) {
				return "forbid";
			}
			List<string> types = new();
			if(bp.BreakOnExec) {
				types.Add("exec");
			}
			if(bp.BreakOnRead) {
				types.Add("read");
			}
			if(bp.BreakOnWrite) {
				types.Add("write");
			}
			return string.Join("+", types);
		}

		public static string Describe(Breakpoint bp)
		{
			string range = McpHelpers.FormatAddress(bp.MemoryType, bp.StartAddress) + (bp.StartAddress != bp.EndAddress ? "-" + McpHelpers.FormatAddress(bp.MemoryType, bp.EndAddress) : "");
			return $"{GetTypeString(bp)} {bp.MemoryType} {range}" + (bp.Condition.Length > 0 ? " if " + bp.Condition : "");
		}

		public static JsonObject ToJson(Breakpoint bp)
		{
			JsonObject result = new JsonObject() {
				["id"] = GetId(bp),
				["cpu"] = bp.CpuType.ToString(),
				["memory_type"] = bp.MemoryType.ToString(),
				["start"] = McpHelpers.FormatAddress(bp.MemoryType, bp.StartAddress)
			};
			if(bp.EndAddress != bp.StartAddress) {
				result["end"] = McpHelpers.FormatAddress(bp.MemoryType, bp.EndAddress);
			}
			result["type"] = GetTypeString(bp);
			if(bp.Condition.Length > 0) {
				result["condition"] = bp.Condition;
			}
			result["enabled"] = bp.Enabled;
			if(bp.MarkEvent) {
				result["mark_event"] = true;
			}
			if(bp.IsAssert) {
				result["assert"] = true;
			}
			McpHelpers.AddLabelInfo(result, bp.MemoryType, bp.StartAddress);
			return result;
		}
	}
}
