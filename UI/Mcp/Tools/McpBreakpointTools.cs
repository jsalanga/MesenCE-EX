using Mesen.Debugger;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;

namespace Mesen.Mcp.Tools
{
	public static class McpBreakpointTools
	{
		//Breakpoint IDs given to MCP clients: stable for the lifetime of the breakpoint objects
		//(the core's IDs are list indexes, which change when breakpoints are added/removed)
		private static ConditionalWeakTable<Breakpoint, StrongBox<int>> _ids = new();
		private static int _lastId = 0;

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
