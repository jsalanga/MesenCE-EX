using Mesen.Config.Shortcuts;
using Mesen.Interop;
using Mesen.Utilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp.Tools
{
	/// <summary>Tools that modify emulated state: only available when "Allow write access" is enabled</summary>
	public static class McpWriteTools
	{
		public const int MaxWriteLength = 4096;
		private const int LoadTimeoutMs = 15000;

		public static void Register(McpToolRegistry registry)
		{
			registry.Add(new McpTool(
				"write_memory",
				$"Writes bytes to a memory type (max {MaxWriteLength} bytes), e.g. to patch code in SnesPrgRom, change a variable in SnesWorkRam, or write through the CPU's address space. " +
				"The write can be undone with the debugger's undo. Returns the previous bytes.",
				McpSchema.Create()
					.String("memory_type", "Memory type to write, e.g. SnesWorkRam, SnesPrgRom, SnesMemory.", true)
					.Address("address", "Start address.", true)
					.String("hex", "Bytes to write, as hex separated by spaces (e.g. \"EA EA\") or contiguous (\"EAEA\").", true)
					.Build(),
				WriteMemory
			) { RequiresWriteAccess = true, Destructive = true });

			registry.Add(new McpTool(
				"set_cpu_register",
				"Sets a CPU register while execution is paused. 65816 (cpu Snes/Sa1): a, x, y, sp, d, db, pb, pc (16-bit, in the current bank; or 24-bit to change the bank), p. " +
				"SPC700 (Spc): a, x, y, sp, pc, psw. Other CPUs: pc only.",
				McpSchema.Create()
					.String("register", "Register name.", true)
					.Address("value", "New value.", true)
					.String("cpu", "CPU (default: main CPU).")
					.Build(),
				SetCpuRegister
			) { RequiresWriteAccess = true, Destructive = true });

			registry.Add(new McpTool(
				"reset",
				"Resets the console: reset (soft reset, like the reset button), power_cycle (hard reset: RAM is reinitialized), or reload (reloads the ROM file from disk). " +
				"With pause=true (default), execution is paused on the first instruction after the reset so the startup code can be traced (pause=false: the game keeps running); " +
				"the result then contains the CPU state at the reset vector.",
				McpSchema.Create()
					.String("type", "reset (default), power_cycle or reload.", false, new[] { "reset", "power_cycle", "reload" })
					.Boolean("pause", "Pause on the first instruction after the reset (default true).")
					.Build(),
				Reset
			) { RequiresWriteAccess = true, Destructive = true });

			registry.Add(new McpTool(
				"load_rom",
				"Loads a ROM file (SNES .sfc/.smc, NES .nes, GB/GBC, GBA, PCE, SMS/GG, WS, or a .zip/.7z archive, which loads its first ROM) and attaches the debugger. " +
				"The debugger workspace (labels, breakpoints) for that ROM is loaded automatically, as are .mlb/.dbg/.sym/.cdl files next to the ROM if enabled in Mesen. " +
				"With pause=true (default), execution is paused on the first instruction (pause=false: the game starts running).",
				McpSchema.Create()
					.String("path", "Absolute path of the ROM file.", true)
					.String("patch_path", "Optional absolute path of an IPS/UPS/BPS patch to apply.")
					.Boolean("pause", "Pause on the first instruction (default true).")
					.Build(),
				LoadRom
			) { RequiresWriteAccess = true, Destructive = true, RequiresRom = false });

			registry.Add(new McpTool(
				"save_save_state",
				"Saves the complete emulator state, either to a .mss file (absolute path) or to one of Mesen's save slots (1-10). " +
				"Save states let you return to an interesting point (e.g. before a boss or a menu) repeatedly.",
				McpSchema.Create()
					.String("path", "Absolute path of the .mss file to write.")
					.Integer("slot", "Save slot (1-10), alternative to path.", false, 1, 10)
					.Boolean("overwrite", "Overwrite the file if it exists (default false).")
					.Build(),
				SaveState
			) { RequiresWriteAccess = true });

			registry.Add(new McpTool(
				"load_save_state",
				"Loads a save state from a .mss file (absolute path) or from a save slot (1-10). The paused/running state is kept. Returns the CPU state after loading.",
				McpSchema.Create()
					.String("path", "Absolute path of the .mss file.")
					.Integer("slot", "Save slot (1-10), alternative to path.", false, 1, 10)
					.Build(),
				LoadState
			) { RequiresWriteAccess = true, Destructive = true });
		}

		private static Task<JsonObject> WriteMemory(McpArgs args, CancellationToken ct)
		{
			MemoryType memType = McpHelpers.ResolveMemoryType(args, "memory_type", null);
			UInt32 address = args.GetRequiredAddress("address");
			string hex = args.GetRequiredString("hex").Replace(" ", "").Replace(",", "").Replace("$", "");
			if(hex.Length == 0 || hex.Length % 2 != 0) {
				throw new McpToolException("'hex' must contain an even number of hex digits.");
			}

			byte[] data = new byte[hex.Length / 2];
			for(int i = 0; i < data.Length; i++) {
				if(!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out data[i])) {
					throw new McpToolException($"Invalid hex byte '{hex.Substring(i * 2, 2)}'.");
				}
			}
			if(data.Length > MaxWriteLength) {
				throw new McpToolException($"At most {MaxWriteLength} bytes can be written per call.");
			}

			int size = DebugApi.GetMemorySize(memType);
			if(address + data.Length > size) {
				throw new McpToolException($"The write goes past the end of {memType} (size {McpHelpers.FormatAddress(memType, size)}).");
			}

			byte[] previous = DebugApi.GetMemoryValues(memType, address, (UInt32)(address + data.Length - 1));
			DebugApi.SetMemoryValues(memType, address, data, data.Length);

			return Task.FromResult(new JsonObject() {
				["memory_type"] = memType.ToString(),
				["address"] = McpHelpers.FormatAddress(memType, address),
				["length"] = data.Length,
				["previous"] = McpHelpers.HexBytes(previous),
				["written"] = McpHelpers.HexBytes(data)
			});
		}

		private static Task<JsonObject> SetCpuRegister(McpArgs args, CancellationToken ct)
		{
			CpuType cpu = McpHelpers.ResolveCpu(args);
			string register = args.GetRequiredString("register").Trim().ToLowerInvariant();
			UInt32 value = args.GetRequiredAddress("value");

			if(!EmuApi.IsPaused()) {
				throw new McpToolException("Execution must be paused to change registers (use pause first).");
			}

			if(register == "pc" && (cpu != CpuType.Snes && cpu != CpuType.Sa1)) {
				if(!DebugApi.GetDebuggerFeatures(cpu).ChangeProgramCounter) {
					throw new McpToolException($"Changing the program counter isn't supported for the {cpu} CPU.");
				}
				DebugApi.SetProgramCounter(cpu, value);
			} else if(cpu == CpuType.Snes || cpu == CpuType.Sa1) {
				SnesCpuState state = DebugApi.GetCpuState<SnesCpuState>(cpu);
				switch(register) {
					case "a": state.A = CheckRange16(value); break;
					case "x": state.X = CheckRange16(value); break;
					case "y": state.Y = CheckRange16(value); break;
					case "sp": state.SP = CheckRange16(value); break;
					case "d": state.D = CheckRange16(value); break;
					case "db": case "dbr": state.DBR = CheckRange8(value); break;
					case "pb": case "k": state.K = CheckRange8(value); break;
					case "p": state.PS = (SnesCpuFlags)CheckRange8(value); break;
					case "pc":
						if(value > 0xFFFF) {
							state.K = (byte)(value >> 16);
						}
						state.PC = (UInt16)value;
						break;
					default: throw new McpToolException($"Unknown register '{register}'. Valid registers: a, x, y, sp, d, db, pb, pc, p.");
				}
				DebugApi.SetCpuState(state, cpu);
			} else if(cpu == CpuType.Spc) {
				SpcState state = DebugApi.GetCpuState<SpcState>(cpu);
				switch(register) {
					case "a": state.A = CheckRange8(value); break;
					case "x": state.X = CheckRange8(value); break;
					case "y": state.Y = CheckRange8(value); break;
					case "sp": state.SP = CheckRange8(value); break;
					case "psw": case "p": state.PS = (SpcFlags)CheckRange8(value); break;
					case "pc": state.PC = CheckRange16(value); break;
					default: throw new McpToolException($"Unknown register '{register}'. Valid registers: a, x, y, sp, pc, psw.");
				}
				DebugApi.SetCpuState(state, cpu);
			} else {
				throw new McpToolException($"Only 'pc' can be changed for the {cpu} CPU.");
			}

			JsonObject result = McpHelpers.GetCpuState(cpu);
			result["cpu"] = cpu.ToString();
			return Task.FromResult(result);
		}

		private static UInt16 CheckRange16(UInt32 value)
		{
			if(value > 0xFFFF) {
				throw new McpToolException("Value must be between $0000 and $FFFF.");
			}
			return (UInt16)value;
		}

		private static byte CheckRange8(UInt32 value)
		{
			if(value > 0xFF) {
				throw new McpToolException("Value must be between $00 and $FF.");
			}
			return (byte)value;
		}

		private static async Task<JsonObject> Reset(McpArgs args, CancellationToken ct)
		{
			string type = args.GetString("type") ?? "reset";
			bool pause = args.GetBool("pause", true);
			EmulatorShortcut shortcut = type switch {
				"reset" => EmulatorShortcut.ExecReset,
				"power_cycle" => EmulatorShortcut.ExecPowerCycle,
				"reload" => EmulatorShortcut.ExecReloadRom,
				_ => throw new McpToolException($"Invalid reset type '{type}'. Use reset, power_cycle or reload.")
			};

			await McpDebugSession.Instance.ExecutionLock.WaitAsync(ct);
			try {
				McpDebugSession session = McpDebugSession.Instance;
				Task<ConsoleNotificationType> done = type == "reset" ?
					session.ArmNotificationWaiter(ConsoleNotificationType.GameReset) :
					session.ArmNotificationWaiter(ConsoleNotificationType.GameLoaded, ConsoleNotificationType.GameLoadFailed);

				session.DisarmStopWaiter();
				session.PauseOnNextReset(pause);
				_ = Task.Run(() => EmuApi.ExecuteShortcut(new ExecuteShortcutParams() { Shortcut = shortcut }));

				ConsoleNotificationType? result = await WaitNotificationAsync(done, LoadTimeoutMs, ct);
				if(result == null) {
					session.PauseOnNextReset(false);
					throw new McpToolException("The reset did not complete in time.");
				} else if(result == ConsoleNotificationType.GameLoadFailed) {
					throw new McpToolException("Reloading the ROM failed.");
				}

				return await GetPostResetResult(type, pause);
			} finally {
				McpDebugSession.Instance.ExecutionLock.Release();
			}
		}

		private static async Task<JsonObject> LoadRom(McpArgs args, CancellationToken ct)
		{
			string path = args.GetRequiredString("path");
			string? patchPath = args.GetString("patch_path");
			bool pause = args.GetBool("pause", true);

			if(!Path.IsPathFullyQualified(path) || !File.Exists(path)) {
				throw new McpToolException($"File not found (an absolute path is required): {path}");
			}
			if(patchPath != null && (!Path.IsPathFullyQualified(patchPath) || !File.Exists(patchPath))) {
				throw new McpToolException($"Patch file not found (an absolute path is required): {patchPath}");
			}

			await McpDebugSession.Instance.ExecutionLock.WaitAsync(ct);
			try {
				McpDebugSession session = McpDebugSession.Instance;
				Task<ConsoleNotificationType> done = session.ArmNotificationWaiter(ConsoleNotificationType.GameLoaded, ConsoleNotificationType.GameLoadFailed);
				session.DisarmStopWaiter();
				session.PauseOnNextReset(pause);

				bool success = await Task.Run(() => EmuApi.LoadRom(path, patchPath));
				if(!success) {
					session.PauseOnNextReset(false);
					session.DisarmNotificationWaiter(done);
					throw new McpToolException($"Mesen could not load the file: {path}");
				}

				ConsoleNotificationType? result = await WaitNotificationAsync(done, LoadTimeoutMs, ct);
				if(result != ConsoleNotificationType.GameLoaded) {
					throw new McpToolException("The ROM could not be loaded.");
				}

				await session.EnsureReadyAsync();
				JsonObject response = await GetPostResetResult("load_rom", pause);
				RomInfo romInfo = EmuApi.GetRomInfo();
				response["rom"] = new JsonObject() {
					["name"] = romInfo.GetRomName(),
					["console"] = romInfo.ConsoleType.ToString(),
					["format"] = romInfo.Format.ToString()
				};
				return response;
			} finally {
				McpDebugSession.Instance.ExecutionLock.Release();
			}
		}

		private static async Task<JsonObject> GetPostResetResult(string reason, bool pause)
		{
			if(pause) {
				//Pause is requested from the reset/load notification, wait for it to take effect
				for(int i = 0; i < 100 && !EmuApi.IsPaused(); i++) {
					await Task.Delay(20);
				}
				return McpExecutionTools.BuildStopResult(reason, null, McpHelpers.GetMainCpu(), null, null);
			}
			//Mesen keeps the previous paused state after a reset/load, make sure the game runs
			if(EmuApi.IsPaused()) {
				DebugApi.ResumeExecution();
			}
			return new JsonObject() { ["stop_reason"] = reason, ["paused"] = EmuApi.IsPaused() };
		}

		private static string? GetStatePath(McpArgs args, out int? slot)
		{
			string? path = args.GetString("path");
			slot = (int?)args.GetInt("slot");
			if((path == null) == (slot == null)) {
				throw new McpToolException("Specify either 'path' or 'slot'.");
			}
			if(path != null) {
				if(!Path.IsPathFullyQualified(path)) {
					throw new McpToolException("'path' must be an absolute path.");
				}
				if(!path.EndsWith("." + FileDialogHelper.MesenSaveStateExt, StringComparison.OrdinalIgnoreCase)) {
					throw new McpToolException($"The file name must end with .{FileDialogHelper.MesenSaveStateExt}.");
				}
			} else if(slot < 1 || slot > 10) {
				throw new McpToolException("'slot' must be between 1 and 10.");
			}
			return path;
		}

		private static async Task<JsonObject> SaveState(McpArgs args, CancellationToken ct)
		{
			string? path = GetStatePath(args, out int? slot);
			bool overwrite = args.GetBool("overwrite", false);

			if(path != null) {
				string? folder = Path.GetDirectoryName(path);
				if(folder == null || !Directory.Exists(folder)) {
					throw new McpToolException($"The folder doesn't exist: {folder}");
				}
				if(File.Exists(path) && !overwrite) {
					throw new McpToolException($"The file already exists: {path}. Set overwrite=true to replace it.");
				}
				await Task.Run(() => EmuApi.SaveStateFile(path));
				if(!File.Exists(path)) {
					throw new McpToolException("The save state could not be written.");
				}
				return new JsonObject() { ["path"] = path, ["size"] = new FileInfo(path).Length };
			}

			await Task.Run(() => EmuApi.SaveState((uint)slot!.Value));
			return new JsonObject() { ["slot"] = slot };
		}

		private static async Task<JsonObject> LoadState(McpArgs args, CancellationToken ct)
		{
			string? path = GetStatePath(args, out int? slot);
			if(path != null && !File.Exists(path)) {
				throw new McpToolException($"File not found: {path}");
			}

			await McpDebugSession.Instance.ExecutionLock.WaitAsync(ct);
			try {
				Task<ConsoleNotificationType> done = McpDebugSession.Instance.ArmNotificationWaiter(ConsoleNotificationType.StateLoaded);
				if(path != null) {
					await Task.Run(() => EmuApi.LoadStateFile(path));
				} else {
					await Task.Run(() => EmuApi.LoadState((uint)slot!.Value));
				}

				if(await WaitNotificationAsync(done, 5000, ct) == null) {
					throw new McpToolException("The save state could not be loaded (invalid file, empty slot, or a state from another game).");
				}

				JsonObject result = McpExecutionTools.BuildStopResult("state_loaded", null, McpHelpers.GetMainCpu(), null, null);
				result.Remove("stop_reason");
				result["state_loaded"] = path ?? ("slot " + slot);
				return result;
			} finally {
				McpDebugSession.Instance.ExecutionLock.Release();
			}
		}

		private static async Task<ConsoleNotificationType?> WaitNotificationAsync(Task<ConsoleNotificationType> waiter, int timeoutMs, CancellationToken ct)
		{
			try {
				Task completed = await Task.WhenAny(waiter, Task.Delay(timeoutMs, ct));
				if(completed == waiter) {
					return waiter.Result;
				}
			} catch(OperationCanceledException) {
			}
			McpDebugSession.Instance.DisarmNotificationWaiter(waiter);
			return null;
		}
	}
}
