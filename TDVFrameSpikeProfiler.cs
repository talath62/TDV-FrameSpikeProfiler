using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TDV.FrameSpikeProfiler
{
    // Diagnostic build: extensive managed-method profiling for isolated test servers.
    // Stopwatch includes descheduling/GC pauses occurring INSIDE instrumented calls.
    [BepInPlugin(PluginGuid, "TDV Frame Spike Profiler", PluginVersion)]
    public sealed class TDVFrameSpikeProfiler : BaseUnityPlugin
    {
        public const string PluginGuid = "tdv.frame-spike-profiler";
        public const string PluginVersion = "1.2.0";
        private const int MaximumMethods = 30000;
        private const int FrameRing = 8;
        private const int MaxPatchErrorLogs = 12;

        // Server methods patched first, even if the automatic limit is reached.
        private const string CoreMethods =
            "ZNet.Update;ZDOMan.Update;ZDOMan.SendZDOToPeers2;" +
            "ZDOMan.SendZDOs;ZDOMan.CreateSyncList;ZDOMan.ServerSortSendZDOS;" +
            "ZNetScene.Update;ZNetScene.CreateDestroyObjects;ZNetScene.CreateObjects;" +
            "ZNetScene.RemoveObjects;ZoneSystem.Update;ZoneSystem.PokeLocalZone;" +
            "Game.Update;ZNet.SaveWorld;ZNet.SaveWorldThread;" +
            "ZDOMan.PrepareSave;ZDOMan.Save;ZRoutedRpc.RouteRPC;" +
            "ZNet.RPC_RoutedRPC;ZNet.RPC_PeerInfo";

        private struct CallState
        {
            internal bool Valid;
            internal int Slot;
            internal int Frame;
            internal long Started;
            internal int Depth;
            internal int ThreadId;
        }

        private struct ActiveCall
        {
            internal int Slot;
            internal long Started;
            internal long ChildTicks;
        }

        private sealed class FrameData
        {
            internal int Frame = int.MinValue;
            internal readonly long[] Inclusive = new long[MaximumMethods];
            internal readonly long[] Exclusive = new long[MaximumMethods];
            internal readonly long[] MaxInclusive = new long[MaximumMethods];
            internal readonly long[] MaxExclusive = new long[MaximumMethods];
            internal readonly int[] Calls = new int[MaximumMethods];
            internal readonly int[] ParentForMax = new int[MaximumMethods];
            internal readonly List<int> Touched = new List<int>(512);

            internal void Add(int frame, int slot, long inclusive, long exclusive, int parentSlot)
            {
                if (Frame != frame)
                {
                    foreach (int previous in Touched)
                    {
                        Inclusive[previous] = 0;
                        Exclusive[previous] = 0;
                        MaxInclusive[previous] = 0;
                        MaxExclusive[previous] = 0;
                        Calls[previous] = 0;
                        ParentForMax[previous] = -1;
                    }
                    Touched.Clear();
                    Frame = frame;
                }
                if (Calls[slot]++ == 0) Touched.Add(slot);
                Inclusive[slot] += inclusive;
                Exclusive[slot] += exclusive;
                if (inclusive > MaxInclusive[slot]) MaxInclusive[slot] = inclusive;
                if (exclusive > MaxExclusive[slot])
                {
                    MaxExclusive[slot] = exclusive;
                    ParentForMax[slot] = parentSlot;
                }
            }
        }

        private struct MethodHit
        {
            internal int Slot;
            internal long Inclusive, Exclusive, MaxInclusive, MaxExclusive;
            internal int Calls;
            internal int ParentSlot;
        }

        [ThreadStatic] private static List<ActiveCall> _threadCalls;
        private static readonly Dictionary<MethodBase, int> Slots = new Dictionary<MethodBase, int>();
        private static readonly string[] Names = new string[MaximumMethods];
        private static TDVFrameSpikeProfiler _owner;

        // These arrays are modified by multiple worker threads, using Interlocked.
        private static readonly long[] WorkerInclusive = new long[MaximumMethods];
        private static readonly long[] WorkerExclusive = new long[MaximumMethods];
        private static readonly int[] WorkerCalls = new int[MaximumMethods];

        private readonly FrameData[] _records = new FrameData[FrameRing];
        private Harmony _harmony;
        private ConfigEntry<bool> _enabled, _dedicatedOnly, _includeGame, _includePlugins;
        private ConfigEntry<bool> _includeModDependencies, _includeSpecialMethods, _includeConstructors;
        private ConfigEntry<bool> _captureWorkers, _watchdogEnabled;
        private ConfigEntry<int> _maxMethods, _top, _watchdogIntervalMs;
        private ConfigEntry<float> _threshold, _logSpacing;
        private ConfigEntry<string> _extra, _assemblies, _rankBy;

        private int _mainThread, _patched, _failed, _skipped, _candidateCount;
        private int _lastFrame = -1, _lastG0, _lastG1, _lastG2, _suppressed;
        private long _lastUpdateTicks, _lastLogTicks;
        private volatile bool _collect;
        private bool _patchCapReached;
        private long _lastFrameBeat, _watchdogWorstTicks;
        private int _liveSlot = -1, _liveDepth, _watchdogWorstSlot = -1, _watchdogWorstDepth;
        private Thread _watchdog;
        private volatile bool _watchdogStop;

        private void Awake()
        {
            _enabled = Config.Bind("Profiler", "Enabled", true, "Enable method timing; restart after changing.");
            _dedicatedOnly = Config.Bind("Profiler", "DedicatedBatchModeOnly", true,
                "Do not profile a graphical client.");
            _threshold = Config.Bind("Profiler", "SpikeThresholdMs", 100f,
                new ConfigDescription("Report when a frame exceeds this duration.",
                    new AcceptableValueRange<float>(25f, 5000f)));
            _logSpacing = Config.Bind("Profiler", "MinimumLogSpacingSeconds", 1f,
                new ConfigDescription("Maximum rate of spike reports.",
                    new AcceptableValueRange<float>(0f, 60f)));
            _top = Config.Bind("Profiler", "TopMethods", 10,
                new ConfigDescription("Number of methods shown for the main thread.",
                    new AcceptableValueRange<int>(1, 50)));
            _rankBy = Config.Bind("Profiler", "RankBy", "ExclusiveMax",
                "ExclusiveMax, InclusiveMax, ExclusiveTotal or InclusiveTotal.");
            _extra = Config.Bind("Profiler", "ExtraMethods", "",
                "Always instrument these Type.Method requests, separated by semicolons.");
            _maxMethods = Config.Bind("AggressiveInstrumentation", "MaxMethods", 18000,
                new ConfigDescription("Maximum Harmony patches (test only). Absolute limit 30000.",
                    new AcceptableValueRange<int>(20, MaximumMethods)));
            _includeGame = Config.Bind("AggressiveInstrumentation", "IncludeValheim", true,
                "Instrument eligible methods in assembly_valheim and Assembly-CSharp.");
            _includePlugins = Config.Bind("AggressiveInstrumentation", "IncludeBepInExPlugins", true,
                "Instrument assemblies hosting BepInEx plugins.");
            _includeModDependencies = Config.Bind("AggressiveInstrumentation", "IncludePluginFolderAssemblies", true,
                "Include loaded assemblies under BepInEx/plugins, even if not plugin entry-point assemblies.");
            _assemblies = Config.Bind("AggressiveInstrumentation", "AdditionalAssemblyNames", "",
                "Additional exact assembly names or prefix* patterns, semicolon-separated.");
            _includeSpecialMethods = Config.Bind("AggressiveInstrumentation", "IncludeAccessorsAndGeneratedMethods", true,
                "Includes getters/setters, compiler-generated lambdas and coroutine MoveNext methods.");
            _includeConstructors = Config.Bind("AggressiveInstrumentation", "IncludeInstanceConstructors", true,
                "Include instance .ctor methods; static constructors are always excluded.");
            _captureWorkers = Config.Bind("Profiler", "IncludeBackgroundThreads", true,
                "Report separately methods executed by worker threads since the last spike report.");
            _watchdogEnabled = Config.Bind("Watchdog", "Enabled", true,
                "Auxiliary sampler: records the active instrumented method during a long gap between Updates.");
            _watchdogIntervalMs = Config.Bind("Watchdog", "IntervalMs", 20,
                new ConfigDescription("Sampling interval on a background thread (not native stack sampling).",
                    new AcceptableValueRange<int>(10, 200)));

            if (!_enabled.Value || (_dedicatedOnly.Value && !Application.isBatchMode))
            {
                Logger.LogInfo("[TDVFrame] Disabled or graphical client: no instrumentation.");
                return;
            }
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            for (int i = 0; i < FrameRing; i++) _records[i] = new FrameData();
            _owner = this;
            _harmony = new Harmony(PluginGuid);
        }

        private void Start()
        {
            if (_harmony == null) return;
            _collect = false;
            var prefix = new HarmonyMethod(typeof(TDVFrameSpikeProfiler), nameof(Enter));
            var finalizer = new HarmonyMethod(typeof(TDVFrameSpikeProfiler), nameof(Leave));
            var seen = new HashSet<MethodBase>();
            var installTimer = Stopwatch.StartNew();
            try
            {
                AddExplicit(CoreMethods + ";" + _extra.Value, prefix, finalizer, seen);
                var assemblies = FindAssemblies();
                // Every eligible IL method, including generated methods, is a candidate.
                // Order matters when a cap is hit: TDV first, game second, other mods third.
                foreach (Assembly assembly in assemblies)
                {
                    if (_patchCapReached) break;
                    int previous = _patched;
                    Scan(assembly, prefix, finalizer, seen);
                    Logger.LogInfo("[TDVFrame] " + assembly.GetName().Name + ": +" +
                        (_patched - previous) + " patches (total=" + _patched +
                        ", candidates=" + _candidateCount + ")");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("[TDVFrame] Discovery failure: " + ex);
            }
            installTimer.Stop();
            _lastUpdateTicks = Stopwatch.GetTimestamp();
            _lastFrameBeat = _lastUpdateTicks;
            _collect = true;
            Logger.LogWarning("[TDVFrame] TEST PROFILER v" + PluginVersion + " ready | patched=" +
                _patched + " | candidates=" + _candidateCount + " | failed=" + _failed +
                " | ineligible=" + _skipped + " | cap=" + _maxMethods.Value +
                " | install=" + installTimer.Elapsed.TotalSeconds.ToString("F1") + "s" +
                (_patchCapReached ? " | WARNING: CAP REACHED, SOME METHODS NOT TRACED" : ""));
            if (_watchdogEnabled.Value)
            {
                _watchdogStop = false;
                _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "TDVFrame-Watchdog" };
                _watchdog.Start();
            }
        }

        private Assembly[] FindAssemblies()
        {
            var chosen = new HashSet<Assembly>();
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly asm in all)
            {
                if (asm == null || asm.IsDynamic || asm == typeof(TDVFrameSpikeProfiler).Assembly) continue;
                string name;
                try { name = asm.GetName().Name; } catch { continue; }
                if (_includeGame.Value && (name.Equals("assembly_valheim", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase))) chosen.Add(asm);
                if (_includeModDependencies.Value)
                {
                    try
                    {
                        string path = asm.Location.Replace('\\', '/');
                        if (path.IndexOf("/BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) >= 0)
                            chosen.Add(asm);
                    }
                    catch { /* dynamic or location-free assembly */ }
                }
                foreach (string pattern in (_assemblies.Value ?? "").Split(';'))
                    if (MatchName(name, pattern.Trim())) chosen.Add(asm);
            }
            if (_includePlugins.Value)
            {
                try
                {
                    foreach (var info in Chainloader.PluginInfos.Values)
                        if (info != null && info.Instance != null)
                            chosen.Add(info.Instance.GetType().Assembly);
                }
                catch (Exception ex) { Logger.LogWarning("[TDVFrame] Plugin scan: " + ex.Message); }
            }
            chosen.Remove(typeof(TDVFrameSpikeProfiler).Assembly);
            return chosen.OrderBy(a => AssemblyPriority(a.GetName().Name))
                .ThenBy(a => a.GetName().Name, StringComparer.Ordinal).ToArray();
        }

        private static int AssemblyPriority(string name)
        {
            if (name.IndexOf("Viking", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("TDV", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (name.Equals("assembly_valheim", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }

        private static bool MatchName(string name, string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern == "*") return false;
            return pattern.EndsWith("*", StringComparison.Ordinal)
                ? name.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase)
                : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
        }

        private void AddExplicit(string input, HarmonyMethod prefix, HarmonyMethod finalizer,
            HashSet<MethodBase> seen)
        {
            foreach (string raw in input.Split(';'))
            {
                string request = raw.Trim();
                int dot = request.LastIndexOf('.');
                if (dot <= 0) continue;
                Type type = AccessTools.TypeByName(request.Substring(0, dot));
                if (type == null) continue;
                MethodInfo[] methods;
                try { methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (MethodInfo method in methods)
                    if (method.Name == request.Substring(dot + 1) && Eligible(method))
                        Patch(method, prefix, finalizer, seen);
            }
        }

        private void Scan(Assembly assembly, HarmonyMethod prefix,
            HarmonyMethod finalizer, HashSet<MethodBase> seen)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch (Exception ex)
            {
                Logger.LogWarning("[TDVFrame] Cannot scan " + assembly.GetName().Name + ": " + ex.Message);
                return;
            }
            foreach (Type type in types)
            {
                if (type == null || type.IsInterface || type.ContainsGenericParameters) continue;
                MethodBase[] methods;
                try
                {
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                    var list = new List<MethodBase>(type.GetMethods(flags));
                    if (_includeConstructors.Value) list.AddRange(type.GetConstructors(flags));
                    methods = list.ToArray();
                }
                catch { continue; }
                foreach (MethodBase method in methods)
                {
                    if (!Eligible(method)) { _skipped++; continue; }
                    if (seen.Contains(method)) continue;
                    _candidateCount++;
                    if (!Patch(method, prefix, finalizer, seen))
                    {
                        if (_patchCapReached) return;
                    }
                }
            }
        }

        private bool Eligible(MethodBase method)
        {
            if (method == null || method.IsAbstract || method.ContainsGenericParameters ||
                method.DeclaringType == null || method.DeclaringType.ContainsGenericParameters)
                return false;
            if (method is ConstructorInfo && (method.IsStatic || !_includeConstructors.Value))
                return false;
            if (!_includeSpecialMethods.Value &&
                (method.IsSpecialName || method.Name.StartsWith("<", StringComparison.Ordinal) ||
                 method.Name == "MoveNext")) return false;
            if (method is MethodInfo mi && mi.ReturnType.IsByRef) return false;
            try { return method.GetMethodBody() != null; }
            catch { return false; }
        }

        private bool Patch(MethodBase method, HarmonyMethod prefix,
            HarmonyMethod finalizer, HashSet<MethodBase> seen)
        {
            if (_patched >= _maxMethods.Value || _patched >= MaximumMethods)
            { _patchCapReached = true; return false; }
            if (!seen.Add(method)) return false;
            try
            {
                // No transpiler and no game logic changes. Finalizer preserves exceptions.
                _harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                Slots.Add(method, _patched);
                Names[_patched] = Describe(method);
                _patched++;
                if ((_patched % 2000) == 0)
                    Logger.LogInfo("[TDVFrame] Instrumented " + _patched + " methods...");
                return true;
            }
            catch (Exception ex)
            {
                _failed++;
                if (_failed <= MaxPatchErrorLogs)
                    Logger.LogWarning("[TDVFrame] Cannot patch " + Describe(method) +
                        " | " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static string Describe(MethodBase method)
        {
            string type = method.DeclaringType != null ? method.DeclaringType.FullName : "?";
            return type + "." + method.Name + "(" + method.GetParameters().Length + " args)";
        }

        // __state is method-local and safe for recursion. A thread-local stack tracks nested
        // instrumented calls, allowing exclusive times without subtracting other threads.
        private static void Enter(MethodBase __originalMethod, out CallState __state)
        {
            __state = default(CallState);
            TDVFrameSpikeProfiler p = _owner;
            if (p == null || !p._collect || !p._enabled.Value) return;
            int threadId = Thread.CurrentThread.ManagedThreadId;
            if (threadId != p._mainThread && !p._captureWorkers.Value) return;
            int slot;
            if (!Slots.TryGetValue(__originalMethod, out slot)) return;
            int frame = threadId == p._mainThread ? Time.frameCount : -1;
            long started = Stopwatch.GetTimestamp();
            List<ActiveCall> stack = _threadCalls;
            if (stack == null) _threadCalls = stack = new List<ActiveCall>(24);
            stack.Add(new ActiveCall { Slot = slot, Started = started, ChildTicks = 0 });
            __state = new CallState
            {
                Valid = true, Slot = slot, Frame = frame,
                Started = started, Depth = stack.Count, ThreadId = threadId
            };
            if (threadId == p._mainThread)
            {
                Volatile.Write(ref p._liveSlot, slot);
                Volatile.Write(ref p._liveDepth, stack.Count);
            }
        }

        private static Exception Leave(Exception __exception, CallState __state)
        {
            if (!__state.Valid) return __exception;
            TDVFrameSpikeProfiler p = _owner;
            if (p == null) return __exception;
            long now = Stopwatch.GetTimestamp();
            List<ActiveCall> stack = _threadCalls;
            if (stack == null || stack.Count != __state.Depth ||
                stack[stack.Count - 1].Slot != __state.Slot)
            {
                // Never change the original exception or crash gameplay because profiling
                // started/stopped in an unusual nested Harmony invocation.
                if (stack != null) stack.Clear();
                if (__state.ThreadId == p._mainThread) Volatile.Write(ref p._liveSlot, -1);
                return __exception;
            }
            int last = stack.Count - 1;
            ActiveCall active = stack[last];
            stack.RemoveAt(last);
            long inclusive = Math.Max(0, now - active.Started);
            long exclusive = Math.Max(0, inclusive - active.ChildTicks);
            int parentSlot = last > 0 ? stack[last - 1].Slot : -1;
            if (last != 0)
            {
                ActiveCall parent = stack[last - 1];
                parent.ChildTicks += inclusive;
                stack[last - 1] = parent;
            }
            if (__state.ThreadId == p._mainThread)
            {
                Volatile.Write(ref p._liveSlot, last != 0 ? stack[last - 1].Slot : -1);
                Volatile.Write(ref p._liveDepth, last);
                if (__state.Frame >= p._lastFrame - FrameRing + 2)
                    p._records[(__state.Frame & int.MaxValue) % FrameRing].Add(
                        __state.Frame, __state.Slot, inclusive, exclusive, parentSlot);
            }
            else
            {
                Interlocked.Add(ref WorkerInclusive[__state.Slot], inclusive);
                Interlocked.Add(ref WorkerExclusive[__state.Slot], exclusive);
                Interlocked.Increment(ref WorkerCalls[__state.Slot]);
            }
            return __exception;
        }

        private void Update()
        {
            if (!_collect || !_enabled.Value) return;
            int currentFrame = Time.frameCount;
            if (currentFrame == _lastFrame) return;
            long now = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _lastFrameBeat, now);
            double intervalMs = _lastUpdateTicks > 0
                ? (now - _lastUpdateTicks) * 1000.0 / Stopwatch.Frequency : 0;
            _lastUpdateTicks = now;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            if (_lastFrame >= 0 && currentFrame == _lastFrame + 1)
            {
                // unscaledDeltaTime refers to the preceding frame, as does our record.
                double frameMs = Time.unscaledDeltaTime * 1000.0;
                if (!double.IsNaN(frameMs) && frameMs >= _threshold.Value)
                {
                    if (_lastLogTicks == 0 ||
                        (now - _lastLogTicks) * 1.0 / Stopwatch.Frequency >= _logSpacing.Value)
                    {
                        _lastLogTicks = now;
                        LogSpike(_lastFrame, frameMs, intervalMs,
                            Math.Max(0, g0 - _lastG0), Math.Max(0, g1 - _lastG1),
                            Math.Max(0, g2 - _lastG2));
                        _suppressed = 0;
                    }
                    else _suppressed++;
                }
            }
            _lastFrame = currentFrame;
            _lastG0 = g0; _lastG1 = g1; _lastG2 = g2;
        }

        private void WatchdogLoop()
        {
            while (!_watchdogStop)
            {
                Thread.Sleep(_watchdogIntervalMs.Value);
                if (!_collect) continue;
                long beat = Interlocked.Read(ref _lastFrameBeat);
                long now = Stopwatch.GetTimestamp();
                long elapsed = now - beat;
                long limit = (long)(_threshold.Value * Stopwatch.Frequency / 1000.0);
                if (beat == 0 || elapsed < limit) continue;
                long previous = Interlocked.Read(ref _watchdogWorstTicks);
                if (elapsed > previous)
                {
                    Interlocked.Exchange(ref _watchdogWorstTicks, elapsed);
                    Volatile.Write(ref _watchdogWorstSlot, Volatile.Read(ref _liveSlot));
                    Volatile.Write(ref _watchdogWorstDepth, Volatile.Read(ref _liveDepth));
                }
            }
        }

        private static long RankValue(MethodHit m, string order)
        {
            switch (order)
            {
                case "InclusiveMax": return m.MaxInclusive;
                case "ExclusiveTotal": return m.Exclusive;
                case "InclusiveTotal": return m.Inclusive;
                default: return m.MaxExclusive;
            }
        }

        private static double Millis(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private void LogSpike(int frame, double frameMs, double intervalMs,
            int gc0, int gc1, int gc2)
        {
            FrameData data = _records[(frame & int.MaxValue) % FrameRing];
            var hits = new List<MethodHit>(256);
            long measuredSelf = 0;
            if (data.Frame == frame)
            {
                foreach (int slot in data.Touched)
                {
                    measuredSelf += data.Exclusive[slot];
                    hits.Add(new MethodHit
                    {
                        Slot = slot, Inclusive = data.Inclusive[slot],
                        Exclusive = data.Exclusive[slot], MaxInclusive = data.MaxInclusive[slot],
                        MaxExclusive = data.MaxExclusive[slot], Calls = data.Calls[slot],
                        ParentSlot = data.ParentForMax[slot]
                    });
                }
            }
            string order = _rankBy.Value ?? "ExclusiveMax";
            hits.Sort((a, b) => RankValue(b, order).CompareTo(RankValue(a, order)));
            var sb = new StringBuilder(2000);
            sb.Append("[TDVFrame] SPIKE ").Append(frameMs.ToString("F1"))
                .Append(" ms | interUpdate=").Append(intervalMs.ToString("F1"))
                .Append(" ms | frame=").Append(frame)
                .Append(" | GC(0/1/2)=").Append(gc0).Append('/').Append(gc1).Append('/').Append(gc2)
                .Append(" | managed=").Append((GC.GetTotalMemory(false) / 1048576.0).ToString("F1"))
                .Append(" MiB | patches=").Append(_patched)
                .Append(" | activeMethods=").Append(hits.Count)
                .Append(" | measuredSelf~=").Append(Millis(measuredSelf).ToString("F2"))
                .Append(" ms | rank=").Append(order);
            if (_patchCapReached) sb.Append(" | INCOMPLETE-CAP");
            if (_suppressed > 0) sb.Append(" | earlier-suppressed=").Append(_suppressed);
            int display = Math.Min(_top.Value, hits.Count);
            if (display == 0) sb.Append("\n  No patched main-thread methods completed in this frame.");
            for (int i = 0; i < display; i++)
            {
                MethodHit hit = hits[i];
                sb.Append("\n  ").Append(i + 1).Append(". ").Append(Names[hit.Slot])
                    .Append(" | selfMax=").Append(Millis(hit.MaxExclusive).ToString("F3"))
                    .Append(" ms | selfTotal=").Append(Millis(hit.Exclusive).ToString("F3"))
                    .Append(" ms | inclMax=").Append(Millis(hit.MaxInclusive).ToString("F3"))
                    .Append(" ms | inclTotal=").Append(Millis(hit.Inclusive).ToString("F3"))
                    .Append(" ms | calls=").Append(hit.Calls);
                if (hit.ParentSlot >= 0 && hit.ParentSlot < _patched)
                    sb.Append(" | calledBy=").Append(Names[hit.ParentSlot]);
            }
            long worst = Interlocked.Exchange(ref _watchdogWorstTicks, 0);
            int observedSlot = Interlocked.Exchange(ref _watchdogWorstSlot, -1);
            int observedDepth = Interlocked.Exchange(ref _watchdogWorstDepth, 0);
            if (_watchdogEnabled.Value)
                sb.Append("\n  Watchdog: longest-gap-observed=").Append(Millis(worst).ToString("F1"))
                    .Append(" ms | active-leaf=")
                    .Append(observedSlot >= 0 && observedSlot < _patched ? Names[observedSlot] : "none/uninstrumented")
                    .Append(" | depth=").Append(observedDepth)
                    .Append(" (sampled, not a native stack trace)");
            if (_captureWorkers.Value)
                AppendWorkers(sb);
            sb.Append("\n  Caveat: self subtracts instrumented children only; inclusive and exclusive are wall-clock, " +
                "and can contain GC/OS pauses. Native Unity, JIT, unpatched/inlined methods and sleeping " +
                "between callbacks cannot be identified by Harmony.");
            Logger.LogWarning(sb.ToString());
        }

        private void AppendWorkers(StringBuilder sb)
        {
            var hits = new List<MethodHit>(32);
            for (int i = 0; i < _patched; i++)
            {
                int calls = Interlocked.Exchange(ref WorkerCalls[i], 0);
                if (calls <= 0) continue;
                long inc = Interlocked.Exchange(ref WorkerInclusive[i], 0);
                long exc = Interlocked.Exchange(ref WorkerExclusive[i], 0);
                hits.Add(new MethodHit { Slot = i, Inclusive = inc, Exclusive = exc, Calls = calls });
            }
            hits.Sort((a, b) => b.Exclusive.CompareTo(a.Exclusive));
            if (hits.Count == 0) return;
            sb.Append("\n  BACKGROUND since last report (NOT attributable to this frame):");
            for (int i = 0; i < Math.Min(5, hits.Count); i++)
                sb.Append("\n    ").Append(Names[hits[i].Slot])
                    .Append(" | selfTotal=").Append(Millis(hits[i].Exclusive).ToString("F2"))
                    .Append(" ms | inclusiveTotal=").Append(Millis(hits[i].Inclusive).ToString("F2"))
                    .Append(" ms | calls=").Append(hits[i].Calls);
        }

        private void OnDestroy()
        {
            _collect = false;
            _watchdogStop = true;
            if (_watchdog != null && _watchdog.IsAlive) _watchdog.Join(250);
            if (ReferenceEquals(_owner, this)) _owner = null;
            if (_harmony != null) _harmony.UnpatchSelf();
            Slots.Clear();
        }
    }
}
