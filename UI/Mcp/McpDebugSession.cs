using Avalonia.Threading;
using Mesen.Debugger;
using Mesen.Debugger.Utilities;
using Mesen.Debugger.Windows;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp
{
	public enum McpStopReason
	{
		Break,
		FrameLimit,
		RomUnloaded
	}

	public class McpStopEvent
	{
		public McpStopReason Reason { get; }
		public BreakEvent Break { get; }

		public McpStopEvent(McpStopReason reason, BreakEvent evt = default)
		{
			Reason = reason;
			Break = evt;
		}
	}

	/// <summary>
	/// Keeps the debugger attached while the MCP server is used, and tracks emulator notifications
	/// (breaks, frames, ROM loading) so tools can wait for them without blocking the emulation thread.
	/// </summary>
	public class McpDebugSession
	{
		public static McpDebugSession Instance { get; } = new McpDebugSession();

		private object _lock = new();
		private NotificationListener? _listener;
		private bool _sessionAcquired;
		private HashSet<CpuType> _registeredCpuTypes = new();

		private volatile bool _loading;
		private long _frameCount;
		private long _breakCount;
		private BreakEvent _lastBreak;
		private DateTime? _lastBreakTime;

		private TaskCompletionSource<McpStopEvent>? _stopWaiter;
		private long _frameTarget = long.MaxValue;

		/// <summary>Serializes tools that control execution (pause/resume/step/run_until/etc.)</summary>
		public SemaphoreSlim ExecutionLock { get; } = new SemaphoreSlim(1, 1);

		public bool IsLoading { get { return _loading; } }
		public long FrameCounter { get { return Interlocked.Read(ref _frameCount); } }
		public long BreakCounter { get { return Interlocked.Read(ref _breakCount); } }
		public BreakEvent? LastBreak { get { return _lastBreakTime != null ? _lastBreak : null; } }
		public DateTime? LastBreakTime { get { return _lastBreakTime; } }

		public void Start()
		{
			lock(_lock) {
				if(_listener == null) {
					_listener = new NotificationListener();
					_listener.OnNotification += OnNotification;
				}
			}
		}

		public void Stop()
		{
			lock(_lock) {
				_listener?.Dispose();
				_listener = null;
			}

			Interlocked.Exchange(ref _stopWaiter, null)?.TrySetResult(new McpStopEvent(McpStopReason.RomUnloaded));

			if(Dispatcher.UIThread.CheckAccess()) {
				ReleaseSession();
			} else {
				Dispatcher.UIThread.Post(ReleaseSession);
			}
		}

		/// <summary>
		/// Ensures a ROM is loaded and the debugger is attached, with the workspace (labels/breakpoints) loaded,
		/// exactly as if a debugger window was opened.
		/// </summary>
		public async Task EnsureReadyAsync()
		{
			if(_loading) {
				throw new McpToolException("A ROM is currently being loaded, try again in a moment.");
			}
			if(!EmuApi.IsRunning()) {
				throw new McpToolException("No ROM is loaded. Ask the user to load a ROM in Mesen (or use load_rom if write access is enabled).");
			}

			await Dispatcher.UIThread.InvokeAsync(() => {
				//No-op if the debugger is already running
				DebugApi.InitializeDebugger();
				if(!_sessionAcquired) {
					DebugWindowManager.AcquireDebugSession();
					_sessionAcquired = true;
				}
				SyncCpuTypes();
			});

			if(_loading || !EmuApi.IsRunning()) {
				throw new McpToolException("The ROM was unloaded or is being reloaded, try again in a moment.");
			}
		}

		private void SyncCpuTypes()
		{
			//Register the ROM's CPUs so that breakpoints are sent to the core even when no debugger window is opened
			HashSet<CpuType> cpuTypes = EmuApi.GetRomInfo().CpuTypes;
			if(!_registeredCpuTypes.SetEquals(cpuTypes)) {
				UnregisterCpuTypes();
			}
			foreach(CpuType cpuType in cpuTypes) {
				if(_registeredCpuTypes.Add(cpuType)) {
					BreakpointManager.AddCpuType(cpuType);
				}

				//Same as an opened debugger window: the core only builds the disassembly cache for executed code
				//when this flag is set. Set it on every request, since closing a debugger window clears it.
				ConfigApi.SetDebuggerFlag(cpuType.GetDebuggerFlag(), true);
			}
		}

		private void UnregisterCpuTypes()
		{
			foreach(CpuType cpuType in _registeredCpuTypes) {
				BreakpointManager.RemoveCpuType(cpuType);
				bool hasDebuggerWindow = DebugWindowManager.GetDebugWindow<DebuggerWindow>(wnd => wnd.CpuType == cpuType) != null;
				if(!hasDebuggerWindow) {
					ConfigApi.SetDebuggerFlag(cpuType.GetDebuggerFlag(), false);
				}
			}
			_registeredCpuTypes.Clear();
		}

		private void ReleaseSession()
		{
			if(_sessionAcquired) {
				_sessionAcquired = false;
				UnregisterCpuTypes();
				DebugWindowManager.ReleaseDebugSession();
			}
		}

		private volatile bool _pauseOnReset;
		private List<(HashSet<ConsoleNotificationType> types, TaskCompletionSource<ConsoleNotificationType> tcs)> _notificationWaiters = new();

		/// <summary>
		/// Pauses execution when the next reset/power cycle/ROM load completes, so that execution stops on the first instruction
		/// (same as the debugger window's "Break on power cycle/reset" option)
		/// </summary>
		public void PauseOnNextReset(bool enabled)
		{
			_pauseOnReset = enabled;
		}

		/// <summary>Returns a task that completes when one of the given notifications is received</summary>
		public Task<ConsoleNotificationType> ArmNotificationWaiter(params ConsoleNotificationType[] types)
		{
			TaskCompletionSource<ConsoleNotificationType> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
			lock(_notificationWaiters) {
				_notificationWaiters.Add((new HashSet<ConsoleNotificationType>(types), tcs));
			}
			return tcs.Task;
		}

		public void DisarmNotificationWaiter(Task<ConsoleNotificationType> task)
		{
			lock(_notificationWaiters) {
				_notificationWaiters.RemoveAll(w => w.tcs.Task == task);
			}
		}

		private void ProcessNotificationWaiters(ConsoleNotificationType type)
		{
			lock(_notificationWaiters) {
				for(int i = _notificationWaiters.Count - 1; i >= 0; i--) {
					if(_notificationWaiters[i].types.Contains(type)) {
						_notificationWaiters[i].tcs.TrySetResult(type);
						_notificationWaiters.RemoveAt(i);
					}
				}
			}
		}

		private Timer? _saveTimer;

		/// <summary>
		/// Saves the debugger workspace (labels, breakpoints) a few seconds after the last change made through MCP.
		/// (The UI's AutoSave is throttled to once per minute, which could lose a lot of an agent's work on a crash.)
		/// </summary>
		public void ScheduleWorkspaceSave()
		{
			lock(_lock) {
				_saveTimer?.Dispose();
				_saveTimer = new Timer(_ => Dispatcher.UIThread.Post(() => {
					if(_sessionAcquired) {
						DebugWorkspaceManager.Save();
					}
				}), null, 2000, Timeout.Infinite);
			}
		}

		/// <summary>Arms a waiter that completes on the next break (breakpoint, step, pause), or when the ROM is unloaded</summary>
		public Task<McpStopEvent> ArmStopWaiter(long frameLimit = long.MaxValue)
		{
			TaskCompletionSource<McpStopEvent> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
			Interlocked.Exchange(ref _frameTarget, frameLimit == long.MaxValue ? long.MaxValue : FrameCounter + frameLimit);
			Interlocked.Exchange(ref _stopWaiter, tcs)?.TrySetCanceled();
			return tcs.Task;
		}

		public void DisarmStopWaiter()
		{
			Interlocked.Exchange(ref _frameTarget, long.MaxValue);
			Interlocked.Exchange(ref _stopWaiter, null)?.TrySetCanceled();
		}

		private void CompleteStopWaiter(McpStopEvent evt)
		{
			Interlocked.Exchange(ref _stopWaiter, null)?.TrySetResult(evt);
		}

		private void OnNotification(NotificationEventArgs e)
		{
			//Called on the emulation thread (usually) - must never block or call into the debugger
			switch(e.NotificationType) {
				case ConsoleNotificationType.CodeBreak:
					BreakEvent evt = Marshal.PtrToStructure<BreakEvent>(e.Parameter);
					_lastBreak = evt;
					_lastBreakTime = DateTime.Now;
					Interlocked.Increment(ref _breakCount);
					CompleteStopWaiter(new McpStopEvent(McpStopReason.Break, evt));
					break;

				case ConsoleNotificationType.PpuFrameDone:
					long frame = Interlocked.Increment(ref _frameCount);
					if(frame >= Interlocked.Read(ref _frameTarget)) {
						Interlocked.Exchange(ref _frameTarget, long.MaxValue);
						CompleteStopWaiter(new McpStopEvent(McpStopReason.FrameLimit));
					}
					break;

				case ConsoleNotificationType.BeforeGameLoad:
					_loading = true;
					CompleteStopWaiter(new McpStopEvent(McpStopReason.RomUnloaded));
					break;

				case ConsoleNotificationType.BeforeGameUnload:
				case ConsoleNotificationType.BeforeEmulationStop:
					CompleteStopWaiter(new McpStopEvent(McpStopReason.RomUnloaded));
					break;

				case ConsoleNotificationType.GameReset:
					if(_pauseOnReset) {
						_pauseOnReset = false;
						EmuApi.Pause();
					}
					break;

				case ConsoleNotificationType.GameLoaded:
				case ConsoleNotificationType.GameLoadFailed:
					_loading = false;
					_lastBreakTime = null;
					if(e.NotificationType == ConsoleNotificationType.GameLoaded) {
						GameLoadedEventParams evtParams = Marshal.PtrToStructure<GameLoadedEventParams>(e.Parameter);
						if(_pauseOnReset) {
							_pauseOnReset = false;
							if(!evtParams.IsPaused) {
								//The debugger must be running for the pause to happen on the first instruction
								//(it's already running if a ROM was loaded with the debugger attached before)
								DebugApi.InitializeDebugger();
								EmuApi.Pause();
							}
						}

						//New ROM may have a different set of CPUs
						Dispatcher.UIThread.Post(() => {
							if(_sessionAcquired) {
								SyncCpuTypes();
							}
						});
					}
					break;

				case ConsoleNotificationType.EmulationStopped:
					_loading = false;
					_lastBreakTime = null;
					break;
			}

			if(e.NotificationType != ConsoleNotificationType.PpuFrameDone) {
				ProcessNotificationWaiters(e.NotificationType);
			}
		}
	}
}
