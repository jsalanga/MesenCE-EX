using Mesen.Config;
using Mesen.Debugger;
using Mesen.Debugger.Labels;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp.Tools
{
	public static class McpAnalysisTools
	{
		public const int MaxDisassemblyRows = 200;
		public const int MaxCdlSummaryLength = 0x40000;
		public const int MaxCdlRawLength = 4096;
		public const int MaxCdlRuns = 1000;
		public const int MaxFunctions = 2000;
		public const int MaxReferences = 200;

		//SNES-specific CDL flags (not in the CdlFlags enum)
		private const byte SnesCdlGsu = 0x40;
		private const byte SnesCdlCx4 = 0x80;

		public static void Register(McpToolRegistry registry)
		{
			registry.Add(new McpTool(
				"disassemble",
				$"Disassembles code the way Mesen's debugger shows it (max {MaxDisassemblyRows} instructions). Each row has the CPU address, " +
				"the absolute location (e.g. SnesPrgRom offset), byte code, instruction text with labels substituted, effective address and value " +
				"(computed with the CPU's current register state, so only reliable at the current PC), label, comment, and kind: " +
				"code (executed/verified by the code/data logger), data (verified data), or unknown (not yet executed; disassembled speculatively, " +
				"for 65816 using the currently known M/X flags). Returns next_address to continue. Defaults to the current PC.",
				McpSchema.Create()
					.String("cpu", "CPU (default: main CPU).")
					.Address("address", "Start address in the CPU's address space (default: current PC). If memory_type is a physical memory type (e.g. SnesPrgRom), this is an offset in that memory and is converted to the CPU address where it's currently mapped.")
					.String("memory_type", "Optional memory type of 'address' (default: the CPU's address space).")
					.Integer("count", $"Number of instructions (1-{MaxDisassemblyRows}, default 30).", false, 1, MaxDisassemblyRows)
					.Integer("before", "Number of rows to include before the address (0-50, default 0), useful to see context before the PC.", false, 0, 50)
					.Boolean("speculative", "Also disassemble bytes the code/data logger hasn't identified yet (default true). When false, unexplored regions are collapsed like in the debugger's default view.")
					.Build(),
				Disassemble
			) { ReadOnly = true });

			registry.Add(new McpTool(
				"get_cdl",
				"Returns code/data logger (CDL) information for a range: which bytes were executed as code, read as data, or never accessed (unknown). " +
				"The summary is a list of contiguous runs {start, end, kind: code|data|code+data|unknown}; for SNES code runs it includes m8/x8 " +
				"(the 65816 M and X flags recorded when the code was executed, i.e. 8-bit accumulator/index) and the coprocessor (Gsu/Cx4) when applicable. " +
				"Also lists function entry points (subroutine targets) and jump targets in the range. " +
				$"Summary range max {MaxCdlSummaryLength} bytes; include_raw adds the raw flag bytes (max {MaxCdlRawLength} bytes; bits: 01=code 02=data 04=jump target 08=sub entry 10=X8 20=M8 40=GSU 80=Cx4 on SNES).",
				McpSchema.Create()
					.String("memory_type", "Memory type with CDL data (default: PRG ROM, e.g. SnesPrgRom). CPU address spaces are also accepted.")
					.Address("address", "Start address/offset (default 0).")
					.Integer("length", $"Number of bytes (1-{MaxCdlSummaryLength}, default 4096).", false, 1, MaxCdlSummaryLength)
					.Boolean("include_raw", "Include the raw CDL flag bytes as hex (default false).")
					.Build(),
				GetCdl
			) { ReadOnly = true });

			registry.Add(new McpTool(
				"get_cdl_stats",
				"Returns code/data logger coverage for each memory type that supports it (PRG ROM, and CHR ROM for NES): bytes and percentage " +
				"identified as code, data and still unknown, plus the number of functions and jump targets found. Use it to measure exploration progress.",
				McpSchema.Create().Build(),
				GetCdlStats
			) { ReadOnly = true });

			registry.Add(new McpTool(
				"list_functions",
				$"Lists function entry points found by the code/data logger (targets of JSR/JSL/calls, and interrupt handlers), with their CPU address " +
				$"(where currently mapped), absolute offset and label. Paged: max {MaxFunctions} per call.",
				McpSchema.Create()
					.String("memory_type", "PRG ROM memory type (default: the console's PRG ROM).")
					.Integer("offset", "Index of the first function to return (default 0).", false, 0)
					.Integer("limit", $"Maximum number of functions (1-{MaxFunctions}, default 500).", false, 1, MaxFunctions)
					.Boolean("unlabeled_only", "Only return functions that don't have a label yet (default false).")
					.Build(),
				ListFunctions
			) { ReadOnly = true });

			registry.Add(new McpTool(
				"find_references",
				"Finds code that references an address: searches the whole disassembly for instructions whose operand is the address (hex), whose label is used, " +
				"or whose effective address (computed with the current register state) is the address. Also returns the runtime access counters " +
				"(how many times the address was read/written/executed since power on) and its CDL flags. " +
				"Limitations: only code that the disassembler shows (known code, or speculative disassembly) is searched, and indirect/computed " +
				"accesses can be missed. Each result has a match type: effective_address (the instruction's effective address, computed with the current D/DB/bank state, is the target), " +
				"operand or label (the operand is the target's full address or label), operand_16bit (same 16-bit operand, bank not confirmed, only with search_16bit), " +
				$"operand_16bit_other_bank (same 16-bit operand, but the effective address is elsewhere). Mirrors are merged. Max {MaxReferences} results.",
				McpSchema.Create()
					.Address("address", "Target address.", true)
					.String("memory_type", "Memory type of the address (default: the CPU's address space). Physical types (SnesPrgRom, SnesWorkRam...) are converted to the CPU address.")
					.String("cpu", "CPU whose code is searched (default: main CPU).")
					.Boolean("search_16bit", "Also match 16-bit operands with the same low 16 bits (default true for 24-bit CPUs).")
					.Boolean("include_unexplored", "Also search code that hasn't been executed yet, disassembled speculatively (default true). Such results have kind=unknown and may be data misread as code.")
					.Build(),
				FindReferences
			) { ReadOnly = true });
		}

		private static object _disassemblyConfigLock = new();

		/// <summary>
		/// Runs an action with the core configured to disassemble bytes that the code/data logger hasn't identified yet
		/// (by default, the debugger collapses them into a single "unidentified" block), then restores the user's settings.
		/// </summary>
		private static T WithSpeculativeDisassembly<T>(bool enabled, Func<T> action)
		{
			if(!enabled) {
				return action();
			}

			lock(_disassemblyConfigLock) {
				InteropDebugConfig cfg = ConfigManager.Config.Debug.ToInterop();
				if(cfg.DisassembleUnidentifiedData) {
					return action();
				}

				cfg.DisassembleUnidentifiedData = true;
				cfg.ShowUnidentifiedData = false;
				ConfigApi.SetDebugConfig(cfg);
				try {
					return action();
				} finally {
					ConfigApi.SetDebugConfig(ConfigManager.Config.Debug.ToInterop());
				}
			}
		}

		private static CpuType ResolveCpuAddress(McpArgs args, CpuType cpu, UInt32 address, out UInt32 cpuAddress, out AddressInfo? absAddress)
		{
			MemoryType cpuMemType = cpu.ToMemoryType();
			MemoryType memType = McpHelpers.ResolveMemoryType(args, "memory_type", cpuMemType);

			if(memType.IsRelativeMemory()) {
				if(memType != cpuMemType) {
					cpu = memType.ToCpuType();
				}
				cpuAddress = address;
				AddressInfo abs = DebugApi.GetAbsoluteAddress(new AddressInfo() { Address = (int)address, Type = memType });
				absAddress = abs.Address >= 0 ? abs : null;
			} else {
				AddressInfo abs = new AddressInfo() { Address = (int)address, Type = memType };
				AddressInfo rel = DebugApi.GetRelativeAddress(abs, cpu);
				if(rel.Address < 0) {
					throw new McpToolException($"{memType} offset {McpHelpers.FormatAddress(memType, address)} is not currently mapped in the {cpu} CPU's address space.");
				}
				cpuAddress = (UInt32)rel.Address;
				absAddress = abs;
			}
			return cpu;
		}

		private static Task<JsonObject> Disassemble(McpArgs args, CancellationToken ct)
		{
			CpuType cpu = McpHelpers.ResolveCpu(args);
			UInt32 pc = DebugApi.GetProgramCounter(cpu, true);
			UInt32 address = pc;
			if(args.Has("address")) {
				cpu = ResolveCpuAddress(args, cpu, args.GetRequiredAddress("address"), out address, out _);
				pc = DebugApi.GetProgramCounter(cpu, true);
			}

			int count = (int)args.GetInt("count", 30, 1, MaxDisassemblyRows);
			int before = (int)args.GetInt("before", 0, 0, 50);
			bool speculative = args.GetBool("speculative", true);

			CodeLineData[] lines = WithSpeculativeDisassembly(speculative, () => {
				UInt32 start = address;
				if(before > 0) {
					int startRow = DebugApi.GetDisassemblyRowAddress(cpu, address, -before);
					if(startRow >= 0) {
						start = (UInt32)startRow;
					}
				}
				return DebugApi.GetDisassemblyOutput(cpu, start, (UInt32)Math.Min(1000, (count + before) * 3 + 10));
			});
			string addrFormat = "X" + cpu.GetAddressSize();

			JsonArray rows = new JsonArray();
			string? pendingLabel = null;
			List<string> pendingComments = new();
			long nextAddress = -1;

			foreach(CodeLineData line in lines) {
				if(rows.Count >= count + before) {
					break;
				}

				if(line.Flags.HasFlag(LineFlags.Empty)) {
					continue;
				} else if(line.Flags.HasFlag(LineFlags.Label)) {
					pendingLabel = line.Text.TrimEnd(':').Trim();
					continue;
				} else if(line.Flags.HasFlag(LineFlags.Comment) && line.Text.Length == 0) {
					pendingComments.Add(line.Comment.Trim());
					continue;
				} else if(line.Address < 0) {
					continue;
				}

				JsonObject row = new JsonObject() {
					["address"] = McpHelpers.FormatCpuAddress(cpu, line.Address)
				};
				if(line.AbsoluteAddress.Address >= 0) {
					row["absolute"] = line.AbsoluteAddress.Type + ":" + McpHelpers.FormatAddress(line.AbsoluteAddress.Type, line.AbsoluteAddress.Address);
				}
				if(line.OpSize > 0) {
					row["bytes"] = McpHelpers.HexBytes(line.ByteCode, 0, Math.Min((int)line.OpSize, line.ByteCode.Length));
				}
				row["text"] = line.Text.Trim();

				string ea = line.GetEffectiveAddressString(addrFormat, out _);
				if(ea.Length > 0) {
					row["effective_address"] = ea + line.GetValueString();
				}

				if(pendingLabel != null) {
					row["label"] = pendingLabel;
					pendingLabel = null;
				}

				string comment = string.Join("\n", pendingComments.Append(line.Comment.Trim()).Where(c => c.Length > 0));
				pendingComments.Clear();
				if(comment.Length > 0) {
					row["comment"] = comment;
				}

				row["kind"] = GetLineKind(line.Flags);
				if(line.Flags.HasFlag(LineFlags.SubStart)) {
					row["function_start"] = true;
				}
				if(line.Address == pc) {
					row["is_pc"] = true;
				}

				rows.Add((JsonNode)row);
				nextAddress = line.Address + Math.Max(1, (int)line.OpSize);
			}

			JsonObject result = new JsonObject() {
				["cpu"] = cpu.ToString(),
				["pc"] = McpHelpers.FormatCpuAddress(cpu, pc),
				["rows"] = rows
			};
			if(nextAddress >= 0) {
				result["next_address"] = McpHelpers.FormatCpuAddress(cpu, nextAddress);
			}
			return Task.FromResult(result);
		}

		private static string GetLineKind(LineFlags flags)
		{
			if(flags.HasFlag(LineFlags.VerifiedCode)) {
				return "code";
			} else if(flags.HasFlag(LineFlags.VerifiedData)) {
				return "data";
			} else if(flags.HasFlag(LineFlags.UnmappedMemory)) {
				return "unmapped";
			}
			return "unknown";
		}

		private static MemoryType GetDefaultPrgType()
		{
			return McpHelpers.GetMainCpu().GetPrgRomMemoryType();
		}

		private static Task<JsonObject> GetCdl(McpArgs args, CancellationToken ct)
		{
			MemoryType memType = McpHelpers.ResolveMemoryType(args, "memory_type", GetDefaultPrgType());
			if(!memType.SupportsCdl()) {
				throw new McpToolException($"{memType} has no code/data logger data. Use the PRG ROM memory type (e.g. SnesPrgRom) or a CPU address space.");
			}

			UInt32 address = args.GetAddress("address") ?? 0;
			int length = (int)args.GetInt("length", 4096, 1, MaxCdlSummaryLength);
			bool includeRaw = args.GetBool("include_raw", false);
			bool isSnes = memType.ToCpuType().GetConsoleType() == ConsoleType.Snes;

			int size = DebugApi.GetMemorySize(memType);
			McpHelpers.ValidateRange(memType, address, (UInt32)length);
			bool truncatedRange = false;
			if(address + length > size) {
				length = (int)(size - address);
				truncatedRange = true;
			}

			byte[] flags = DebugApi.GetCdlData(address, (UInt32)length, memType).Select(f => (byte)f).ToArray();

			JsonArray runs = new JsonArray();
			JsonArray entryPoints = new JsonArray();
			JsonArray jumpTargets = new JsonArray();
			int codeBytes = 0, dataBytes = 0, unknownBytes = 0;
			bool runsTruncated = false;

			int runStart = 0;
			string? runKey = null;
			for(int i = 0; i <= flags.Length; i++) {
				string? key = i < flags.Length ? GetCdlRunKey(flags[i], isSnes) : null;
				if(i < flags.Length) {
					byte f = flags[i];
					if((f & (byte)CdlFlags.Code) != 0) {
						codeBytes++;
					} else if((f & (byte)CdlFlags.Data) != 0) {
						dataBytes++;
					} else {
						unknownBytes++;
					}

					if((f & (byte)CdlFlags.Code) != 0) {
						if((f & (byte)CdlFlags.SubEntryPoint) != 0 && entryPoints.Count < 500) {
							entryPoints.Add((JsonNode)McpHelpers.FormatAddress(memType, address + i));
						} else if((f & (byte)CdlFlags.JumpTarget) != 0 && jumpTargets.Count < 500) {
							jumpTargets.Add((JsonNode)McpHelpers.FormatAddress(memType, address + i));
						}
					}
				}

				if(key != runKey) {
					if(runKey != null) {
						if(runs.Count < MaxCdlRuns) {
							runs.Add((JsonNode)CreateRun(memType, address, runStart, i - 1, flags[runStart], isSnes));
						} else {
							runsTruncated = true;
						}
					}
					runStart = i;
					runKey = key;
				}
			}

			JsonObject result = new JsonObject() {
				["memory_type"] = memType.ToString(),
				["address"] = McpHelpers.FormatAddress(memType, address),
				["length"] = length,
				["code_bytes"] = codeBytes,
				["data_bytes"] = dataBytes,
				["unknown_bytes"] = unknownBytes,
				["runs"] = runs,
				["function_entry_points"] = entryPoints,
				["jump_targets"] = jumpTargets
			};

			if(includeRaw) {
				int rawLength = Math.Min(flags.Length, MaxCdlRawLength);
				result["raw"] = McpHelpers.HexBytes(flags, 0, rawLength);
				if(rawLength < flags.Length) {
					result["raw_truncated"] = true;
				}
			}

			List<string> notes = new();
			if(runsTruncated) {
				result["truncated"] = true;
				notes.Add($"Only the first {MaxCdlRuns} runs are listed, request a smaller range to see the rest.");
			}
			if(truncatedRange) {
				notes.Add($"Range stopped at the end of {memType}.");
			}
			if(notes.Count > 0) {
				result["note"] = string.Join(" ", notes);
			}
			return Task.FromResult(result);
		}

		private static string GetCdlRunKey(byte f, bool isSnes)
		{
			bool code = (f & (byte)CdlFlags.Code) != 0;
			bool data = (f & (byte)CdlFlags.Data) != 0;
			if(code) {
				//For SNES code, split runs when the M/X flags or the coprocessor changes
				return (data ? "cd" : "c") + (isSnes ? (f & 0xF0).ToString("X2") : "");
			}
			return data ? "d" : "u";
		}

		private static JsonObject CreateRun(MemoryType memType, UInt32 baseAddress, int start, int end, byte f, bool isSnes)
		{
			bool code = (f & (byte)CdlFlags.Code) != 0;
			bool data = (f & (byte)CdlFlags.Data) != 0;
			JsonObject run = new JsonObject() {
				["start"] = McpHelpers.FormatAddress(memType, baseAddress + start),
				["end"] = McpHelpers.FormatAddress(memType, baseAddress + end),
				["kind"] = code ? (data ? "code+data" : "code") : (data ? "data" : "unknown")
			};

			if(code && isSnes) {
				if((f & SnesCdlGsu) != 0) {
					run["cpu"] = "Gsu";
				} else if((f & SnesCdlCx4) != 0) {
					run["cpu"] = "Cx4";
				} else {
					run["m8"] = (f & (byte)CdlFlags.MemoryMode8) != 0;
					run["x8"] = (f & (byte)CdlFlags.IndexMode8) != 0;
				}
			}
			return run;
		}

		private static Task<JsonObject> GetCdlStats(McpArgs args, CancellationToken ct)
		{
			JsonArray regions = new JsonArray();
			foreach(MemoryType memType in McpHelpers.GetAvailableMemoryTypes()) {
				if(!memType.SupportsCdl() || memType.IsRelativeMemory()) {
					continue;
				}

				CdlStatistics stats = DebugApi.GetCdlStatistics(memType);
				if(stats.TotalBytes == 0) {
					continue;
				}

				double total = stats.TotalBytes;
				JsonObject region = new JsonObject() {
					["memory_type"] = memType.ToString(),
					["total_bytes"] = stats.TotalBytes,
					["code_bytes"] = stats.CodeBytes,
					["data_bytes"] = stats.DataBytes,
					["unknown_bytes"] = stats.TotalBytes - stats.CodeBytes - stats.DataBytes,
					["code_percent"] = Math.Round(stats.CodeBytes * 100 / total, 2),
					["data_percent"] = Math.Round(stats.DataBytes * 100 / total, 2),
					["unknown_percent"] = Math.Round((total - stats.CodeBytes - stats.DataBytes) * 100 / total, 2),
					["functions"] = stats.FunctionCount,
					["jump_targets"] = stats.JumpTargetCount
				};
				if(stats.TotalChrBytes > 0) {
					region["chr_drawn_bytes"] = stats.DrawnChrBytes;
					region["chr_total_bytes"] = stats.TotalChrBytes;
					region["chr_drawn_percent"] = Math.Round(stats.DrawnChrBytes * 100.0 / stats.TotalChrBytes, 2);
				}
				regions.Add((JsonNode)region);
			}

			return Task.FromResult(new JsonObject() {
				["regions"] = regions,
				["note"] = "CDL data accumulates while the game runs with the debugger attached, and is saved automatically by Mesen."
			});
		}

		private static Task<JsonObject> ListFunctions(McpArgs args, CancellationToken ct)
		{
			MemoryType memType = McpHelpers.ResolveMemoryType(args, "memory_type", GetDefaultPrgType());
			if(!memType.SupportsCdl() || memType.IsRelativeMemory()) {
				throw new McpToolException($"{memType} has no function list. Use the PRG ROM memory type (e.g. SnesPrgRom).");
			}

			int offset = (int)args.GetInt("offset", 0, 0, int.MaxValue);
			int limit = (int)args.GetInt("limit", 500, 1, MaxFunctions);
			bool unlabeledOnly = args.GetBool("unlabeled_only", false);
			CpuType cpu = McpHelpers.GetMainCpu();

			List<UInt32> functions = DebugApi.GetCdlFunctions(memType).OrderBy(f => f).ToList();
			if(unlabeledOnly) {
				functions = functions.Where(f => string.IsNullOrEmpty(LabelManager.GetLabel(f, memType)?.Label)).ToList();
			}

			JsonArray result = new JsonArray();
			foreach(UInt32 func in functions.Skip(offset).Take(limit)) {
				JsonObject entry = new JsonObject() {
					["absolute"] = McpHelpers.FormatAddress(memType, func)
				};
				AddressInfo rel = DebugApi.GetRelativeAddress(new AddressInfo() { Address = (int)func, Type = memType }, cpu);
				if(rel.Address >= 0) {
					entry["cpu_address"] = McpHelpers.FormatCpuAddress(cpu, rel.Address);
				}
				CodeLabel? label = LabelManager.GetLabel(func, memType);
				if(label != null && label.Label.Length > 0) {
					entry["label"] = label.Label;
				}
				result.Add((JsonNode)entry);
			}

			JsonObject response = new JsonObject() {
				["memory_type"] = memType.ToString(),
				["total"] = functions.Count,
				["offset"] = offset,
				["functions"] = result
			};
			if(offset + result.Count < functions.Count) {
				response["truncated"] = true;
				response["next_offset"] = offset + result.Count;
			}
			return Task.FromResult(response);
		}

		/// <summary>Returns true/false if the instruction has an effective address that does/doesn't map to the target, null if unknown</summary>
		private static bool? EffectiveAddressMatches(CodeLineData line, AddressInfo targetAbs)
		{
			if(!line.ShowEffectiveAddress || line.EffectiveAddress < 0 || line.EffectiveAddress > Int32.MaxValue) {
				return null;
			}

			AddressInfo ea = new AddressInfo() { Address = (int)line.EffectiveAddress, Type = line.EffectiveAddressType };
			if(ea.Type.IsRelativeMemory() && !targetAbs.Type.IsRelativeMemory()) {
				ea = DebugApi.GetAbsoluteAddress(ea);
			}
			return ea.Address == targetAbs.Address && ea.Type == targetAbs.Type;
		}

		private static Task<JsonObject> FindReferences(McpArgs args, CancellationToken ct)
		{
			CpuType cpu = McpHelpers.ResolveCpu(args);
			cpu = ResolveCpuAddress(args, cpu, args.GetRequiredAddress("address"), out UInt32 cpuAddress, out AddressInfo? absAddress);
			MemoryType cpuMemType = cpu.ToMemoryType();
			int addrSize = cpu.GetAddressSize();
			bool search16 = args.GetBool("search_16bit", addrSize > 4);
			bool includeUnexplored = args.GetBool("include_unexplored", true);

			JsonObject target = new JsonObject() {
				["cpu"] = cpu.ToString(),
				["cpu_address"] = McpHelpers.FormatCpuAddress(cpu, cpuAddress)
			};
			if(absAddress != null) {
				target["absolute"] = McpHelpers.AddressInfoToJson(absAddress.Value);
			}

			//Candidate searches: label, full address, 16-bit operand, 8-bit (direct page/zero page) operand.
			//Short forms are ambiguous, so they are only kept when the instruction's effective address confirms the match.
			List<(string search, bool needsConfirmation)> searches = new();
			CodeLabel? label = McpHelpers.GetLabel(cpuMemType, cpuAddress);
			if(label != null && label.Label.Length > 0) {
				target["label"] = label.Label;
				searches.Add((label.Label, false));
			}
			searches.Add(("$" + cpuAddress.ToString("X" + addrSize), false));
			if(addrSize > 4) {
				searches.Add(("$" + (cpuAddress & 0xFFFF).ToString("X4"), !search16));
			}
			if((cpuAddress & 0xFF00) == 0 || (addrSize > 4 && (cpuAddress & 0xFFFF) < 0x100)) {
				searches.Add(("$" + (cpuAddress & 0xFF).ToString("X2"), true));
			}

			AddressInfo targetAbs = absAddress ?? new AddressInfo() { Address = (int)cpuAddress, Type = cpuMemType };
			DisassemblySearchOptions options = new DisassemblySearchOptions() { MatchCase = false, MatchWholeWord = true };
			Dictionary<(MemoryType, int), (int cpuAddr, JsonObject entry)> refs = new();
			bool truncated = false;
			foreach((string search, bool needsConfirmation) in searches) {
				CodeLineData[] lines = WithSpeculativeDisassembly(includeUnexplored, () => DebugApi.FindOccurrences(cpu, search, options));
				if(lines.Length >= 500) {
					truncated = true;
				}
				foreach(CodeLineData line in lines) {
					if(line.Address < 0 || line.OpSize == 0) {
						continue;
					}

					//Deduplicate mirrors (e.g. the same ROM code visible in several banks)
					(MemoryType, int) key = line.AbsoluteAddress.Address >= 0 ? (line.AbsoluteAddress.Type, line.AbsoluteAddress.Address) : (cpuMemType, line.Address);
					if(refs.ContainsKey(key)) {
						continue;
					}

					string match;
					bool? eaMatches = EffectiveAddressMatches(line, targetAbs);
					if(eaMatches == true) {
						match = "effective_address";
					} else if(needsConfirmation) {
						//Short operand that can't be confirmed to access the target
						continue;
					} else if(search == label?.Label) {
						match = "label";
					} else if(search.Length - 1 == addrSize) {
						match = "operand";
					} else {
						//16-bit operand: same offset, but the bank can't be confirmed
						match = eaMatches == false ? "operand_16bit_other_bank" : "operand_16bit";
					}

					if(refs.Count >= MaxReferences) {
						truncated = true;
						break;
					}

					JsonObject entry = new JsonObject() {
						["address"] = McpHelpers.FormatCpuAddress(cpu, line.Address),
						["text"] = line.Text.Trim(),
						["match"] = match,
						["kind"] = GetLineKind(line.Flags)
					};
					if(line.AbsoluteAddress.Address >= 0) {
						entry["absolute"] = line.AbsoluteAddress.Type + ":" + McpHelpers.FormatAddress(line.AbsoluteAddress.Type, line.AbsoluteAddress.Address);
					}
					refs[key] = (line.Address, entry);
				}
			}

			JsonObject result = new JsonObject() {
				["target"] = target,
				["references"] = McpHelpers.ToJsonArray(refs.Values.OrderBy(r => r.cpuAddr).Select(r => (JsonNode)r.entry))
			};

			AddressInfo countersAddr = absAddress ?? new AddressInfo() { Address = (int)cpuAddress, Type = cpuMemType };
			AddressCounters counters = DebugApi.GetMemoryAccessCounts((UInt32)countersAddr.Address, 1, countersAddr.Type)[0];
			result["runtime_access_counts"] = new JsonObject() {
				["reads"] = counters.ReadCounter,
				["writes"] = counters.WriteCounter,
				["executions"] = counters.ExecCounter
			};

			if(absAddress != null && absAddress.Value.Type.SupportsCdl()) {
				byte f = (byte)DebugApi.GetCdlData((UInt32)absAddress.Value.Address, 1, absAddress.Value.Type)[0];
				result["cdl"] = new JsonObject() {
					["code"] = (f & (byte)CdlFlags.Code) != 0,
					["data"] = (f & (byte)CdlFlags.Data) != 0,
					["jump_target"] = (f & (byte)CdlFlags.JumpTarget) != 0,
					["function_entry"] = (f & (byte)CdlFlags.SubEntryPoint) != 0
				};
			}

			if(truncated) {
				result["truncated"] = true;
				result["note"] = "The result limit was reached; some references may be missing.";
			}
			return Task.FromResult(result);
		}
	}
}
