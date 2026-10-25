using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LiteMonitor
{
    class GpuInfo
    {
        public string Name, Luid; public bool IsNvml; public IntPtr NvHandle; public uint Vendor;
        public double Util, MemCtrl, Temp = -1, PowerW = -1, PowerLimitW = -1, Fan = -1, ClockCore = -1, ClockMem = -1, Enc = -1, Dec = -1;
        public ulong MemUsed, MemTotal, SharedUsed, SharedTotal;
        public Series Hist = new Series();
        public List<GpuProc> Procs = new List<GpuProc>();
        // Task Manager-style engines
        public readonly List<EngineSlot> Engines = new();
        public int EngVersion; public double EngineMax; public bool EngSampled;
        public Series DedHist = new Series(), ShHist = new Series();
        public string DriverVer = "", DriverDate = "", Location = "";
        public EngineSlot FindEngine(string label) { foreach (var e in Engines) if (e.Label == label) return e; return null; }
    }
    class EngineSlot
    {
        public int Phys, Eng; public string Type, Label; public double Acc, Value; public Series Hist;
        public int Watchers; // >0 while a "who uses this engine" popup is open
        public readonly Dictionary<int, PidUse> Pids = new();
    }
    class PidUse { public double Acc, Value; public long LastSeen; }
    class GpuProc { public int Pid; public string Name; public ulong Dedicated, Shared; public double Util; }
    class DiskInfo { public string Inst, Title, Type, Model; public int Index; public double Active, Read, Write; public Series Hist = new Series(), HistR = new Series(), HistW = new Series(); public int Slot; }
    class NetInfo { public string Inst; public double Send, Recv; public bool Seen; public Series HistS = new Series(), HistR = new Series(); public int Slot; }
    class ProcRow { public int Pid; public string Name; public long CpuTime, IoBytes; public long CreateTime; public double Cpu, IoRate, Gpu; public ulong PrivateWs; public int Threads, Handles; public uint Gen; }

    class Series
    {
        public readonly float[] D = new float[120]; public int Head;
        public void Push(double v) { Head = (Head + 1) % D.Length; D[Head] = double.IsNaN(v) ? 0 : (float)v; }
        public float Last => D[Head];
        public float Max() { float m = 0; for (int i = 0; i < D.Length; i++) if (D[i] > m) m = D[i]; return m; }
    }

    sealed unsafe class Sampler : IDisposable
    {
        public readonly int Cores = Environment.ProcessorCount;
        public readonly string CpuName = Native.CpuName();
        public readonly uint BaseMHz = Native.BaseMHz();
        public double CpuTotal, CpuMHz; public double[] CoreUtil; public Series[] CoreHist; public Series CpuHist = new Series();
        public int Procs, Threads, Handles; public TimeSpan Uptime;
        public ulong MemTotal, MemAvail, CommitUsed, CommitLimit, Cached, Paged, NonPaged; public Series MemHist = new Series();
        public readonly List<DiskInfo> Disks = new(); public readonly List<NetInfo> Nets = new(); public readonly List<GpuInfo> Gpus = new();

        readonly PdhQuery sys = new PdhQuery();
        readonly PdhCounter cUtilTotal, cUtil, cPerf, cDiskIdle, cDiskR, cDiskW, cNetS, cNetR;
        readonly PdhQuery gq = new PdhQuery();
        readonly PdhCounter cEngine, cAdDed, cAdShared, cProcDed, cProcShared;
        bool engineEverCollected;
        long lastGpuCollect;
        readonly HashSet<string> seenEng = new();
        bool[] engSkip = new bool[0], engDup = new bool[0];
        readonly HashSet<string> dupCheck = new();
        int[] coreIndexOf = new int[0];
        // parsed GPU-engine instance cache (parallel to counter arrays)
        EngineSlot[] engSlot = new EngineSlot[0]; int[] engPid = new int[0]; string[] engLuid = new string[0]; string[] engType = new string[0];
        int[] pmPid = new int[0]; string[] pmLuid = new string[0];
        readonly Dictionary<string, string> luidIntern = new();
        readonly Dictionary<string, string> typeIntern = new();
        readonly Dictionary<long, double> engSum = new(); // key = pid<<8 | typeIndex... per luid handled separately
        readonly Dictionary<string, Dictionary<string, double>> adapterEng = new();
        public readonly Dictionary<int, double> ProcGpu = new();
        // per adapter per pid: util from engines
        readonly Dictionary<string, Dictionary<int, Dictionary<string, double>>> adPidEng = new();

        public Sampler()
        {
            CoreUtil = new double[Cores]; CoreHist = new Series[Cores];
            for (int i = 0; i < Cores; i++) CoreHist[i] = new Series();
            cUtilTotal = sys.Add(@"\Processor Information(_Total)\% Processor Utility");
            cUtil = sys.Add(@"\Processor Information(*)\% Processor Utility");
            if (cUtilTotal == null) { cUtilTotal = sys.Add(@"\Processor Information(_Total)\% Processor Time"); cUtil = sys.Add(@"\Processor Information(*)\% Processor Time"); }
            cPerf = sys.Add(@"\Processor Information(_Total)\% Processor Performance");
            cDiskIdle = sys.Add(@"\PhysicalDisk(*)\% Idle Time");
            cDiskR = sys.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
            cDiskW = sys.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
            cNetS = sys.Add(@"\Network Interface(*)\Bytes Sent/sec");
            cNetR = sys.Add(@"\Network Interface(*)\Bytes Received/sec");
            cEngine = gq.Add(@"\GPU Engine(*)\Utilization Percentage");
            cAdDed = gq.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
            cAdShared = gq.Add(@"\GPU Adapter Memory(*)\Shared Usage");
            cProcDed = gq.Add(@"\GPU Process Memory(*)\Dedicated Usage");
            cProcShared = gq.Add(@"\GPU Process Memory(*)\Shared Usage");
            try { sys.Collect(); } catch (Exception ex) { Log.Write(ex); }
            try { SetupGpus(); } catch (Exception ex) { Log.Write(ex); }
        }

        void SetupGpus()
        {
            var ads = Dxgi.List();
            int nv = 0;
            try { if (Nvml.Load()) nv = Nvml.Count(); } catch (Exception ex) { Log.Write(ex); }
            int nvUsed = 0;
            foreach (var a in ads)
            {
                if (a.Vendor == 0x1414) continue; // Microsoft Basic Render
                var g = new GpuInfo { Name = a.Name, Luid = a.Luid, Vendor = a.Vendor, MemTotal = a.Dedicated, SharedTotal = a.Shared };
                if (a.Vendor == 0x10DE && nvUsed < nv) { g.NvHandle = Nvml.Handle(nvUsed++); g.IsNvml = g.NvHandle != IntPtr.Zero; }
                Gpus.Add(g);
            }
            foreach (var g in Gpus) try { DriverInfo(g); } catch (Exception ex) { Log.Write(ex); }
            if (Gpus.Count == 0) for (int i = 0; i < nv; i++) { var h = Nvml.Handle(i); Gpus.Add(new GpuInfo { Name = Nvml.Name(h), NvHandle = h, IsNvml = true, Luid = "" }); }
        }

        static void DriverInfo(GpuInfo g)
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !char.IsDigit(sub[0])) continue;
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (!string.Equals((k?.GetValue("DriverDesc") as string)?.Trim(), g.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    g.DriverVer = k.GetValue("DriverVersion") as string ?? "";
                    g.DriverDate = k.GetValue("DriverDate") as string ?? "";
                    return;
                }
                catch { }
            }
        }

        public void Sample(bool needGpuPdh, bool needGpuProcs)
        {
            sys.Collect();
            // CPU
            double t = cUtilTotal?.Read() ?? double.NaN;
            CpuTotal = double.IsNaN(t) ? 0 : Math.Min(100, t);
            CpuHist.Push(CpuTotal);
            double perf = cPerf?.Read() ?? double.NaN;
            CpuMHz = double.IsNaN(perf) ? BaseMHz : perf / 100.0 * BaseMHz;
            if (cUtil != null)
            {
                int n = cUtil.ReadArray(), k = 0;
                for (int i = 0; i < n && k < Cores; i++)
                {
                    string nm = cUtil.Names[i];
                    if (nm.IndexOf("_Total", StringComparison.Ordinal) >= 0) continue;
                    int comma = nm.IndexOf(','), grp = 0, core = k;
                    if (comma > 0 && int.TryParse(nm.AsSpan(0, comma), out grp) && int.TryParse(nm.AsSpan(comma + 1), out core)) core += grp * 64;
                    if (core < 0 || core >= Cores) core = k;
                    CoreUtil[core] = Math.Min(100, cUtil.Values[i]); k++;
                }
                // instances are "group,core"; PDH returns them in order within groups in practice. Sort is cheap enough only when names change.
                for (int i = 0; i < Cores; i++) CoreHist[i].Push(CoreUtil[i]);
            }
            // Memory
            var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)sizeof(Native.MEMORYSTATUSEX) };
            if (Native.GlobalMemoryStatusEx(ref ms)) { MemTotal = ms.ullTotalPhys; MemAvail = ms.ullAvailPhys; }
            var pi = new Native.PERFORMANCE_INFORMATION { cb = (uint)sizeof(Native.PERFORMANCE_INFORMATION) };
            if (Native.GetPerformanceInfo(ref pi, pi.cb))
            {
                ulong ps = pi.PageSize; CommitUsed = pi.CommitTotal * ps; CommitLimit = pi.CommitLimit * ps; Cached = pi.SystemCache * ps;
                Paged = pi.KernelPaged * ps; NonPaged = pi.KernelNonpaged * ps;
                Procs = (int)pi.ProcessCount; Threads = (int)pi.ThreadCount; Handles = (int)pi.HandleCount;
            }
            MemHist.Push(MemTotal == 0 ? 0 : 100.0 * (MemTotal - MemAvail) / MemTotal);
            Uptime = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            SampleDisks(); SampleNets();
            // GPUs
            foreach (var g in Gpus) if (g.IsNvml) try { Nvml.Sample(g.NvHandle, g); } catch (Exception ex) { g.IsNvml = false; Log.Write(ex); }
            if (needGpuPdh || needGpuProcs) SampleGpuPdh(needGpuProcs);
            foreach (var g in Gpus) g.Hist.Push(g.Util);
        }

        public bool HasNonNvml { get { foreach (var g in Gpus) if (!g.IsNvml) return true; return false; } }

        void SampleDisks()
        {
            if (cDiskIdle == null) return;
            int n = cDiskIdle.ReadArray(); cDiskR?.ReadArray(); cDiskW?.ReadArray();
            for (int i = 0; i < n; i++)
            {
                string inst = cDiskIdle.Names[i];
                if (inst == "_Total") continue;
                DiskInfo d = null;
                foreach (var x in Disks) if (x.Inst == inst) { d = x; break; }
                if (d == null)
                {
                    d = new DiskInfo { Inst = inst };
                    int sp = inst.IndexOf(' ');
                    int.TryParse(sp > 0 ? inst.Substring(0, sp) : inst, out d.Index);
                    string letters = sp > 0 ? inst.Substring(sp + 1).Trim() : "";
                    (d.Type, d.Model) = Native.DiskType(d.Index);
                    d.Title = "Disk " + d.Index + (letters.Length > 0 ? " (" + letters + ")" : "");
                    d.Slot = Disks.Count; Disks.Add(d);
                    Disks.Sort((a, b) => a.Index.CompareTo(b.Index));
                }
                d.Active = Math.Clamp(100 - cDiskIdle.Values[i], 0, 100);
                d.Read = cDiskR != null && i < cDiskR.Count && cDiskR.Names[i] == inst ? cDiskR.Values[i] : 0;
                d.Write = cDiskW != null && i < cDiskW.Count && cDiskW.Names[i] == inst ? cDiskW.Values[i] : 0;
                d.Hist.Push(d.Active); d.HistR.Push(d.Read); d.HistW.Push(d.Write);
            }
        }

        void SampleNets()
        {
            if (cNetS == null || cNetR == null) return;
            int n = cNetS.ReadArray(); cNetR.ReadArray();
            for (int i = 0; i < n; i++)
            {
                string inst = cNetS.Names[i];
                NetInfo d = null;
                foreach (var x in Nets) if (x.Inst == inst) { d = x; break; }
                if (d == null)
                {
                    string l = inst.ToLowerInvariant();
                    bool virt = l.Contains("loopback") || l.Contains("isatap") || l.Contains("teredo") || l.Contains("virtual") || l.Contains("hyper-v") || l.Contains("wan miniport") || l.Contains("vpn") || l.Contains("tap-");
                    d = new NetInfo { Inst = inst, Seen = !virt };
                    Nets.Add(d);
                }
                d.Send = cNetS.Values[i];
                d.Recv = i < cNetR.Count && cNetR.Names[i] == inst ? cNetR.Values[i] : 0;
                if (d.Send + d.Recv > 0) d.Seen = true;
                d.HistS.Push(d.Send); d.HistR.Push(d.Recv);
            }
        }

        string Intern(Dictionary<string, string> dict, string s) { if (!dict.TryGetValue(s, out var v)) { dict[s] = s; v = s; } return v; }

        static int ParsePid(string s)
        {
            int p = s.IndexOf("pid_", StringComparison.Ordinal); if (p < 0) return -1;
            int v = 0; for (int i = p + 4; i < s.Length && char.IsDigit(s[i]); i++) v = v * 10 + (s[i] - '0');
            return v;
        }
        string ParseLuid(string s)
        {
            int p = s.IndexOf("luid_", StringComparison.Ordinal); if (p < 0 || p + 26 > s.Length) return "";
            return Intern(luidIntern, s.Substring(p, 26).ToUpperInvariant().Replace("LUID_0X", "luid_0x").Replace("_0X", "_0x"));
        }

        void SampleGpuPdh(bool procs)
        {
            // Rate counters over a very short interval give wild values (false 100% spikes): skip and keep the previous readings.
            long tnowc = Environment.TickCount64;
            if (engineEverCollected && tnowc - lastGpuCollect < 500) return;
            gq.Collect(); lastGpuCollect = tnowc;
            if (!engineEverCollected) { engineEverCollected = true; return; } // rate counters need two samples
            // Adapter memory
            if (cAdDed != null)
            {
                int n = cAdDed.ReadArray(); cAdShared?.ReadArray();
                for (int i = 0; i < n; i++)
                {
                    string l = ParseLuid(cAdDed.Names[i]);
                    foreach (var g in Gpus) if (!string.IsNullOrEmpty(g.Luid) && g.Luid == l)
                        {
                            if (!g.IsNvml) g.MemUsed = (ulong)cAdDed.Values[i];
                            if (cAdShared != null && i < cAdShared.Count) g.SharedUsed = (ulong)cAdShared.Values[i];
                        }
                }
            }
            // Engines
            foreach (var kv in adapterEng) kv.Value.Clear();
            foreach (var g in Gpus) foreach (var en in g.Engines) { en.Acc = 0; if (en.Watchers > 0) foreach (var pu in en.Pids.Values) pu.Acc = 0; }
            foreach (var kv in adPidEng) foreach (var p in kv.Value) p.Value.Clear();
            if (cEngine != null)
            {
                int n = cEngine.ReadArray();
                if (engDup.Length < n) Array.Resize(ref engDup, n + 64);
                dupCheck.Clear();
                for (int k = 0; k < n; k++) engDup[k] = !dupCheck.Add(cEngine.Names[k]);
                if (seenEng.Count > n * 4 + 256) { seenEng.Clear(); for (int k = 0; k < n; k++) seenEng.Add(cEngine.Names[k]); }
                if (engPid.Length < n) { Array.Resize(ref engSkip, n + 64); Array.Resize(ref engSlot, n + 64); Array.Resize(ref engPid, n + 64); Array.Resize(ref engLuid, n + 64); Array.Resize(ref engType, n + 64); }
                for (int i = 0; i < n; i++)
                {
                    if (cEngine.NameChanged[i] || engLuid[i] == null)
                    {
                        string s = cEngine.Names[i];
                        engPid[i] = ParsePid(s); engLuid[i] = ParseLuid(s);
                        int e = s.IndexOf("engtype_", StringComparison.Ordinal);
                        engType[i] = Intern(typeIntern, e >= 0 ? s.Substring(e + 8) : "");
                        // An instance's first reading has no proper previous sample and can be garbage: ignore it once.
                        engSkip[i] = seenEng.Add(s);
                        engSlot[i] = SlotFor(engLuid[i], ParseNum(s, "phys_"), ParseNum(s, "_eng_"), engType[i]);
                    }
                    double v = cEngine.Values[i];
                    if (engDup[i]) v = 0; // same instance name twice in one read: count it once
                    if (engSkip[i]) { engSkip[i] = false; v = 0; }
                    if (v > 100 || double.IsNaN(v) || v < 0) v = v > 100 ? 100 : 0; // one engine instance can't be over 100%
                    if (engSlot[i] != null && v > 0)
                    {
                        var sl = engSlot[i]; sl.Acc += v;
                        if (sl.Watchers > 0)
                        {
                            if (!sl.Pids.TryGetValue(engPid[i], out var pu)) sl.Pids[engPid[i]] = pu = new PidUse();
                            pu.Acc += v;
                        }
                    } if (v <= 0) continue;
                    if (!adapterEng.TryGetValue(engLuid[i], out var te)) adapterEng[engLuid[i]] = te = new Dictionary<string, double>();
                    te.TryGetValue(engType[i], out double cur); te[engType[i]] = cur + v;
                    if (procs)
                    {
                        if (!adPidEng.TryGetValue(engLuid[i], out var pd)) adPidEng[engLuid[i]] = pd = new Dictionary<int, Dictionary<string, double>>();
                        if (!pd.TryGetValue(engPid[i], out var pe)) pd[engPid[i]] = pe = new Dictionary<string, double>();
                        pe.TryGetValue(engType[i], out double c2); pe[engType[i]] = c2 + v;
                    }
                }
            }
            foreach (var g in Gpus)
            {
                double em = 0;
                long tnow = Environment.TickCount64;
                foreach (var en in g.Engines)
                {
                    en.Value = Math.Min(100, en.Acc); if (en.Value > em) em = en.Value; en.Hist?.Push(en.Value);
                    if (en.Watchers > 0) foreach (var pu in en.Pids.Values) { pu.Value = Math.Min(100, pu.Acc); if (pu.Value > 0) pu.LastSeen = tnow; }
                }
                g.EngineMax = em; g.EngSampled = true;
                g.DedHist.Push(g.MemTotal > 0 ? 100.0 * g.MemUsed / g.MemTotal : 0);
                g.ShHist.Push(g.SharedTotal > 0 ? 100.0 * g.SharedUsed / g.SharedTotal : 0);
                if (g.IsNvml) continue;
                double m = 0;
                if (adapterEng.TryGetValue(g.Luid ?? "", out var te)) foreach (var v in te.Values) if (v > m) m = v;
                g.Util = Math.Min(100, m);
            }
            if (!procs) return;
            // Per-process GPU % (max over engine types, across adapters take max)
            ProcGpu.Clear();
            foreach (var g in Gpus) g.Procs.Clear();
            foreach (var ad in adPidEng)
            {
                GpuInfo gi = null; foreach (var g in Gpus) if (g.Luid == ad.Key) gi = g;
                foreach (var p in ad.Value)
                {
                    double m = 0; foreach (var v in p.Value.Values) if (v > m) m = v;
                    if (m <= 0) continue;
                    ProcGpu.TryGetValue(p.Key, out double c); if (m > c) ProcGpu[p.Key] = Math.Min(100, m);
                    if (gi != null) GetProc(gi, p.Key).Util = Math.Min(100, m);
                }
            }
            if (cProcDed != null)
            {
                int n = cProcDed.ReadArray(); cProcShared?.ReadArray();
                if (pmPid.Length < n) { Array.Resize(ref pmPid, n + 64); Array.Resize(ref pmLuid, n + 64); }
                for (int i = 0; i < n; i++)
                {
                    if (cProcDed.NameChanged[i] || pmLuid[i] == null) { pmPid[i] = ParsePid(cProcDed.Names[i]); pmLuid[i] = ParseLuid(cProcDed.Names[i]); }
                    ulong ded = (ulong)cProcDed.Values[i];
                    ulong sh = cProcShared != null && i < cProcShared.Count && cProcShared.Names[i] == cProcDed.Names[i] ? (ulong)cProcShared.Values[i] : 0;
                    if (ded == 0 && sh == 0) continue;
                    foreach (var g in Gpus) if (g.Luid == pmLuid[i]) { var gp = GetProc(g, pmPid[i]); gp.Dedicated += ded; gp.Shared += sh; }
                }
            }
        }

        static string Friendly(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            var sb = new System.Text.StringBuilder(t.Length + 4);
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (i > 0 && char.IsUpper(c) && char.IsLower(t[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        static int ParseNum(string s, string tag)
        {
            int p = s.IndexOf(tag, StringComparison.Ordinal); if (p < 0) return 0;
            int v = 0; for (int i = p + tag.Length; i < s.Length && char.IsDigit(s[i]); i++) v = v * 10 + (s[i] - '0');
            return v;
        }

        // One slot per (adapter, phys, eng index); labelled like Task Manager: "Copy", "Copy 1", ...
        EngineSlot SlotFor(string luid, int phys, int eng, string type)
        {
            GpuInfo g = null; foreach (var x in Gpus) if (x.Luid == luid) { g = x; break; }
            if (g == null) return null;
            foreach (var e in g.Engines) if (e.Phys == phys && e.Eng == eng) return e;
            var n = new EngineSlot { Phys = phys, Eng = eng, Type = Friendly(type), Hist = new Series() };
            g.Engines.Add(n);
            g.Engines.Sort((a, b) => a.Phys != b.Phys ? a.Phys.CompareTo(b.Phys) : a.Eng.CompareTo(b.Eng));
            var count = new Dictionary<string, int>();
            foreach (var e in g.Engines)
            {
                count.TryGetValue(e.Type, out int c); count[e.Type] = c + 1;
                e.Label = e.Type.Length == 0 ? "Engine " + e.Eng : (c == 0 ? e.Type : e.Type + " " + c);
            }
            g.EngVersion++;
            return n;
        }

        static GpuProc GetProc(GpuInfo g, int pid)
        {
            foreach (var p in g.Procs) if (p.Pid == pid) return p;
            var n = new GpuProc { Pid = pid }; g.Procs.Add(n); return n;
        }

        // ---------------- Processes via NtQuerySystemInformation (no handles opened, no admin) ----------------
        IntPtr pbuf; int pbufLen;
        public readonly Dictionary<int, ProcRow> ProcMap = new();
        public readonly List<ProcRow> ProcList = new();
        uint gen; long lastProcTick;

        public void SampleProcesses()
        {
            long now = Environment.TickCount64;
            double dt = lastProcTick == 0 ? 0 : (now - lastProcTick) / 1000.0; lastProcTick = now;
            if (pbuf == IntPtr.Zero) { pbufLen = 1 << 20; pbuf = Marshal.AllocHGlobal(pbufLen); }
            int st;
            while ((st = Native.NtQuerySystemInformation(5, pbuf, pbufLen, out int need)) == unchecked((int)0xC0000004))
            { Marshal.FreeHGlobal(pbuf); pbufLen = Math.Max(need + 65536, pbufLen * 2); pbuf = Marshal.AllocHGlobal(pbufLen); }
            if (st != 0) return;
            gen++;
            byte* b = (byte*)pbuf; int off = 0;
            while (true)
            {
                byte* e = b + off;
                int pid = (int)*(long*)(e + 80);
                long create = *(long*)(e + 32);
                long cpu = *(long*)(e + 40) + *(long*)(e + 48);
                long io = *(long*)(e + 232) + *(long*)(e + 240);
                if (!ProcMap.TryGetValue(pid, out var r) || r.CreateTime != create)
                {
                    ushort len = *(ushort*)(e + 56); char* nm = *(char**)(e + 64);
                    r = new ProcRow { Pid = pid, CreateTime = create, CpuTime = cpu, IoBytes = io, Name = pid == 0 ? "System Idle Process" : (nm != null ? new string(nm, 0, len / 2) : "System") };
                    ProcMap[pid] = r;
                }
                if (dt > 0)
                {
                    r.Cpu = pid == 0 ? 0 : Math.Min(100, (cpu - r.CpuTime) / (dt * 1e7 * Cores) * 100);
                    r.IoRate = Math.Max(0, (io - r.IoBytes) / dt);
                }
                r.CpuTime = cpu; r.IoBytes = io;
                r.PrivateWs = (ulong)*(long*)(e + 8);
                r.Threads = (int)*(uint*)(e + 4); r.Handles = (int)*(uint*)(e + 96);
                r.Gen = gen;
                uint next = *(uint*)e; if (next == 0) break; off += (int)next;
            }
            ProcList.Clear();
            List<int> dead = null;
            foreach (var kv in ProcMap) { if (kv.Value.Gen != gen) (dead ??= new()).Add(kv.Key); else { kv.Value.Gpu = ProcGpu.TryGetValue(kv.Key, out var gg) ? gg : 0; ProcList.Add(kv.Value); } }
            if (dead != null) foreach (var d in dead) ProcMap.Remove(d);
        }

        public string ProcName(int pid) => ProcMap.TryGetValue(pid, out var r) ? r.Name : "pid " + pid;

        public void Dispose() { sys.Dispose(); gq.Dispose(); if (pbuf != IntPtr.Zero) Marshal.FreeHGlobal(pbuf); pbuf = IntPtr.Zero; }
    }
}
