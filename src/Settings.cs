using System;
using System.IO;
using System.Text.Json;

namespace LiteMonitor
{
    class Settings
    {
        public int IntervalMs { get; set; } = 1000;
        public bool MinimizeToTray { get; set; } = true;
        public bool OverlayOn { get; set; }
        public int OverlayX { get; set; } = -1;
        public int OverlayY { get; set; } = -1;
        public int WinX { get; set; } = -1;
        public int WinY { get; set; } = -1;
        public int WinW { get; set; }
        public int WinH { get; set; }
        public bool WinMax { get; set; }
        public System.Collections.Generic.Dictionary<string, string[]> GpuEngines { get; set; } = new();
        public System.Collections.Generic.Dictionary<string, bool> GpuShowAll { get; set; } = new();
        public int GpuSelected { get; set; }
        public int EngPopupW { get; set; }
        public int EngPopupH { get; set; }
        public int MemPopupW { get; set; }
        public int MemPopupH { get; set; }

        static string PathF => Path.Combine(Log.Dir, "settings.json");
        public static Settings Load()
        {
            try { if (File.Exists(PathF)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathF)) ?? new Settings(); }
            catch (Exception ex) { Log.Write(ex); }
            return new Settings();
        }
        public void Save()
        {
            try { Directory.CreateDirectory(Log.Dir); File.WriteAllText(PathF, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Log.Write(ex); }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool StartWithWindows
        {
            get { try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("LiteMonitor") != null; } catch { return false; } }
            set
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                    if (value) k.SetValue("LiteMonitor", "\"" + Environment.ProcessPath + "\" --tray"); else k.DeleteValue("LiteMonitor", false);
                }
                catch (Exception ex) { Log.Write(ex); }
            }
        }
    }
}
