using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LiteMonitor
{
    static class Log
    {
        public static string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteMonitor");
        public static void Write(Exception ex) => Write(ex?.ToString() ?? "null exception");
        public static void Write(string s)
        {
            try { Directory.CreateDirectory(Dir); File.AppendAllText(Path.Combine(Dir, "error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + Environment.NewLine); } catch { }
        }
    }

    static unsafe class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }
        [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo")] public static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION p, uint cb);

        [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int retLen);
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, void* inBuf, int inLen, void* outBuf, int outLen, out int ret, IntPtr ov);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // Returns e.g. "NVMe", "SSD", "HDD" and the model name, without admin rights.
        public static (string type, string model) DiskType(int index)
        {
            IntPtr h = CreateFileW(@"\\.\PhysicalDrive" + index, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == (IntPtr)(-1)) return ("", "");
            try
            {
                const uint IOCTL = 0x2D1400;
                int* q = stackalloc int[3];
                byte* buf = stackalloc byte[1024];
                int bus = -1; string model = "";
                q[0] = 0; q[1] = 0; q[2] = 0;
                if (DeviceIoControl(h, IOCTL, q, 12, buf, 1024, out int ret, IntPtr.Zero) && ret >= 28)
                {
                    bus = *(int*)(buf + 24);
                    uint po = *(uint*)(buf + 12);
                    if (po > 0 && po < ret) model = Marshal.PtrToStringAnsi((IntPtr)(buf + po))?.Trim() ?? "";
                }
                if (bus == 17) return ("NVMe", model);
                q[0] = 7; q[1] = 0;
                byte* sp = stackalloc byte[16];
                if (DeviceIoControl(h, IOCTL, q, 12, sp, 16, out int r2, IntPtr.Zero) && r2 >= 9)
                    return (sp[8] != 0 ? "HDD" : "SSD", model);
                return (bus == 7 ? "USB" : "", model);
            }
            catch { return ("", ""); }
            finally { CloseHandle(h); }
        }

        public static uint BaseMHz()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return Convert.ToUInt32(k?.GetValue("~MHz") ?? 0);
            }
            catch { return 0; }
        }
        public static string CpuName()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
            }
            catch { return "CPU"; }
        }
    }

    // ---------------- PDH ----------------
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double doubleValue; }

    static unsafe class PdhApi
    {
        public const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQueryW(string src, IntPtr user, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounterW(IntPtr q, string path, IntPtr user, out IntPtr c);
        [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(IntPtr q);
        [DllImport("pdh.dll")] public static extern uint PdhGetFormattedCounterValue(IntPtr c, uint fmt, out uint type, out PDH_FMT_COUNTERVALUE v);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhGetFormattedCounterArrayW(IntPtr c, uint fmt, ref uint size, out uint count, IntPtr buf);
    }

    sealed class PdhQuery : IDisposable
    {
        IntPtr q;
        public bool Ok => q != IntPtr.Zero;
        public PdhQuery() { if (PdhApi.PdhOpenQueryW(null, IntPtr.Zero, out q) != 0) q = IntPtr.Zero; }
        public PdhCounter Add(string path)
        {
            if (q == IntPtr.Zero) return null;
            try { return PdhApi.PdhAddEnglishCounterW(q, path, IntPtr.Zero, out IntPtr c) == 0 ? new PdhCounter(c) : null; }
            catch (Exception ex) { Log.Write("PDH add " + path + ": " + ex.Message); return null; }
        }
        public void Collect() { if (q != IntPtr.Zero) PdhApi.PdhCollectQueryData(q); }
        public void Dispose() { if (q != IntPtr.Zero) PdhApi.PdhCloseQuery(q); q = IntPtr.Zero; }
    }

    // A counter; for wildcard (*) counters use ReadArray which reuses its buffers and cached instance-name strings.
    sealed unsafe class PdhCounter
    {
        readonly IntPtr c;
        IntPtr buf; uint bufSize;
        public string[] Names = new string[0];
        public double[] Values = new double[0];
        public bool[] NameChanged = new bool[0];
        public int Count;
        public PdhCounter(IntPtr h) { c = h; }

        public double Read()
        {
            if (PdhApi.PdhGetFormattedCounterValue(c, PdhApi.PDH_FMT_DOUBLE | PdhApi.PDH_FMT_NOCAP100, out _, out var v) != 0) return double.NaN;
            return v.CStatus <= 1 ? v.doubleValue : double.NaN;
        }

        public int ReadArray()
        {
            Count = 0;
            for (int tries = 0; tries < 3; tries++)
            {
                uint size = bufSize;
                uint r = PdhApi.PdhGetFormattedCounterArrayW(c, PdhApi.PDH_FMT_DOUBLE | PdhApi.PDH_FMT_NOCAP100, ref size, out uint n, buf);
                if (r == PdhApi.PDH_MORE_DATA)
                {
                    if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                    bufSize = size + 4096; buf = Marshal.AllocHGlobal((int)bufSize); continue;
                }
                if (r != 0) return 0;
                if (Names.Length < n) { Array.Resize(ref Names, (int)n + 16); Array.Resize(ref Values, (int)n + 16); Array.Resize(ref NameChanged, (int)n + 16); }
                byte* b = (byte*)buf;
                for (int i = 0; i < n; i++)
                {
                    byte* item = b + i * 24; // PDH_FMT_COUNTERVALUE_ITEM_W: LPWSTR szName (8) + PDH_FMT_COUNTERVALUE (16)
                    char* name = *(char**)item;
                    uint status = *(uint*)(item + 8);
                    double val = *(double*)(item + 16);
                    int len = 0; while (name[len] != 0) len++;
                    var span = new ReadOnlySpan<char>(name, len);
                    var cached = Names[i];
                    if (cached == null || !span.SequenceEqual(cached.AsSpan())) { Names[i] = new string(span); NameChanged[i] = true; }
                    else NameChanged[i] = false;
                    Values[i] = status <= 1 ? val : 0;
                }
                Count = (int)n; return Count;
            }
            return 0;
        }
    }

    // ---------------- NVML (NVIDIA, loaded dynamically) ----------------
    static unsafe class Nvml
    {
        static IntPtr lib;
        static delegate* unmanaged[Cdecl]<int> init;
        static delegate* unmanaged[Cdecl]<uint*, int> getCount;
        static delegate* unmanaged[Cdecl]<uint, IntPtr*, int> getHandle;
        static delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int> getName;
        static delegate* unmanaged[Cdecl]<IntPtr, uint*, int> getUtil; // nvmlUtilization_t {uint gpu, memory}
        static delegate* unmanaged[Cdecl]<IntPtr, ulong*, int> getMem;   // nvmlMemory_t {ulong total, free, used}
        static delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> getTemp;
        static delegate* unmanaged[Cdecl]<IntPtr, uint*, int> getPower, getPowerLimit, getFan;
        static delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> getClock;
        static delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int> getEnc, getDec;
        static delegate* unmanaged[Cdecl]<IntPtr, uint*, int> getPci; // nvmlPciInfo_t (we only need busId)
        public static bool Available;

        static void* Get(string n) => NativeLibrary.TryGetExport(lib, n, out IntPtr a) ? (void*)a : null;

        public static bool Load()
        {
            try
            {
                string[] paths = { Path.Combine(Environment.SystemDirectory, "nvml.dll"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll" };
                foreach (var p in paths) if (File.Exists(p) && NativeLibrary.TryLoad(p, out lib)) break;
                if (lib == IntPtr.Zero) return false;
                init = (delegate* unmanaged[Cdecl]<int>)Get("nvmlInit_v2");
                getCount = (delegate* unmanaged[Cdecl]<uint*, int>)Get("nvmlDeviceGetCount_v2");
                getHandle = (delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)Get("nvmlDeviceGetHandleByIndex_v2");
                getName = (delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int>)Get("nvmlDeviceGetName");
                getUtil = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)Get("nvmlDeviceGetUtilizationRates");
                getMem = (delegate* unmanaged[Cdecl]<IntPtr, ulong*, int>)Get("nvmlDeviceGetMemoryInfo");
                getTemp = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)Get("nvmlDeviceGetTemperature");
                getPower = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)Get("nvmlDeviceGetPowerUsage");
                getPowerLimit = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)Get("nvmlDeviceGetEnforcedPowerLimit");
                getFan = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)Get("nvmlDeviceGetFanSpeed");
                getClock = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)Get("nvmlDeviceGetClockInfo");
                getEnc = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int>)Get("nvmlDeviceGetEncoderUtilization");
                getDec = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, int>)Get("nvmlDeviceGetDecoderUtilization");
                if (init == null || getCount == null || getHandle == null) return false;
                Available = init() == 0;
                return Available;
            }
            catch (Exception ex) { Log.Write("NVML load: " + ex.Message); return false; }
        }

        public static int Count() { if (!Available) return 0; uint n = 0; return getCount(&n) == 0 ? (int)n : 0; }
        public static IntPtr Handle(int i) { IntPtr h = IntPtr.Zero; return getHandle((uint)i, &h) == 0 ? h : IntPtr.Zero; }
        public static string Name(IntPtr d)
        {
            if (getName == null) return "NVIDIA GPU";
            byte* b = stackalloc byte[96];
            return getName(d, b, 96) == 0 ? Marshal.PtrToStringAnsi((IntPtr)b) : "NVIDIA GPU";
        }

        public static void Sample(IntPtr d, GpuInfo g)
        {
            uint a = 0, b = 0;
            uint* u = stackalloc uint[2];
            if (getUtil != null && getUtil(d, u) == 0) { g.Util = u[0]; g.MemCtrl = u[1]; }
            ulong* m = stackalloc ulong[3];
            if (getMem != null && getMem(d, m) == 0) { g.MemTotal = m[0]; g.MemUsed = m[2]; }
            g.Temp = getTemp != null && getTemp(d, 0, &a) == 0 ? a : -1;
            g.PowerW = getPower != null && getPower(d, &a) == 0 ? a / 1000.0 : -1;
            g.PowerLimitW = getPowerLimit != null && getPowerLimit(d, &a) == 0 ? a / 1000.0 : -1;
            g.Fan = getFan != null && getFan(d, &a) == 0 ? a : -1;
            g.ClockCore = getClock != null && getClock(d, 0, &a) == 0 ? a : -1;
            g.ClockMem = getClock != null && getClock(d, 2, &a) == 0 ? a : -1;
            g.Enc = getEnc != null && getEnc(d, &a, &b) == 0 ? a : -1;
            g.Dec = getDec != null && getDec(d, &a, &b) == 0 ? a : -1;
        }
    }

    // ---------------- DXGI adapter list (names + LUIDs) ----------------
    static unsafe class Dxgi
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DESC1
        {
            public fixed char Description[128];
            public uint VendorId, DeviceId, SubSysId, Revision;
            public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow; public int LuidHigh;
            public uint Flags;
        }
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(Guid* riid, IntPtr* f);

        public record Adapter(string Name, uint Vendor, string Luid, ulong Dedicated, ulong Shared);

        public static System.Collections.Generic.List<Adapter> List()
        {
            var list = new System.Collections.Generic.List<Adapter>();
            try
            {
                Guid iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                IntPtr f;
                if (CreateDXGIFactory1(&iid, &f) < 0 || f == IntPtr.Zero) return list;
                void** fv = *(void***)f;
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr ad;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)fv[12])(f, i, &ad) < 0) break;
                    void** av = *(void***)ad;
                    DESC1 d;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, DESC1*, int>)av[10])(ad, &d) >= 0 && (d.Flags & 2) == 0)
                    {
                        string name = new string(d.Description).TrimEnd('\0').Trim();
                        string luid = $"luid_0x{(uint)d.LuidHigh:X8}_0x{d.LuidLow:X8}";
                        bool dup = false; foreach (var x in list) if (x.Luid == luid) dup = true;
                        if (!dup) list.Add(new Adapter(name, d.VendorId, luid, d.DedicatedVideoMemory, d.SharedSystemMemory));
                    }
                    ((delegate* unmanaged[Stdcall]<IntPtr, uint>)av[2])(ad);
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)fv[2])(f);
            }
            catch (Exception ex) { Log.Write("DXGI: " + ex.Message); }
            return list;
        }
    }
}
