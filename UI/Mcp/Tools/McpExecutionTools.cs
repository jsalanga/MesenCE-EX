using Avalonia.Threading;
using Mesen.Debugger;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp.Tools
{
	public static class McpExecutionTools
	{
		public const int MaxTimeoutMs = 60000;
		public const int MaxTimeoutFrames = 36000;
		public const int MaxInputFrames = 3600;
		private const int PauseTimeoutMs = 2000;

		private static readonly Dictionary<string, StepType> StepTypes = new() {
			["instruction"] = StepType.Step,
			["over"] = StepType.StepOver,
			["out"] = StepType.StepOut,
			["cycle"] = StepType.CpuCycleStep,
			["ppu_cycle"] = StepType.PpuStep,
			["scanline"] = StepType.PpuScanline,
			["frame"] = StepType.PpuFrame,
			["nmi"] = StepType.RunToNmi,
			["irq"] = StepType.RunToIrq,
			["back_instruction"] = StepType.StepBack,
			["back_scanline"] = StepType.StepBack,
			["back_frame"] = StepType.StepBack
		};

		private static readonly string[] ButtonNames = { "a", "b", "x", "y", "l", "r", "up", "down", "left", "right", "select", "start", "u", "d" };

		public static void Register(McpToolRegistry registry)
		{
			registry.Add(new McpTool(
				"pause",
				"Pauses execution (breaks into the debugger at an instruction boundary) and returns where execution stopped: " +
				"stop reason, CPU state and the current instruction. Does nothing if already paused.",
				McpSchema.Create().Build(),
				Pause
			));

			registry.Add(new McpTool(
				"resume",
				"Resumes execution. Returns immediately; use run_until instead to resume and wait for a breakpoint.",
				McpSchema.Create().Build(),
				Resume
			));

			registry.Add(new McpTool(
				"step",
				"Steps execution and waits until it stops again. Types: instruction (default), over (step over subroutine calls), out (run until the current " +
				"subroutine returns), cycle (CPU cycles), ppu_cycle, scanline, frame, nmi (run until the next NMI handler starts), irq (until the next IRQ), " +
				"back_instruction/back_scanline/back_frame (step backward using rewind data; count ignored). " +
				"A breakpoint hit during the step stops it early. Returns the stop reason, CPU state and current instruction.",
				McpSchema.Create()
					.String("type", "Step type (default instruction).", false, StepTypes.Keys)
					.Integer("count", "Number of instructions/cycles/scanlines/frames, or repetitions for over/out (default 1, max 100000; frame max 3600).", false, 1, 100000)
					.String("cpu", "CPU to step for instruction-level steps (default: main CPU). Other CPUs keep running normally.")
					.Integer("timeout_ms", $"Maximum wall-clock time to wait (default 10000, max {MaxTimeoutMs}). On timeout, execution is paused.", false, 100, MaxTimeoutMs)
					.Build(),
				Step
			));

			registry.Add(new McpTool(
				"run_until",
				"The main exploration primitive: resumes execution and waits until execution stops (breakpoint hit, break on NMI/IRQ if requested, " +
				"or another break such as BRK), or until a timeout in frames or milliseconds is reached, in which case execution is paused. " +
				"Always returns with execution paused at an instruction boundary (unless the ROM was unloaded), with: stop_reason " +
				"(breakpoint, nmi, irq, step, pause, brk/cop/..., timeout_frames, timeout_ms, rom_unloaded), the breakpoint that was hit, the memory operation " +
				"that triggered it (address/value/type), frames and milliseconds elapsed, CPU state and the current instruction. " +
				"Optionally adds a temporary breakpoint (break_address, ...) that only exists for this call; existing breakpoints stay active (see add_breakpoint).",
				McpSchema.Create()
					.Integer("timeout_frames", $"Stop after this many frames (default 600 = ~10 seconds of game time, max {MaxTimeoutFrames}).", false, 1, MaxTimeoutFrames)
					.Integer("timeout_ms", $"Stop after this wall-clock time in milliseconds (default 10000, max {MaxTimeoutMs}).", false, 100, MaxTimeoutMs)
					.String("until", "Optional: nmi or irq to stop at the start of the next NMI/IRQ handler.", false, new[] { "nmi", "irq" })
					.Address("break_address", "Optional temporary breakpoint address (in break_memory_type).")
					.Address("break_end_address", "Optional end address (inclusive) to make the temporary breakpoint a range.")
					.String("break_type", "Temporary breakpoint type: exec (default), read, write, or rw.", false, new[] { "exec", "read", "write", "rw" })
					.String("break_memory_type", "Memory type of break_address (default: the CPU's address space, e.g. SnesMemory).")
					.String("break_condition", "Optional condition for the temporary breakpoint, in Mesen's expression syntax (e.g. \"a == $10\").")
					.String("cpu", "CPU for the temporary breakpoint (default: main CPU).")
					.Build(),
				RunUntil
			));

			registry.Add(new McpTool(
				"set_input",
				$"Holds controller buttons for a number of frames (max {MaxInputFrames}), then releases them, to drive gameplay and reach new code. " +
				"If execution is paused, it advances exactly that many frames and stays paused; if running, it keeps running. " +
				"A breakpoint hit during that time stops early (buttons are always released). Returns frames elapsed and the stop state. " +
				"Buttons: a, b, x, y, l, r, up, down, left, right, select, start (u/d are extra buttons on some controllers). " +
				"An empty button list holds nothing (lets time pass with no input).",
				McpSchema.Create()
					.StringArray("buttons", "Buttons to hold, e.g. [\"start\"] or [\"right\", \"b\"].", true, ButtonNames)
					.Integer("frames", $"Number of frames to hold the buttons (default 1, max {MaxInputFrames}).", false, 1, MaxInputFrames)
					.Integer("port", "Controller port (0 = player 1, default 0).", false, 0, 7)
					.Build(),
				SetInput
			));
		}

		private static async Task<JsonObject> Pause(McpArgs args, CancellationToken ct)
		{
			await McpDebugSession.Instance.ExecutionLock.WaitAsync(ct);
			try {
				if(EmuApi.IsPaused()) {
					JsonObject result = BuildStopResult("already_paused", null, McpHelpers.GetMainCpu(), null, null);
					return result;
				}

				McpStopEvent? evt = await PauseAndWaitAsync();
				return BuildStopResult(evt?.Reason == McpStopReason.RomUnloaded ? "rom_unloaded" : "pause", evt, null, null, null);
			} finally {
				McpDebugSession.Instance.ExecutionLock.Release();
			}
		}

		private static async Task<JsonObject> Resume(McpArgs args, CancellationToken ct)
		{
			await McpDebugSession.Instance.ExecutionLock.WaitAsync(ct);
			try {
				McpDebugSession.Instance.DisarmStopWaiter();
				DebugApi.ResumeExecution();
				return new JsonObject() { ["paused"] = EmuApi.IsPaused() };
			} finally {
				McpDebugSession.Instance.ExecutionLock.Release();
			}
		}

		private static async Task<JsonObject> Step(McpArgs args, CancellationToken ct)
		{
			string typeName = args.GetString("type") ?? "instruction";
			if(!StepTypes.TryGetValue(typeName, out StepType stepType)) {
				throw new McpToolException($"Invalid step type '{typeName}'. Valid types: {string.Join(", ", StepTypes.Keys)}.");
			}

			CpuType cpu = McpHelpers.ResolveCpu(args);
			int count = (int)args.GetInt("count", 1, 1, 100000);
			int timeoutMs = (int)args.GetInt("timeout_ms", 10000, 100, MaxTimeoutMs);

			switch(stepType) {
				case StepType.PpuStep:
				case StepType.PpuScanline:
				case StepType.PpuFrame:
				case StepType.RunToNmi:
				case StepType.RunToIrq:
					cpu = cpu.GetConsoleType().GetMainCpuType();
					break;
			}

			if(stepType == StepType.PpuFrame && count > MaxInputFrames) {
				throw new McpToolException($"'count' must be at most {MaxInputFrames} for frame steps.");
			}

			DebuggerFeatures features = DebugApi.GetDebuggerFeatures(cpu);
			if((stepType == StepType.StepOver && !features.StepOver) || (stepType == StepType.StepOut && !features.StepOut) ||
				(stepType == StepType.StepBack && !features.StepBack) || (stepType == StepType.CpuCycleStep && !features.CpuCycleStep) ||
				(stepType == StepType.RunToNmi && !features.RunToNmi) || (stepType == StepType.RunToIrq && !features.RunToIrq)) {
				throw new McpToolException($"Step type '{typeName}' is not supported for the {cpu} CPU.");
			}

			await McpDebugSession.Instance.ExecutionLock.WaitAsync(ct);
			try {
				Stopwatch sw = Stopwatch.StartNew();
				long startFrame = McpDebugSession.Instance.FrameCounter;

				//Step over/out only take one step per call, repeat them
				int repeat = 1;
				int stepCount = count;
				if(stepType == StepType.StepOver || stepType == StepType.StepOut || stepType == StepType.RunToNmi || stepType == StepType.RunToIrq) {
					repeat = count;
					stepCount = 1;
				} else if(stepType == StepType.StepBack) {
					stepCount = typeName switch {
						"back_scanline" => 1,
						"back_frame" => 2,
						_ => 0
					};
				}

				McpStopEvent? evt = null;
				string reason = "step";
				for(int i = 0; i < repeat; i++) {
					Task<McpStopEvent> waiter = McpDebugSession.Instance.ArmStopWaiter();
					DebugApi.Step(cpu, stepCount, stepType);

					int remaining = Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds);
					evt = await WaitAsync(waiter, remaining, ct);
					if(evt == null) {
						reason = ct.IsCancellationRequested ? "cancelled" : "timeout_ms";
						evt = await PauseAndWaitAsync();
						break;
					} else if(evt.Reason == McpStopReason.RomUnloaded) {
						reason = "rom_unloaded";
						break;
					} else if(!IsStepSource(evt.Break.Source)) {
						//Breakpoint (or other break) hit during the step
						reason = GetBreakReason(evt.Break.Source);
						break;
					}
				}

				return BuildStopResult(reason, evt, cpu, startFrame, sw);
			} finally {
				McpDebugSession.Instance.ExecutionLock.Release();
			}
		}

		private static async Task<JsonObject> RunUntil(McpArgs args, CancellationToken ct)
		{
			int timeoutFrames = (int)args.GetInt("timeout_frames", 600, 1, MaxTimeoutFrames);
			int timeoutMs = (int)args.GetInt("timeout_ms", 10000, 100, MaxTimeoutMs);
			string? until = args.GetString("until");
			CpuType cpu = McpHelpers.ResolveCpu(args);
			CpuType mainCpu = cpu.GetConsoleType().GetMainCpuType();

			Breakpoint? tempBreakpoint = null;
			if(args.Has("break_address")) {
				tempBreakpoint = McpBreakpointTools.CreateBreakpoint(args, cpu, "break_address", "break_end_address", "break_type", "break_memory_type", "break_condition", "exec");
			} else if(args.Has("break_end_address") || args.Has("break_type") || args.Has("break_memory_type") || args.Has("break_condition")) {
				throw new McpToolException("break_address is required to add a temporary breakpoint.");
			}

			await McpDebugSession.Instance.ExecutionLock.WaitAsync(ct);
			try {
				if(tempBreakpoint != null) {
					await Dispatcher.UIThread.InvokeAsync(() => BreakpointManager.AddTemporaryBreakpoint(tempBreakpoint));
				}

				try {
					Stopwatch sw = Stopwatch.StartNew();
					long startFrame = McpDebugSession.Instance.FrameCounter;
					Task<McpStopEvent> waiter = McpDebugSession.Instance.ArmStopWaiter(timeoutFrames);

					if(until == "nmi") {
						DebugApi.Step(mainCpu, 1, StepType.RunToNmi);
					} else if(until == "irq") {
						DebugApi.Step(mainCpu, 1, StepType.RunToIrq);
					} else {
						DebugApi.ResumeExecution();
					}

					McpStopEvent? evt = await WaitAsync(waiter, timeoutMs, ct);
					string reason;
					if(evt == null) {
						reason = ct.IsCancellationRequested ? "cancelled" : "timeout_ms";
						evt = await PauseAndWaitAsync();
					} else if(evt.Reason == McpStopReason.FrameLimit) {
						reason = "timeout_frames";
						evt = await PauseAndWaitAsync();
					} else if(evt.Reason == McpStopReason.RomUnloaded) {
						reason = "rom_unloaded";
					} else {
						reason = GetBreakReason(evt.Break.Source);
						if(reason == "step" && until != null) {
							reason = until;
						}
					}

					JsonObject result = BuildStopResult(reason, evt, null, startFrame, sw);
					if(tempBreakpoint != null && evt?.Break.BreakpointId >= 0 && BreakpointManager.GetBreakpointById(evt.Break.BreakpointId) == tempBreakpoint) {
						result["breakpoint"] = new JsonObject() { ["temporary"] = true, ["description"] = McpBreakpointTools.Describe(tempBreakpoint) };
					}
					return result;
				} finally {
					if(tempBreakpoint != null) {
						await Dispatcher.UIThread.InvokeAsync(() => BreakpointManager.RemoveTemporaryBreakpoint(tempBreakpoint));
					}
				}
			} finally {
				McpDebugSession.Instance.ExecutionLock.Release();
			}
		}

		private static async Task<JsonObject> SetInput(McpArgs args, CancellationToken ct)
		{
			List<string> buttons = args.GetStringList("buttons").Select(b => b.Trim().ToLowerInvariant()).ToList();
			int frames = (int)args.GetInt("frames", 1, 1, MaxInputFrames);
			int port = (int)args.GetInt("port", 0, 0, 7);

			foreach(string button in buttons) {
				if(!ButtonNames.Contains(button)) {
					throw new McpToolException($"Unknown button '{button}'. Valid buttons: {string.Join(", ", ButtonNames)}.");
				}
			}

			List<int> availablePorts = DebugApi.GetAvailableInputOverrides();
			if(!availablePorts.Contains(port)) {
				string available = availablePorts.Count > 0 ? "Ports with a controller: " + string.Join(", ", availablePorts) + "." : "No controllers are connected: ask the user to configure a controller in Mesen's input settings.";
				throw new McpToolException($"No controller is connected to port {port}. {available}");
			}

			DebugControllerState state = new DebugControllerState() {
				A = buttons.Contains("a"),
				B = buttons.Contains("b"),
				X = buttons.Contains("x"),
				Y = buttons.Contains("y"),
				L = buttons.Contains("l"),
				R = buttons.Contains("r"),
				U = buttons.Contains("u"),
				D = buttons.Contains("d"),
				Up = buttons.Contains("up"),
				Down = buttons.Contains("down"),
				Left = buttons.Contains("left"),
				Right = buttons.Contains("right"),
				Select = buttons.Contains("select"),
				Start = buttons.Contains("start")
			};

			await McpDebugSession.Instance.ExecutionLock.WaitAsync(ct);
			try {
				Stopwatch sw = Stopwatch.StartNew();
				long startFrame = McpDebugSession.Instance.FrameCounter;
				CpuType mainCpu = McpHelpers.GetMainCpu();
				bool wasPaused = EmuApi.IsPaused();
				//Allow ~20 fps worst case, plus a margin
				int timeoutMs = Math.Min(MaxTimeoutMs * 2, frames * 50 + 5000);

				DebugApi.SetInputOverrides((uint)port, state);
				McpStopEvent? evt;
				string reason;
				try {
					Task<McpStopEvent> waiter;
					if(wasPaused) {
						waiter = McpDebugSession.Instance.ArmStopWaiter();
						DebugApi.Step(mainCpu, frames, StepType.PpuFrame);
					} else {
						waiter = McpDebugSession.Instance.ArmStopWaiter(frames);
					}

					evt = await WaitAsync(waiter, timeoutMs, ct);
					if(evt == null) {
						reason = ct.IsCancellationRequested ? "cancelled" : "timeout_ms";
						evt = await PauseAndWaitAsync();
					} else if(evt.Reason == McpStopReason.FrameLimit) {
						reason = "done";
					} else if(evt.Reason == McpStopReason.RomUnloaded) {
						reason = "rom_unloaded";
					} else {
						reason = IsStepSource(evt.Break.Source) ? "done" : GetBreakReason(evt.Break.Source);
					}
				} finally {
					McpDebugSession.Instance.DisarmStopWaiter();
					DebugApi.SetInputOverrides((uint)port, new DebugControllerState());
				}

				if(reason == "done" && !wasPaused) {
					//Still running, don't include a (stale) CPU state
					return new JsonObject() {
						["stop_reason"] = "done",
						["paused"] = EmuApi.IsPaused(),
						["frames_elapsed"] = McpDebugSession.Instance.FrameCounter - startFrame,
						["elapsed_ms"] = sw.ElapsedMilliseconds
					};
				}
				return BuildStopResult(reason, evt, null, startFrame, sw);
			} finally {
				McpDebugSession.Instance.ExecutionLock.Release();
			}
		}

		/// <summary>Pauses execution and waits for the resulting break, so that execution is stopped at an instruction boundary</summary>
		public static async Task<McpStopEvent?> PauseAndWaitAsync()
		{
			if(EmuApi.IsPaused()) {
				return null;
			}
			Task<McpStopEvent> waiter = McpDebugSession.Instance.ArmStopWaiter();
			EmuApi.Pause();
			McpStopEvent? evt = await WaitAsync(waiter, PauseTimeoutMs, CancellationToken.None);
			McpDebugSession.Instance.DisarmStopWaiter();
			return evt;
		}

		private static async Task<McpStopEvent?> WaitAsync(Task<McpStopEvent> waiter, int timeoutMs, CancellationToken ct)
		{
			try {
				Task completed = await Task.WhenAny(waiter, Task.Delay(timeoutMs, ct));
				if(completed == waiter && waiter.IsCompletedSuccessfully) {
					return waiter.Result;
				}
			} catch(OperationCanceledException) {
			}
			McpDebugSession.Instance.DisarmStopWaiter();
			return null;
		}

		private static bool IsStepSource(BreakSource source)
		{
			return source == BreakSource.CpuStep || source == BreakSource.PpuStep || source == BreakSource.Irq || source == BreakSource.Nmi || source == BreakSource.Pause;
		}

		private static string GetBreakReason(BreakSource source)
		{
			return source switch {
				BreakSource.Breakpoint => "breakpoint",
				BreakSource.Pause => "pause",
				BreakSource.CpuStep or BreakSource.PpuStep => "step",
				BreakSource.Irq => "irq",
				BreakSource.Nmi => "nmi",
				BreakSource.BreakOnBrk => "brk",
				BreakSource.BreakOnCop => "cop",
				BreakSource.BreakOnWdm => "wdm",
				BreakSource.BreakOnStp => "stp",
				_ => "other:" + source.ToString()
			};
		}

		public static JsonObject BuildStopResult(string reason, McpStopEvent? evt, CpuType? cpuOverride, long? startFrame, Stopwatch? sw)
		{
			JsonObject result = new JsonObject() {
				["stop_reason"] = reason,
				["paused"] = EmuApi.IsPaused()
			};

			if(reason == "rom_unloaded") {
				return result;
			}

			CpuType cpu = cpuOverride ?? McpHelpers.GetMainCpu();
			if(evt != null && evt.Reason == McpStopReason.Break) {
				BreakEvent brk = evt.Break;
				cpu = cpuOverride ?? brk.SourceCpu;
				result["break_source"] = brk.Source.ToString();
				result["source_cpu"] = brk.SourceCpu.ToString();

				if(brk.BreakpointId >= 0) {
					Breakpoint? bp = BreakpointManager.GetBreakpointById(brk.BreakpointId);
					if(bp != null) {
						result["breakpoint"] = McpBreakpointTools.ToJson(bp);
					}
				}

				if(brk.Source == BreakSource.Breakpoint) {
					JsonObject operation = new JsonObject() {
						["type"] = brk.Operation.Type.ToString(),
						["address"] = McpHelpers.FormatAddress(brk.Operation.MemType, brk.Operation.Address),
						["memory_type"] = brk.Operation.MemType.ToString(),
						["value"] = McpHelpers.Hex(brk.Operation.Value, brk.Operation.Value > 0xFF ? 4 : 2)
					};
					McpHelpers.AddLabelInfo(operation, brk.Operation.MemType, brk.Operation.Address);
					result["operation"] = operation;
				}
			}

			if(startFrame != null) {
				result["frames_elapsed"] = McpDebugSession.Instance.FrameCounter - startFrame.Value;
			}
			if(sw != null) {
				result["elapsed_ms"] = sw.ElapsedMilliseconds;
			}

			try {
				result["cpu"] = cpu.ToString();
				result["cpu_state"] = McpHelpers.GetCpuState(cpu);
				result["instruction"] = GetCurrentInstruction(cpu);
				JsonObject? ppu = McpStateTools.GetPpuPosition(cpu);
				if(ppu != null) {
					result["ppu"] = ppu;
				}
			} catch(Exception) {
				//ROM was unloaded while building the result
			}
			return result;
		}

		private static JsonObject GetCurrentInstruction(CpuType cpu)
		{
			UInt32 pc = DebugApi.GetProgramCounter(cpu, true);
			JsonObject instr = new JsonObject() {
				["address"] = McpHelpers.FormatCpuAddress(cpu, pc)
			};

			foreach(CodeLineData line in DebugApi.GetDisassemblyOutput(cpu, pc, 8)) {
				if(line.Address == pc && line.OpSize > 0 && !line.Flags.HasFlag(LineFlags.Label) && !line.Flags.HasFlag(LineFlags.Empty)) {
					instr["bytes"] = McpHelpers.HexBytes(line.ByteCode, 0, Math.Min((int)line.OpSize, line.ByteCode.Length));
					instr["text"] = line.Text.Trim();
					string ea = line.GetEffectiveAddressString("X" + cpu.GetAddressSize(), out _);
					if(ea.Length > 0) {
						instr["effective_address"] = ea + line.GetValueString();
					}
					if(line.Comment.Length > 0) {
						instr["comment"] = line.Comment.Trim();
					}
					break;
				}
			}
			McpHelpers.AddLabelInfo(instr, cpu.ToMemoryType(), pc);
			return instr;
		}
	}
}
