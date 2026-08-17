using Mesen.Debugger.Utilities;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Mesen.Debugger
{
	public static class BreakpointManager
	{
		public static event EventHandler? BreakpointsChanged;

		private static List<Breakpoint> _breakpoints = new List<Breakpoint>();
		private static List<Breakpoint> _temporaryBreakpoints = new List<Breakpoint>();
		private static HashSet<CpuType> _activeCpuTypes = new HashSet<CpuType>();
		private static List<Breakpoint> _asserts = new List<Breakpoint>();

		/// <summary>
		/// Guards the structural state of the UI-side breakpoint lists, asserts, temporary
		/// breakpoints and active cpu types so the pipe worker (SetBreakpoints) can never
		/// enumerate them while the UI thread mutates them. Always acquire this lock before
		/// _externalLock. Never raise BreakpointsChanged or call DebugApi.SetBreakpoints while
		/// holding this lock.
		/// </summary>
		private static readonly object _structureLock = new();

		private static readonly object _externalLock = new();
		private static Dictionary<int, Breakpoint> _externalBreakpoints = new Dictionary<int, Breakpoint>();

		/// <summary>
		/// Serializes the whole SetBreakpoints pipeline (snapshot, core-&gt;api map build, and the
		/// final native DebugApi.SetBreakpoints call) so that multiple concurrent invocations
		/// cannot send an older snapshot to the core after a newer one. Acquire this before
		/// _structureLock / _externalLock; the native call happens inside this lock but outside
		/// the structural/external locks.
		/// </summary>
		private static readonly object _setBreakpointsLock = new();
		private static Dictionary<int, int> _coreToApiId = new Dictionary<int, int>();
		private static int _externalBreakpointIdCounter = 0;

		public static ReadOnlyCollection<Breakpoint> Breakpoints
		{
			get {
				lock(_structureLock) {
					return _breakpoints.ToList().AsReadOnly();
				}
			}
		}

		public static List<Breakpoint> Asserts
		{
			internal get {
				lock(_structureLock) {
					return _asserts.ToList();
				}
			}
			set {
				lock(_structureLock) {
					_asserts = value ?? new List<Breakpoint>();
				}
			}
		}

		public static List<Breakpoint> GetBreakpoints(CpuType cpuType)
		{
			List<Breakpoint> breakpoints = new List<Breakpoint>();
			lock(_structureLock) {
				foreach(Breakpoint bp in _breakpoints) {
					if(bp.CpuType == cpuType) {
						breakpoints.Add(bp);
					}
				}
			}
			return breakpoints;
		}

		public static void AddCpuType(CpuType cpuType)
		{
			lock(_structureLock) {
				_activeCpuTypes.Add(cpuType);
			}
			SetBreakpoints();
		}

		public static void RemoveCpuType(CpuType cpuType)
		{
			lock(_structureLock) {
				_activeCpuTypes.Remove(cpuType);
			}
			SetBreakpoints();
		}

		public static void RefreshBreakpoints(Breakpoint? bp = null)
		{
			//Raised outside any lock to avoid deadlocks with UI handlers
			BreakpointsChanged?.Invoke(bp, EventArgs.Empty);
			SetBreakpoints();
		}

		public static void ClearBreakpoints()
		{
			lock(_structureLock) {
				_breakpoints = new();
			}
			RefreshBreakpoints();
		}

		public static void AddBreakpoints(List<Breakpoint> breakpoints)
		{
			lock(_structureLock) {
				_breakpoints.AddRange(breakpoints);
			}
			RefreshBreakpoints();
		}

		public static void RemoveBreakpoint(Breakpoint bp)
		{
			bool removed;
			lock(_structureLock) {
				removed = _breakpoints.Remove(bp);
			}
			if(removed) {
				DebugWorkspaceManager.AutoSave();
			}
			RefreshBreakpoints(bp);
		}

		public static void RemoveBreakpoints(IEnumerable<Breakpoint> breakpoints)
		{
			lock(_structureLock) {
				foreach(Breakpoint bp in breakpoints) {
					_breakpoints.Remove(bp);
				}
			}
			RefreshBreakpoints(null);
		}

		public static void AddBreakpoint(Breakpoint bp)
		{
			bool added;
			lock(_structureLock) {
				added = !_breakpoints.Contains(bp);
				if(added) {
					_breakpoints.Add(bp);
				}
			}
			if(added) {
				DebugWorkspaceManager.AutoSave();
			}
			RefreshBreakpoints(bp);
		}

		public static void AddBreakpoint(AddressInfo addr, CpuType cpuType)
		{
			if(BreakpointManager.GetMatchingBreakpoint(addr, cpuType) == null) {
				Breakpoint bp = new Breakpoint() {
					StartAddress = (uint)addr.Address,
					EndAddress = (uint)addr.Address,
					MemoryType = addr.Type,
					CpuType = cpuType,
					BreakOnExec = true,
					BreakOnWrite = true,
					BreakOnRead = true
				};

				BreakpointManager.AddBreakpoint(bp);
			}
		}

		public static void AddTemporaryBreakpoint(Breakpoint bp)
		{
			lock(_structureLock) {
				_temporaryBreakpoints.Add(bp);
			}
			SetBreakpoints();
		}

		public static void ClearTemporaryBreakpoints()
		{
			bool cleared;
			lock(_structureLock) {
				cleared = _temporaryBreakpoints.Count > 0;
				if(cleared) {
					_temporaryBreakpoints.Clear();
				}
			}
			if(cleared) {
				SetBreakpoints();
			}
		}

		private static Breakpoint? GetMatchingBreakpoint(AddressInfo info, CpuType cpuType, Func<Breakpoint, bool> predicate)
		{
			Breakpoint? bp = Breakpoints.Where((bp) => predicate(bp) && bp.Matches((UInt32)info.Address, info.Type, cpuType)).FirstOrDefault();

			if(bp == null) {
				AddressInfo altAddr;
				if(info.Type.IsRelativeMemory()) {
					altAddr = DebugApi.GetAbsoluteAddress(info);
				} else {
					altAddr = DebugApi.GetRelativeAddress(info, cpuType);
				}

				if(altAddr.Address >= 0) {
					bp = Breakpoints.Where((bp) => predicate(bp) && bp.Matches((UInt32)altAddr.Address, altAddr.Type, cpuType)).FirstOrDefault();
				}
			}

			return bp;
		}

		public static Breakpoint? GetMatchingBreakpoint(AddressInfo info, CpuType cpuType, bool ignoreRangedRwBp = false)
		{
			return GetMatchingBreakpoint(info, cpuType, (bp) => !ignoreRangedRwBp || bp.IsSingleAddress || bp.BreakOnExec);
		}

		public static Breakpoint? GetMatchingForbidBreakpoint(AddressInfo info, CpuType cpuType)
		{
			return GetMatchingBreakpoint(info, cpuType, (bp) => bp.Forbid);
		}

		public static Breakpoint? GetMatchingBreakpoint(UInt32 startAddress, UInt32 endAddress, MemoryType memoryType)
		{
			return Breakpoints.Where((bp) =>
					bp.MemoryType == memoryType &&
					bp.StartAddress == startAddress && bp.EndAddress == endAddress
				).FirstOrDefault();
		}

		public static bool EnableDisableBreakpoint(AddressInfo info, CpuType cpuType)
		{
			Breakpoint? breakpoint = BreakpointManager.GetMatchingBreakpoint(info, cpuType);
			if(breakpoint != null) {
				breakpoint.Enabled = !breakpoint.Enabled;
				DebugWorkspaceManager.AutoSave();
				RefreshBreakpoints();
				return true;
			}
			return false;
		}

		public static void ToggleBreakpoint(AddressInfo info, CpuType cpuType)
		{
			if(info.Address < 0) {
				return;
			}

			Breakpoint? breakpoint = BreakpointManager.GetMatchingForbidBreakpoint(info, cpuType) ?? BreakpointManager.GetMatchingBreakpoint(info, cpuType, true);
			if(breakpoint != null) {
				BreakpointManager.RemoveBreakpoint(breakpoint);
			} else {
				bool execBreakpoint = true;
				bool readWriteBreakpoint = !info.Type.IsRomMemory() || info.Type.IsRelativeMemory();
				if(info.Type.SupportsCdl()) {
					CdlFlags cdlData = DebugApi.GetCdlData((uint)info.Address, 1, info.Type)[0];
					bool isCode = cdlData.HasFlag(CdlFlags.Code);
					bool isData = cdlData.HasFlag(CdlFlags.Data);
					if(isCode || isData) {
						readWriteBreakpoint = !isCode;
						execBreakpoint = isCode;
					}
				}

				breakpoint = new Breakpoint() {
					CpuType = cpuType,
					Enabled = true,
					BreakOnExec = execBreakpoint,
					BreakOnRead = readWriteBreakpoint,
					BreakOnWrite = readWriteBreakpoint,
					StartAddress = (UInt32)info.Address,
					EndAddress = (UInt32)info.Address
				};

				breakpoint.MemoryType = info.Type;
				BreakpointManager.AddBreakpoint(breakpoint);
			}
		}

		public static void ToggleForbidBreakpoint(AddressInfo addr, CpuType cpuType)
		{
			if(addr.Address < 0) {
				return;
			}

			Breakpoint? breakpoint = GetMatchingForbidBreakpoint(addr, cpuType);
			if(breakpoint != null) {
				BreakpointManager.RemoveBreakpoint(breakpoint);
			} else {
				breakpoint = new Breakpoint() {
					CpuType = cpuType,
					Enabled = true,
					Forbid = true,
					StartAddress = (UInt32)addr.Address,
					EndAddress = (UInt32)addr.Address
				};
				breakpoint.MemoryType = addr.Type;
				BreakpointManager.AddBreakpoint(breakpoint);
			}
		}

		public static void SetBreakpoints()
		{
			//Serialize the entire pipeline (snapshot through the native call) so concurrent
			//invocations cannot send an older snapshot to the core after a newer one.
			lock(_setBreakpointsLock) {
				List<InteropBreakpoint> breakpoints = new List<InteropBreakpoint>();

				//Snapshot the UI-side structural state under the structure lock. The active cpu
				//types are captured together with the lists so the enumeration is consistent.
				HashSet<CpuType> activeCpuTypes;
				List<Breakpoint> uiBreakpoints;
				List<Breakpoint> asserts;
				List<Breakpoint> temporaryBreakpoints;
				lock(_structureLock) {
					activeCpuTypes = new HashSet<CpuType>(_activeCpuTypes);
					uiBreakpoints = new List<Breakpoint>(_breakpoints);
					asserts = new List<Breakpoint>(_asserts);
					temporaryBreakpoints = new List<Breakpoint>(_temporaryBreakpoints);
				}

				int id = 0;
				void toInteropBreakpoints(IEnumerable<Breakpoint> bpList)
				{
					foreach(Breakpoint bp in bpList) {
						if(activeCpuTypes.Contains(bp.CpuType)) {
							breakpoints.Add(bp.ToInteropBreakpoint(id));
						}
						id++;
					}
				}

				toInteropBreakpoints(uiBreakpoints);
				toInteropBreakpoints(asserts);
				toInteropBreakpoints(temporaryBreakpoints);

				Dictionary<int, int> coreToApi;
				lock(_externalLock) {
					//External breakpoints are managed independently of the UI's active cpu types,
					//so they are always sent to the core (the core only applies them to cpu types
					//that exist in the currently loaded game).
					//Build an explicit core id -> api id map so event.break notifications can be
					//resolved back to the API-stable id without relying on dictionary enumeration order.
					coreToApi = new Dictionary<int, int>();
					foreach(KeyValuePair<int, Breakpoint> kvp in BreakpointManager._externalBreakpoints) {
						coreToApi[id] = kvp.Key;
						breakpoints.Add(kvp.Value.ToInteropBreakpoint(id));
						id++;
					}
					BreakpointManager._coreToApiId = coreToApi;
				}

				//Call the native core outside the structural/external locks (never while holding
				//either of those), but still inside the SetBreakpoints lock.
				DebugApi.SetBreakpoints(breakpoints.ToArray(), (UInt32)breakpoints.Count);
			}
		}

		public static Breakpoint? GetBreakpointById(int breakpointId)
		{
			if(breakpointId < 0) {
				return null;
			}

			lock(_setBreakpointsLock) {
				lock(_structureLock) {
					if(breakpointId < _breakpoints.Count) {
						return _breakpoints[breakpointId];
					} else if(breakpointId < _breakpoints.Count + _asserts.Count) {
						return _asserts[breakpointId - _breakpoints.Count];
					} else if(breakpointId < _breakpoints.Count + _asserts.Count + _temporaryBreakpoints.Count) {
						return _temporaryBreakpoints[breakpointId - _breakpoints.Count - _asserts.Count];
					}
				}

				lock(_externalLock) {
					if(_coreToApiId.TryGetValue(breakpointId, out int apiId) && _externalBreakpoints.TryGetValue(apiId, out Breakpoint? externalBp)) {
						return externalBp;
					}
				}
			}
			return null;
		}

		/// <summary>
		/// Resolves a core-assigned breakpoint id to the API-stable external breakpoint id,
		/// or returns null if the given core id does not belong to an external breakpoint
		/// (e.g. it is a UI/assert/temporary breakpoint).
		/// </summary>
		public static int? GetExternalApiBreakpointId(int coreBreakpointId)
		{
			lock(_setBreakpointsLock) {
				lock(_externalLock) {
					if(_coreToApiId.TryGetValue(coreBreakpointId, out int apiId)) {
						return apiId;
					}
				}
			}
			return null;
		}

		public static int AddExternalBreakpoint(Breakpoint bp)
		{
			int apiId;
			lock(_externalLock) {
				apiId = _externalBreakpointIdCounter++;
				_externalBreakpoints[apiId] = bp;
			}
			SetBreakpoints();
			return apiId;
		}

		public static bool RemoveExternalBreakpoint(int apiId)
		{
			bool removed;
			lock(_externalLock) {
				removed = _externalBreakpoints.Remove(apiId);
			}
			if(removed) {
				SetBreakpoints();
			}
			return removed;
		}

		public static void ClearExternalBreakpoints()
		{
			bool cleared;
			lock(_externalLock) {
				cleared = _externalBreakpoints.Count > 0;
				_externalBreakpoints.Clear();
			}
			if(cleared) {
				SetBreakpoints();
			}
		}

		public static List<KeyValuePair<int, Breakpoint>> GetExternalBreakpoints()
		{
			lock(_externalLock) {
				return _externalBreakpoints.ToList();
			}
		}
	}
}
