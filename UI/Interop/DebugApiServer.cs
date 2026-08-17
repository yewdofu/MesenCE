using Mesen.Debugger;
using Mesen.Debugger.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Mesen.Interop
{
	/// <summary>
	/// Local debug API server for MesenCE.
	/// Exposes a current-user restricted named pipe (mesen-debug-api) speaking newline-delimited
	/// compact JSON-RPC 2.0 so an external MCP server (or any other tool) can inspect and drive
	/// the SNES 65816 debugger of a running MesenCE instance.
	/// Only started when --debugApi is passed on the command line.
	/// </summary>
	public static class DebugApiServer
	{
		public const string PipeName = "mesen-debug-api";

		private const int MaxMemoryTransfer = 64 * 1024;
		private const int BreakWaitTimeoutMs = 5000;

		private static bool _started = false;
		private static CancellationTokenSource? _cts;
		private static NamedPipeServerStream? _pipe;
		private static Channel<QueuedRequest> _requestChannel = Channel.CreateUnbounded<QueuedRequest>();
		private static System.Collections.Concurrent.ConcurrentQueue<QueuedNotification> _notificationQueue = new();
		private static SemaphoreSlim _notificationSignal = new(0, int.MaxValue);
		private static SemaphoreSlim _breakSemaphore = new(0, int.MaxValue);
		private static int _breakCounter = 0;
		private static SemaphoreSlim _resumeSemaphore = new(0, int.MaxValue);
		private static int _resumeCounter = 0;
		private static int _isClientConnected = 0;
		private static long _generationCounter = 0;
		private static long _currentGeneration = 0;

		private static readonly object _writeLock = new();

		/// <summary>
		/// Serializes all notification and request handling that touches the DebugApi /
		/// BreakpointManager so that pause/step responses and event.break notifications are
		/// delivered in a consistent order and never interleave with each other.
		/// </summary>
		private static readonly SemaphoreSlim _apiGate = new(1, 1);

		private static NotificationListener? _listener;

		public static bool IsEnabled => _started;
		public static bool IsClientConnected => Volatile.Read(ref _isClientConnected) == 1;

		public static void Start()
		{
			if(_started) {
				return;
			}

			_started = true;
			_cts = new CancellationTokenSource();
			_requestChannel = Channel.CreateUnbounded<QueuedRequest>();
			_notificationQueue = new System.Collections.Concurrent.ConcurrentQueue<QueuedNotification>();
			_notificationSignal = new SemaphoreSlim(0, int.MaxValue);
			Volatile.Write(ref _resumeCounter, 0);

			_listener = new NotificationListener();
			_listener.OnNotification += OnNotification;

			Thread acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "DebugApi-Accept" };
			acceptThread.Start();

			Thread processThread = new Thread(ProcessLoop) { IsBackground = true, Name = "DebugApi-Process" };
			processThread.Start();

			Thread notificationThread = new Thread(NotificationLoop) { IsBackground = true, Name = "DebugApi-Notify" };
			notificationThread.Start();
		}

		public static void Dispose()
		{
			if(!_started) {
				return;
			}

			_started = false;
			_cts?.Cancel();

			//Invalidate the current session first so that any in-flight worker or delayed
			//finally cannot touch the new generation or the debugger afterwards.
			Volatile.Write(ref _isClientConnected, 0);
			Volatile.Write(ref _currentGeneration, Interlocked.Increment(ref _generationCounter));

			//Wait for any in-flight request / notification holding the API gate to finish before
			//we touch the DebugApi / BreakpointManager so we never race the worker. The
			//synchronous Wait (no cancellation token) ensures our own cleanup cannot be cancelled
			//by the shutdown token.
			_apiGate.Wait();
			try {
				//Ensure external breakpoints are cleared and the debugger is released, even on
				//shutdown. This is idempotent and the generation check in OnClientDisconnected
				//makes any concurrent double cleanup harmless.
				BreakpointManager.ClearExternalBreakpoints();
				if(!DebugWindowManager.HasOpenedDebugWindows()) {
					try {
						DebugApi.ReleaseDebugger();
					} catch {
					}
				}
				//After the API session the SNES debugger flag is only kept enabled while a SNES
				//debugger window remains open; otherwise it is reset to false.
				if(!DebugWindowManager.HasDebuggerWindow(CpuType.Snes)) {
					try {
						ConfigApi.SetDebuggerFlag(CpuType.Snes.GetDebuggerFlag(), false);
					} catch {
					}
				}

				//Clear any residual notification / break state from the current session.
				while(_notificationQueue.TryDequeue(out _)) {
				}
				while(_notificationSignal.Wait(0)) {
				}
				Volatile.Write(ref _breakCounter, 0);
				while(_breakSemaphore.Wait(0)) {
				}
				Volatile.Write(ref _resumeCounter, 0);
				while(_resumeSemaphore.Wait(0)) {
				}
			} finally {
				_apiGate.Release();
			}

			//Wake the notification worker so it can observe the cancellation and exit.
			_notificationSignal.Release();

			try {
				//The pipe is assigned before WaitForConnection, so this reliably closes it even
				//while the accept loop is blocked waiting for a connection.
				_pipe?.Close();
			} catch {
			}

			_requestChannel.Writer.TryComplete();
			_listener?.Dispose();
			_listener = null;
		}

		private static void OnNotification(NotificationEventArgs e)
		{
			//This runs on the emulation thread - do not block, serialize, write to the pipe, or
			//touch the DebugApi here. Only copy native pointer contents by value and enqueue
			//them for the notification worker to process later. When no client is connected we
			//drop the notification entirely so the queue never grows without bound; the
			//pause/step CodeBreak path is only relevant while a client is connected.
			try {
				if(!IsClientConnected) {
					return;
				}

				long gen = Volatile.Read(ref _currentGeneration);
				switch(e.NotificationType) {
					case ConsoleNotificationType.CodeBreak: {
						BreakEvent evt = Marshal.PtrToStructure<BreakEvent>(e.Parameter);
						Interlocked.Increment(ref _breakCounter);
						_notificationQueue.Enqueue(new QueuedNotification() {
							Generation = gen,
							Type = PendingNotificationType.CodeBreak,
							BreakEvent = evt
						});
						_notificationSignal.Release();
						//Wake the break waiter (the pause/step request worker) directly. This is
						//safe even while the request worker holds the API gate because we only
						//touch the semaphore/counter here, never the DebugApi.
						_breakSemaphore.Release();
						break;
					}

					case ConsoleNotificationType.DebuggerResumed:
					case ConsoleNotificationType.GameResumed:
						//Increment the resume counter and release the resume semaphore so a
						//debug.resume handler waiting under the API gate can observe that the
						//emulation has actually resumed. Like the break semaphore, this only
						//touches the semaphore/counter here (never the DebugApi), so it is safe
						//to do even while the request worker holds the API gate.
						Interlocked.Increment(ref _resumeCounter);
						_resumeSemaphore.Release();
						_notificationQueue.Enqueue(new QueuedNotification() {
							Generation = gen,
							Type = PendingNotificationType.Resumed
						});
						_notificationSignal.Release();
						break;

					case ConsoleNotificationType.GameLoaded: {
						GameLoadedEventParams evtParams = Marshal.PtrToStructure<GameLoadedEventParams>(e.Parameter);
						_notificationQueue.Enqueue(new QueuedNotification() {
							Generation = gen,
							Type = PendingNotificationType.GameLoaded,
							GameLoadedParams = evtParams
						});
						_notificationSignal.Release();
						break;
					}

					case ConsoleNotificationType.EmulationStopped:
						_notificationQueue.Enqueue(new QueuedNotification() {
							Generation = gen,
							Type = PendingNotificationType.EmulationStopped
						});
						_notificationSignal.Release();
						break;
				}
			} catch {
			}
		}

		private static void AcceptLoop(object? _)
		{
			CancellationToken ct = _cts!.Token;
			while(!ct.IsCancellationRequested) {
				NamedPipeServerStream? pipe = null;
				long gen = 0;
				try {
					//PipeOptions.CurrentUserOnly restricts access to processes running under
					//the current user account (the default security descriptor grants access
					//only to the current user).
					pipe = new NamedPipeServerStream(
						PipeName,
						PipeDirection.InOut,
						1,
						PipeTransmissionMode.Byte,
						PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
						4096,
						4096
					);

					//Assign the pipe before waiting for a connection so that a concurrent
					//Dispose can close it even while we are blocked waiting. Waiting is made
					//cancellable so shutdown does not hang here.
					_pipe = pipe;
					pipe.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
					if(ct.IsCancellationRequested) {
						break;
					}

					gen = OnClientConnected();
					ReadLoop(pipe, ct, gen);
				} catch(OperationCanceledException) {
					//Shutting down
					break;
				} catch {
					//Connection aborted or shutting down
				} finally {
					//Ensure the pipe is always disposed and the session is cleaned up, even on exceptions
					if(_pipe == pipe) {
						_pipe = null;
					}
					if(gen != 0) {
						OnClientDisconnected(gen);
					}
					try {
						pipe?.Dispose();
					} catch {
					}
				}
			}
		}

		private static long OnClientConnected()
		{
			long gen = Interlocked.Increment(ref _generationCounter);
			Volatile.Write(ref _currentGeneration, gen);
			Volatile.Write(ref _isClientConnected, 1);

			//Enable the SNES debugger flag BEFORE initializing the debugger so that the native
			//SnesDebugger's ProcessConfigChange reads the flag as enabled. Without this, _debuggerEnabled
			//stays false and BuildCache / execution breakpoints don't run, leaving debug.getCurrentInstruction
			//empty. Set the flag even when no ROM is loaded so it is already active when a game is loaded
			//later (the GameLoaded handler then initializes the debugger with the flag enabled).
			ConfigApi.SetDebuggerFlag(CpuType.Snes.GetDebuggerFlag(), true);

			//Initialize the debugger under the API gate so it never races the notification /
			//request handling that just started for this new connection.
			_apiGate.Wait();
			try {
				if(EmuApi.IsRunning()) {
					try {
						DebugApi.InitializeDebugger();
					} catch {
					}
				}
			} finally {
				_apiGate.Release();
			}
			return gen;
		}

		private static void OnClientDisconnected(long gen)
		{
			//Only the current session may perform cleanup. If Dispose (or a newer connection)
			//has already advanced the generation, a delayed finally from an older session must
			//not touch the new session or the debugger.
			if(gen != Volatile.Read(ref _currentGeneration)) {
				return;
			}

			//Invalidate this session first so that queued requests / notifications / responses
			//belonging to it are discarded and never delivered to a future client.
			Volatile.Write(ref _isClientConnected, 0);
			Volatile.Write(ref _currentGeneration, Interlocked.Increment(ref _generationCounter));

			//Wait for any in-flight request / notification that already holds the API gate to
			//finish, then perform the cleanup under the gate so it never races the worker
			//touching the DebugApi / BreakpointManager. This guarantees the old session is
			//fully cleaned up before the next connection is handled.
			_apiGate.Wait();
			try {
				//Remove the external breakpoints added by the API client
				BreakpointManager.ClearExternalBreakpoints();
				if(!DebugWindowManager.HasOpenedDebugWindows()) {
					try {
						DebugApi.ReleaseDebugger();
					} catch {
					}
				}
				//After the API session the SNES debugger flag is only kept enabled while a SNES
				//debugger window remains open; otherwise it is reset to false.
				if(!DebugWindowManager.HasDebuggerWindow(CpuType.Snes)) {
					try {
						ConfigApi.SetDebuggerFlag(CpuType.Snes.GetDebuggerFlag(), false);
					} catch {
					}
				}

				//Clear any residual notification / break state from the old session so the next
				//connection never observes them.
				DrainNotifications();
				while(_notificationQueue.TryDequeue(out _)) {
				}
				while(_notificationSignal.Wait(0)) {
				}
				Volatile.Write(ref _breakCounter, 0);
				while(_breakSemaphore.Wait(0)) {
				}
				Volatile.Write(ref _resumeCounter, 0);
				while(_resumeSemaphore.Wait(0)) {
				}
			} finally {
				_apiGate.Release();
			}
		}

		private static void ReadLoop(NamedPipeServerStream pipe, CancellationToken ct, long gen)
		{
			try {
				using StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
				while(!ct.IsCancellationRequested) {
					string? line;
					try {
						line = reader.ReadLine();
					} catch {
						break;
					}

					if(line == null) {
						//Client disconnected
						break;
					}

					if(string.IsNullOrWhiteSpace(line)) {
						continue;
					}

					RpcRequest? req;
					try {
						req = JsonSerializer.Deserialize(line, DebugApiJsonContext.Default.RpcRequest);
					} catch(JsonException) {
						//Parse errors may respond with an id of null
						SendError(null, -32700, "Parse error", gen);
						continue;
					}

					if(req == null) {
						continue;
					}

					if(!IsValidRequest(req, gen)) {
						continue;
					}

					_requestChannel.Writer.TryWrite(new QueuedRequest() { Generation = gen, Request = req });
				}
			} catch {
			}
		}

		private static bool IsValidRequest(RpcRequest req, long gen)
		{
			if(req.JsonRpc != "2.0" || string.IsNullOrEmpty(req.Method) || !IsValidId(req.Id)) {
				SendError(req.Id, -32600, "Invalid request", gen);
				return false;
			}
			return true;
		}

		private static bool IsValidId(JsonElement? id)
		{
			if(!id.HasValue) {
				//Missing id is a valid notification
				return true;
			}
			switch(id.Value.ValueKind) {
				case JsonValueKind.String:
				case JsonValueKind.Number:
				case JsonValueKind.Null:
					return true;
				default:
					return false;
			}
		}

		private static async void ProcessLoop(object? _)
		{
			CancellationToken ct = _cts!.Token;
			while(true) {
				QueuedRequest q;
				try {
					q = await _requestChannel.Reader.ReadAsync(ct);
				} catch(OperationCanceledException) {
					break;
				} catch(ChannelClosedException) {
					break;
				}

				long gen = q.Generation;

				//A queued request from a stale or disconnected session must never run (e.g. a
				//queued memory.write from a disconnected client must not execute without a
				//response). Check before taking the API gate and again after, since the session
				//may have ended while we were waiting for the gate.
				if(gen != Volatile.Read(ref _currentGeneration) || !IsClientConnected) {
					continue;
				}

				//Serialize notification and request handling that touches the DebugApi /
				//BreakpointManager. The callback (OnNotification) never takes this gate, so a
				//pause/step can still release the break semaphore while we hold the gate, and
				//we drain the queued event.break ourselves before responding.
				try {
					await _apiGate.WaitAsync(ct);
				} catch(OperationCanceledException) {
					//Shutting down - never leak from this async void loop
					break;
				}

				try {
					//Recheck under the gate: the session may have ended while we waited.
					if(gen != Volatile.Read(ref _currentGeneration) || !IsClientConnected) {
						continue;
					}

					//Send any pending notifications before handling the request
					DrainNotifications();
					await HandleRequestAsync(q.Request, gen, ct);
					DrainNotifications();
				} finally {
					_apiGate.Release();
				}
			}
		}

		private static void NotificationLoop(object? _)
		{
			CancellationToken ct = _cts!.Token;
			while(true) {
				try {
					_notificationSignal.Wait(ct);
				} catch(OperationCanceledException) {
					break;
				}

				//Send notifications even without a pending request. We take the API gate so
				//notification-driven DebugApi/BreakpointManager work never races a request.
				try {
					_apiGate.Wait(ct);
				} catch(OperationCanceledException) {
					break;
				}
				try {
					DrainNotifications();
				} finally {
					_apiGate.Release();
				}
			}
		}

		private static void DrainNotifications()
		{
			while(_notificationQueue.TryDequeue(out QueuedNotification q)) {
				ProcessSingleNotification(q);
			}
		}

		private static void ProcessSingleNotification(QueuedNotification q)
		{
			long gen = q.Generation;
			if(gen != Volatile.Read(ref _currentGeneration) || !IsClientConnected) {
				//Stale notification from a previous session - drop it
				return;
			}

			switch(q.Type) {
				case PendingNotificationType.CodeBreak: {
					BreakEvent evt = q.BreakEvent;
					string breakType = evt.Source switch {
						BreakSource.Pause => "pause",
						BreakSource.Breakpoint => "breakpoint",
						BreakSource.CpuStep or BreakSource.PpuStep => "step",
						_ => "break",
					};

					long? apiBreakpointId = null;
					if(evt.Source == BreakSource.Breakpoint && evt.BreakpointId >= 0) {
						//Convert the core-assigned id to the API-stable external id.
						//UI/assert/temporary breakpoints have no external id and report null.
						apiBreakpointId = BreakpointManager.GetExternalApiBreakpointId(evt.BreakpointId);
					}

					//Get the SNES PC from the stopped CPU state (BreakEvent.Operation.Address
					//is a memory operation address that can legitimately be zero)
					uint pc = DebugApi.GetProgramCounter(CpuType.Snes, true);

					SendRpcNotification(new RpcNotification() {
						Method = "event.break",
						Params = JsonSerializer.SerializeToElement(new BreakNotificationData() {
							BreakType = breakType,
							Cpu = CpuType.Snes.ToString(),
							Pc = pc,
							BreakpointId = apiBreakpointId,
						}, DebugApiJsonContext.Default.BreakNotificationData)
					}, gen);
					break;
				}

				case PendingNotificationType.Resumed:
					SendRpcNotification(new RpcNotification() {
						Method = "event.resumed",
						Params = JsonSerializer.SerializeToElement(new ResumedNotificationData() {
							Cpu = CpuType.Snes.ToString()
						}, DebugApiJsonContext.Default.ResumedNotificationData)
					}, gen);
					break;

				case PendingNotificationType.GameLoaded:
					ProcessGameLoaded(q.GameLoadedParams, gen);
					break;

				case PendingNotificationType.EmulationStopped:
					SendRpcNotification(new RpcNotification() {
						Method = "event.emulationStopped",
						Params = JsonSerializer.SerializeToElement(new EmulationStoppedNotificationData(), DebugApiJsonContext.Default.EmulationStoppedNotificationData)
					}, gen);
					break;
			}
		}

		private static void ProcessGameLoaded(GameLoadedEventParams evtParams, long gen)
		{
			RomInfo romInfo = EmuApi.GetRomInfo();
			if(EmuApi.IsRunning()) {
				//Re-initialize the debugger and immediately re-apply the external breakpoints
				//to the core rather than waiting for the next API request.
				try {
					DebugApi.InitializeDebugger();
				} catch {
				}
				try {
					BreakpointManager.SetBreakpoints();
				} catch {
				}
			}

			SendRpcNotification(new RpcNotification() {
				Method = "event.gameLoaded",
				Params = JsonSerializer.SerializeToElement(new GameLoadedNotificationData() {
					RomName = romInfo.GetRomName(),
					ConsoleType = romInfo.ConsoleType.ToString()
				}, DebugApiJsonContext.Default.GameLoadedNotificationData)
			}, gen);
		}

		private static void SendRpcNotification(RpcNotification notification, long gen)
		{
			SendRaw(JsonSerializer.Serialize(notification, DebugApiJsonContext.Default.RpcNotification), gen);
		}

		private static void SendRaw(string json, long gen)
		{
			if(gen != Volatile.Read(ref _currentGeneration) || !IsClientConnected) {
				return;
			}

			NamedPipeServerStream? pipe = _pipe;
			if(pipe == null) {
				return;
			}

			byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
			lock(_writeLock) {
				try {
					pipe.Write(bytes, 0, bytes.Length);
					pipe.Flush();
				} catch {
				}
			}
		}

		private static void SendError(JsonElement? id, int code, string message, long gen)
		{
			SendRaw(CreateResponse(id, error: new RpcError() { Code = code, Message = message }), gen);
		}

		private static string CreateResponse(JsonElement? id, JsonElement? result = null, RpcError? error = null)
		{
			RpcResponse response = new RpcResponse() { Id = id, Result = result, Error = error };
			return JsonSerializer.Serialize(response, DebugApiJsonContext.Default.RpcResponse);
		}

		private static async Task HandleRequestAsync(RpcRequest req, long gen, CancellationToken ct)
		{
			RpcResponder responder = new RpcResponder() {
				Id = req.Id,
				Generation = gen,
				NotifyOnly = !req.Id.HasValue
			};

			try {
				switch(req.Method) {
					case "system.getStatus": HandleSystemGetStatus(responder); break;
					case "debug.pause": await HandleDebugPauseAsync(responder, ct); break;
					case "debug.resume": await HandleDebugResumeAsync(responder, ct); break;
					case "debug.step": await HandleDebugStepAsync(responder, ct); break;
					case "debug.getCurrentInstruction": HandleGetCurrentInstruction(responder); break;
					case "cpu.getRegisters": HandleGetRegisters(responder); break;
					case "cpu.setRegisters": HandleSetRegisters(responder, req.Params); break;
					case "memory.list": HandleMemoryList(responder); break;
					case "memory.read": HandleMemoryRead(responder, req.Params); break;
					case "memory.write": HandleMemoryWrite(responder, req.Params); break;
					case "breakpoint.list": HandleBreakpointList(responder); break;
					case "breakpoint.add": HandleBreakpointAdd(responder, req.Params); break;
					case "breakpoint.remove": HandleBreakpointRemove(responder, req.Params); break;
					default:
						responder.SendError(-32601, "Method not found: " + req.Method);
						break;
				}
			} catch(RpcException ex) {
				responder.SendError(ex.Code, ex.Message);
			} catch(Exception ex) {
				responder.SendError(-32603, "Internal error: " + ex.Message);
			}
		}

		private static void EnsureDebuggerRunning()
		{
			if(!EmuApi.IsRunning()) {
				throw new RpcException(-32001, "No ROM is loaded");
			}
			RomInfo romInfo = EmuApi.GetRomInfo();
			if(romInfo.ConsoleType != ConsoleType.Snes) {
				throw new RpcException(-32005, "Unsupported console: " + romInfo.ConsoleType);
			}
			if(!DebugApi.IsDebuggerRunning()) {
				DebugApi.InitializeDebugger();
			}
		}

		private static void EnsureExecutionStopped()
		{
			EnsureDebuggerRunning();
			if(!DebugApi.IsExecutionStopped()) {
				throw new RpcException(-32002, "Execution is not stopped - pause the emulator first");
			}
		}

		private static SystemStatusResponse GetStatus()
		{
			bool running = EmuApi.IsRunning();
			RomInfo romInfo = running ? EmuApi.GetRomInfo() : new RomInfo();
			return new SystemStatusResponse() {
				RomLoaded = running,
				//Console is only reported when a ROM is loaded; otherwise it stays empty
				Console = running ? romInfo.ConsoleType.ToString() : "",
				Running = running,
				Paused = DebugApi.IsDebuggerRunning() && DebugApi.IsExecutionStopped(),
			};
		}

		private static void HandleSystemGetStatus(RpcResponder responder)
		{
			responder.SendResult(DebugApiJsonContext.Default.SystemStatusResponse, GetStatus());
		}

		private static async Task HandleDebugPauseAsync(RpcResponder responder, CancellationToken ct)
		{
			EnsureDebuggerRunning();
			if(DebugApi.IsExecutionStopped()) {
				//Already paused, return immediately
				responder.SendResult(DebugApiJsonContext.Default.SystemStatusResponse, GetStatus());
				return;
			}

			int before = Volatile.Read(ref _breakCounter);
			EmuApi.Pause();
			if(!await WaitForNewBreakAsync(before, BreakWaitTimeoutMs, ct)) {
				responder.SendError(-32003, "Timed out waiting for emulation to pause");
				return;
			}

			//Flush the pending event.break notification before responding
			DrainNotifications();
			responder.SendResult(DebugApiJsonContext.Default.SystemStatusResponse, GetStatus());
		}

		private static async Task HandleDebugResumeAsync(RpcResponder responder, CancellationToken ct)
		{
			EnsureDebuggerRunning();
			if(!DebugApi.IsExecutionStopped()) {
				//Already running - return the current status immediately
				responder.SendResult(DebugApiJsonContext.Default.SystemStatusResponse, GetStatus());
				return;
			}

			//Resume the emulator, then wait for the DebuggerResumed/GameResumed notification that
			//confirms execution actually resumed. This avoids returning paused:true right after
			//ResumeExecution like the previous synchronous handler did on real hardware.
			int before = Volatile.Read(ref _resumeCounter);
			DebugApi.ResumeExecution();
			if(!await WaitForNewResumeAsync(before, BreakWaitTimeoutMs, ct)) {
				responder.SendError(-32003, "Timed out waiting for emulation to resume");
				return;
			}

			//Flush the pending event.resumed notification (and any others) before responding
			DrainNotifications();
			responder.SendResult(DebugApiJsonContext.Default.SystemStatusResponse, GetStatus());
		}

		private static async Task HandleDebugStepAsync(RpcResponder responder, CancellationToken ct)
		{
			EnsureExecutionStopped();
			int before = Volatile.Read(ref _breakCounter);
			DebugApi.Step(CpuType.Snes, 1, StepType.Step);
			if(!await WaitForNewBreakAsync(before, BreakWaitTimeoutMs, ct)) {
				responder.SendError(-32003, "Timed out waiting for step to complete");
				return;
			}

			//Flush the pending event.break notification before responding
			DrainNotifications();
			InstructionResponse instruction = GetCurrentInstruction();
			responder.SendResult(DebugApiJsonContext.Default.InstructionResponse, instruction);
		}

		private static void HandleGetCurrentInstruction(RpcResponder responder)
		{
			EnsureExecutionStopped();
			responder.SendResult(DebugApiJsonContext.Default.InstructionResponse, GetCurrentInstruction());
		}

		private static InstructionResponse GetCurrentInstruction()
		{
			uint pc = DebugApi.GetProgramCounter(CpuType.Snes, true);
			//Fetch several rows because the first row returned may be a label/comment/block line
			//rather than the actual instruction at the program counter.
			CodeLineData[] lines = DebugApi.GetDisassemblyOutput(CpuType.Snes, pc, 8);

			//Prefer the real instruction at the current PC, then any instruction line as a fallback.
			CodeLineData? line = null;
			foreach(CodeLineData candidate in lines) {
				if(candidate.OpSize > 0 && candidate.Address == (int)pc) {
					line = candidate;
					break;
				}
			}
			if(line == null) {
				foreach(CodeLineData candidate in lines) {
					if(candidate.OpSize > 0) {
						line = candidate;
						break;
					}
				}
			}

			if(line == null) {
				//No instruction line found - return an empty response
				return new InstructionResponse() {
					Pc = pc,
					Address = -1,
					Text = "",
					ByteCode = "",
				};
			}

			return new InstructionResponse() {
				Pc = pc,
				Text = line.Text ?? "",
				ByteCode = line.ByteCodeStr ?? "",
				Address = line.Address,
			};
		}

		private static void HandleGetRegisters(RpcResponder responder)
		{
			EnsureExecutionStopped();
			SnesCpuState state = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
			responder.SendResult(DebugApiJsonContext.Default.SnesRegistersResponse, ToRegistersResponse(state));
		}

		private static void HandleSetRegisters(RpcResponder responder, JsonElement? paramsEl)
		{
			EnsureExecutionStopped();
			SnesRegistersUpdate? update = ParseParams<SnesRegistersUpdate>(paramsEl);
			if(update == null) {
				throw new RpcException(-32602, "Invalid parameters");
			}

			//Validate all values before casting them
			ValidateRegValue(update.A, "A", 0, 0xFFFF);
			ValidateRegValue(update.X, "X", 0, 0xFFFF);
			ValidateRegValue(update.Y, "Y", 0, 0xFFFF);
			ValidateRegValue(update.Sp, "SP", 0, 0xFFFF);
			ValidateRegValue(update.D, "D", 0, 0xFFFF);
			ValidateRegValue(update.Pc, "PC", 0, 0xFFFF);
			ValidateRegValue(update.K, "K", 0, 0xFF);
			ValidateRegValue(update.Dbr, "DBR", 0, 0xFF);
			ValidateRegValue(update.Ps, "PS", 0, 0xFF);

			SnesCpuState state = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
			if(update.A.HasValue) state.A = (UInt16)update.A.Value;
			if(update.X.HasValue) state.X = (UInt16)update.X.Value;
			if(update.Y.HasValue) state.Y = (UInt16)update.Y.Value;
			if(update.Sp.HasValue) state.SP = (UInt16)update.Sp.Value;
			if(update.D.HasValue) state.D = (UInt16)update.D.Value;
			if(update.Pc.HasValue) state.PC = (UInt16)update.Pc.Value;
			if(update.K.HasValue) state.K = (byte)update.K.Value;
			if(update.Dbr.HasValue) state.DBR = (byte)update.Dbr.Value;
			if(update.Ps.HasValue) state.PS = (SnesCpuFlags)update.Ps.Value;
			if(update.EmulationMode.HasValue) state.EmulationMode = update.EmulationMode.Value;

			DebugApi.SetCpuState(state, CpuType.Snes);

			//Re-fetch the native state after the update and return it
			SnesCpuState updated = DebugApi.GetCpuState<SnesCpuState>(CpuType.Snes);
			responder.SendResult(DebugApiJsonContext.Default.SnesRegistersResponse, ToRegistersResponse(updated));
		}

		private static void ValidateRegValue(int? value, string name, int min, int max)
		{
			if(value.HasValue && (value.Value < min || value.Value > max)) {
				throw new RpcException(-32602, $"{name} must be between {min} and {max}");
			}
		}

		private static SnesRegistersResponse ToRegistersResponse(SnesCpuState state)
		{
			return new SnesRegistersResponse() {
				A = state.A,
				X = state.X,
				Y = state.Y,
				Sp = state.SP,
				D = state.D,
				Pc = state.PC,
				K = state.K,
				Dbr = state.DBR,
				Ps = (byte)state.PS,
				EmulationMode = state.EmulationMode,
			};
		}

		private static void HandleMemoryList(RpcResponder responder)
		{
			EnsureDebuggerRunning();
			List<MemoryRegionInfo> regions = new List<MemoryRegionInfo>();
			foreach(MemoryType memType in GetSnesMemoryTypes()) {
				int size = DebugApi.GetMemorySize(memType);
				if(size <= 0) {
					//Exclude unavailable (size 0) regions
					continue;
				}
				regions.Add(new MemoryRegionInfo() {
					Id = memType.ToString(),
					Name = memType.ToString(),
					Size = size,
				});
			}
			responder.SendResult(DebugApiJsonContext.Default.MemoryListResponse, new MemoryListResponse() { Regions = regions });
		}

		private static void HandleMemoryRead(RpcResponder responder, JsonElement? paramsEl)
		{
			EnsureExecutionStopped();
			MemoryReadRequest? request = ParseParams<MemoryReadRequest>(paramsEl);
			if(request == null) {
				throw new RpcException(-32602, "Invalid parameters");
			}

			MemoryType memType = ParseSnesMemoryType(request.Type);
			ValidateRange(memType, request.Address, request.Length);

			byte[] data = DebugApi.GetMemoryValues(memType, (UInt32)request.Address, (UInt32)(request.Address + request.Length - 1));
			responder.SendResult(DebugApiJsonContext.Default.MemoryReadResponse, new MemoryReadResponse() {
				Address = request.Address,
				Data = Convert.ToBase64String(data),
			});
		}

		private static void HandleMemoryWrite(RpcResponder responder, JsonElement? paramsEl)
		{
			EnsureExecutionStopped();
			MemoryWriteRequest? request = ParseParams<MemoryWriteRequest>(paramsEl);
			if(request == null || string.IsNullOrEmpty(request.Data)) {
				throw new RpcException(-32602, "Invalid parameters");
			}

			byte[] data;
			try {
				data = Convert.FromBase64String(request.Data);
			} catch(FormatException) {
				throw new RpcException(-32602, "Invalid base64 data");
			}

			if(data.Length <= 0 || data.Length > MaxMemoryTransfer) {
				throw new RpcException(-32602, "Data length must be between 1 and " + MaxMemoryTransfer + " bytes");
			}

			MemoryType memType = ParseSnesMemoryType(request.Type);
			ValidateRange(memType, request.Address, data.Length);

			DebugApi.SetMemoryValues(memType, (UInt32)request.Address, data, data.Length);
			responder.SendResult(DebugApiJsonContext.Default.MemoryWriteResponse, new MemoryWriteResponse() { Written = data.Length });
		}

		private static void HandleBreakpointList(RpcResponder responder)
		{
			List<BreakpointInfo> breakpoints = new List<BreakpointInfo>();
			foreach(KeyValuePair<int, Breakpoint> kvp in BreakpointManager.GetExternalBreakpoints()) {
				breakpoints.Add(ToBreakpointInfo(kvp.Key, kvp.Value));
			}
			responder.SendResult(DebugApiJsonContext.Default.BreakpointListResponse, new BreakpointListResponse() { Breakpoints = breakpoints });
		}

		private static void HandleBreakpointAdd(RpcResponder responder, JsonElement? paramsEl)
		{
			EnsureDebuggerRunning();
			BreakpointAddRequest? request = ParseParams<BreakpointAddRequest>(paramsEl);
			if(request == null) {
				throw new RpcException(-32602, "Invalid parameters");
			}

			MemoryType memType = ParseSnesMemoryType(request.MemoryType ?? MemoryType.SnesMemory.ToString());

			bool breakOnExec, breakOnRead, breakOnWrite;
			switch((request.Type ?? "exec").ToLowerInvariant()) {
				case "exec": breakOnExec = true; breakOnRead = false; breakOnWrite = false; break;
				case "read": breakOnExec = false; breakOnRead = true; breakOnWrite = false; break;
				case "write": breakOnExec = false; breakOnRead = false; breakOnWrite = true; break;
				case "readwrite": breakOnExec = false; breakOnRead = true; breakOnWrite = true; break;
				default:
					throw new RpcException(-32602, "Invalid breakpoint type: " + request.Type);
			}

			long start = request.Address;
			long end = request.EndAddress ?? request.Address;
			if(start < 0 || start > uint.MaxValue) {
				throw new RpcException(-32602, "Address out of range");
			}
			if(end < 0 || end > uint.MaxValue) {
				throw new RpcException(-32602, "End address out of range");
			}
			if(end < start) {
				throw new RpcException(-32602, "End address must be >= start address");
			}

			long size = DebugApi.GetMemorySize(memType);
			if(end >= size) {
				throw new RpcException(-32602, "Address range out of bounds for memory type " + memType);
			}

			string condition = request.Condition ?? "";
			//The core stores conditions in a fixed 1000-byte buffer; leave room for a NUL terminator
			if(Encoding.UTF8.GetByteCount(condition.Replace(Environment.NewLine, " ").Trim()) >= 1000) {
				throw new RpcException(-32602, "Condition is too long (max 999 UTF-8 bytes)");
			}

			Breakpoint bp = new Breakpoint() {
				CpuType = CpuType.Snes,
				MemoryType = memType,
				StartAddress = (uint)start,
				EndAddress = (uint)end,
				BreakOnExec = breakOnExec,
				BreakOnRead = breakOnRead,
				BreakOnWrite = breakOnWrite,
				Enabled = request.Enabled ?? true,
				Condition = condition,
			};

			int apiId = BreakpointManager.AddExternalBreakpoint(bp);
			responder.SendResult(DebugApiJsonContext.Default.BreakpointInfo, ToBreakpointInfo(apiId, bp));
		}

		private static void HandleBreakpointRemove(RpcResponder responder, JsonElement? paramsEl)
		{
			BreakpointRemoveRequest? request = ParseParams<BreakpointRemoveRequest>(paramsEl);
			if(request == null) {
				throw new RpcException(-32602, "Invalid parameters");
			}

			if(request.Id < int.MinValue || request.Id > int.MaxValue) {
				throw new RpcException(-32602, "Invalid breakpoint id");
			}

			if(!BreakpointManager.RemoveExternalBreakpoint((int)request.Id)) {
				throw new RpcException(-32004, "Breakpoint not found: " + request.Id);
			}

			responder.SendResultCore(result: JsonSerializer.SerializeToElement(true, DebugApiJsonContext.Default.Boolean));
		}

		private static BreakpointInfo ToBreakpointInfo(int apiId, Breakpoint bp)
		{
			return new BreakpointInfo() {
				Id = apiId,
				Cpu = CpuType.Snes.ToString(),
				Type = bp.BreakOnExec ? "exec" : (bp.BreakOnRead && bp.BreakOnWrite ? "readwrite" : (bp.BreakOnRead ? "read" : "write")),
				MemoryType = bp.MemoryType.ToString(),
				Address = bp.StartAddress,
				EndAddress = bp.EndAddress != bp.StartAddress ? bp.EndAddress : null,
				Enabled = bp.Enabled,
			};
		}

		private static T? ParseParams<T>(JsonElement? paramsEl)
		{
			if(!paramsEl.HasValue || paramsEl.Value.ValueKind == JsonValueKind.Null) {
				return default;
			}
			JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)DebugApiJsonContext.Default.GetTypeInfo(typeof(T))!;
			try {
				return paramsEl.Value.Deserialize(typeInfo);
			} catch(JsonException) {
				throw new RpcException(-32602, "Invalid parameters");
			} catch(NotSupportedException) {
				throw new RpcException(-32602, "Invalid parameters");
			}
		}

		private static MemoryType ParseSnesMemoryType(string typeString)
		{
			if(!Enum.TryParse(typeString, true, out MemoryType memType) || !IsSnesMemoryType(memType)) {
				throw new RpcException(-32602, "Invalid or unsupported memory type: " + typeString);
			}
			return memType;
		}

		private static bool IsSnesMemoryType(MemoryType memType)
		{
			switch(memType) {
				case MemoryType.SnesMemory:
				case MemoryType.SnesPrgRom:
				case MemoryType.SnesWorkRam:
				case MemoryType.SnesSaveRam:
				case MemoryType.SnesVideoRam:
				case MemoryType.SnesSpriteRam:
				case MemoryType.SnesCgRam:
				case MemoryType.SnesRegister:
					return true;
				default:
					return false;
			}
		}

		private static MemoryType[] GetSnesMemoryTypes()
		{
			return new MemoryType[] {
				MemoryType.SnesMemory,
				MemoryType.SnesPrgRom,
				MemoryType.SnesWorkRam,
				MemoryType.SnesSaveRam,
				MemoryType.SnesVideoRam,
				MemoryType.SnesSpriteRam,
				MemoryType.SnesCgRam,
				MemoryType.SnesRegister,
			};
		}

		private static void ValidateRange(MemoryType memType, long address, long length)
		{
			if(address < 0 || length <= 0 || length > MaxMemoryTransfer) {
				throw new RpcException(-32602, "Invalid address/length (length must be between 1 and " + MaxMemoryTransfer + ")");
			}

			long size = DebugApi.GetMemorySize(memType);
			//Validate without adding address+length so a large length cannot overflow long.
			if(size <= 0 || address >= size || length > size - address) {
				throw new RpcException(-32602, "Range out of bounds (address=" + address + ", length=" + length + ", memory size=" + size + ")");
			}
		}

		private static async Task<bool> WaitForNewBreakAsync(int beforeCounter, int timeoutMs, CancellationToken ct)
		{
			Stopwatch sw = Stopwatch.StartNew();
			while(true) {
				int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
				if(remaining <= 0) {
					return false;
				}

				bool signaled;
				try {
					signaled = await _breakSemaphore.WaitAsync(remaining, ct);
				} catch(OperationCanceledException) {
					return false;
				}

				if(!signaled) {
					return false;
				}

				if(Volatile.Read(ref _breakCounter) > beforeCounter) {
					return true;
				}
			}
		}

		private static async Task<bool> WaitForNewResumeAsync(int beforeCounter, int timeoutMs, CancellationToken ct)
		{
			Stopwatch sw = Stopwatch.StartNew();
			while(true) {
				int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
				if(remaining <= 0) {
					return false;
				}

				bool signaled;
				try {
					signaled = await _resumeSemaphore.WaitAsync(remaining, ct);
				} catch(OperationCanceledException) {
					return false;
				}

				if(!signaled) {
					return false;
				}

				if(Volatile.Read(ref _resumeCounter) > beforeCounter) {
					return true;
				}
			}
		}

		private struct RpcResponder
		{
			public JsonElement? Id;
			public long Generation;
			public bool NotifyOnly;

			public void SendResult<T>(JsonTypeInfo<T> typeInfo, T value)
			{
				if(NotifyOnly || !IsCurrent()) {
					return;
				}
				JsonElement result = JsonSerializer.SerializeToElement(value, typeInfo);
				SendRaw(CreateResponse(Id, result: result), Generation);
			}

			public void SendError(int code, string message)
			{
				if(NotifyOnly || !IsCurrent()) {
					return;
				}
				SendRaw(CreateResponse(Id, error: new RpcError() { Code = code, Message = message }), Generation);
			}

			public void SendResultCore(JsonElement? result = null, RpcError? error = null)
			{
				if(NotifyOnly || !IsCurrent()) {
					return;
				}
				SendRaw(CreateResponse(Id, result: result, error: error), Generation);
			}

			private bool IsCurrent() => Generation == Volatile.Read(ref _currentGeneration) && IsClientConnected;
		}

		private enum PendingNotificationType
		{
			CodeBreak,
			Resumed,
			GameLoaded,
			EmulationStopped
		}

		private struct QueuedRequest
		{
			public long Generation;
			public RpcRequest Request;
		}

		private struct QueuedNotification
		{
			public long Generation;
			public PendingNotificationType Type;
			public BreakEvent BreakEvent;
			public GameLoadedEventParams GameLoadedParams;
		}

		private class RpcException : Exception
		{
			public int Code { get; }
			public RpcException(int code, string message) : base(message) { Code = code; }
		}
	}
}
