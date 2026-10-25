using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor
{
    class MainForm : Form
    {
        public static readonly Icon AppIcon = LoadIcon();
        static Icon LoadIcon()
        {
            try { using var st = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"); return st != null ? new Icon(st) : SystemIcons.Application; }
            catch { return SystemIcons.Application; }
        }

        readonly Settings set = Settings.Load();
        Sampler S;
        readonly System.Windows.Forms.Timer timer = new();
        NotifyIcon tray;
        Overlay overlay;
        readonly bool startInTray;
        bool ready, reallyExit;
        long lastSlow;

        Panel content; FlowLayoutPanel nav;
        readonly List<Button> navButtons = new(); readonly List<Control> pages = new();
        FlowLayoutPanel perf; Control procPage, gpuPage, setPage;
        Tile tCpu, tMem; CoresTile tCores;
        readonly Dictionary<object, Tile> tiles = new();
        ListView lvProc; ProcRow[] procView = new ProcRow[0]; int sortCol = 2; bool sortDesc = true;
        readonly List<(GpuInfo g, ListView lv, Label lbl)> gpuLists = new();
        Label status; GpuPage gpuDetail; long lastEng;

        public MainForm(bool tray)
        {
            startInTray = tray;
            Text = "LiteMonitor"; Icon = AppIcon; BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f); AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(1150, 820); MinimumSize = new Size(520, 400); StartPosition = FormStartPosition.CenterScreen;
            try { Build(); } catch (Exception ex) { Log.Write(ex); }
            if (set.WinW > 300 && set.WinH > 200)
            {
                var r = new Rectangle(set.WinX, set.WinY, set.WinW, set.WinH);
                foreach (var sc in Screen.AllScreens) if (sc.WorkingArea.IntersectsWith(r)) { StartPosition = FormStartPosition.Manual; Bounds = r; break; }
                if (set.WinMax) WindowState = FormWindowState.Maximized;
            }
            ready = true;
        }

        void Build()
        {
            try { S = new Sampler(); } catch (Exception ex) { Log.Write(ex); }

            nav = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.FromArgb(30, 30, 34), Padding = new Padding(6, 4, 6, 4), WrapContents = true };
            content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            status = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = Theme.Dim, Padding = new Padding(8, 3, 0, 0), Text = "" };
            Controls.Add(content); Controls.Add(status); Controls.Add(nav);

            // Performance page
            perf = new StillFlowPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(6) };
            perf.Resize += (s, e) => Relayout();
            AddPage("Performance", perf);
            if (S != null)
            {
                tCpu = NewTile(S, Theme.Cpu, S.CpuName); tCpu.Title = "CPU";
                tCores = new CoresTile { S = S }; perf.Controls.Add(tCores);
                tMem = NewTile(S.MemHist, Theme.Mem, ""); tMem.Title = "Memory";
                foreach (var g in S.Gpus) { var t = NewTile(g, Theme.GpuColor(g), g.Name); t.S1 = g.Hist; t.Title = "GPU"; }
            }

            // Processes page
            lvProc = MakeList(new[] { ("Name", 220), ("PID", 70), ("CPU %", 70), ("Memory (private)", 120), ("Disk I/O", 100), ("GPU %", 70), ("Threads", 70), ("Handles", 70) });
            lvProc.VirtualMode = true; lvProc.VirtualListSize = 0;
            lvProc.RetrieveVirtualItem += (s, e) => e.Item = ProcItem(e.ItemIndex);
            lvProc.ColumnClick += (s, e) => { if (sortCol == e.Column) sortDesc = !sortDesc; else { sortCol = e.Column; sortDesc = e.Column >= 2; } RefreshProcView(); };
            var cm = new ContextMenuStrip(); cm.Items.Add("End process…", null, (s, e) => EndSelected());
            lvProc.ContextMenuStrip = cm;
            lvProc.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) EndSelected(); };
            var pp = new Panel { Dock = DockStyle.Fill };
            var hint = new Label { Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Dim, Text = "  Click a column to sort. Right-click a process (or press Delete) to end it. Updates every 2 seconds while this tab is open." };
            pp.Controls.Add(lvProc); pp.Controls.Add(hint);
            procPage = pp; AddPage("Processes", pp);

            // GPU apps page (per adapter VRAM by process)
            var gp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Bg, Padding = new Padding(6) };
            int ng = S?.Gpus.Count ?? 0;
            gp.RowCount = Math.Max(1, ng * 2);
            for (int i = 0; i < ng; i++)
            {
                var g = S.Gpus[i];
                gp.RowStyles.Add(new RowStyle(SizeType.AutoSize)); gp.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / ng));
                var lbl = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Theme.GpuColor(g), Text = g.Name, Margin = new Padding(3, 8, 3, 4) };
                var lv = MakeList(new[] { ("App", 240), ("PID", 70), ("Dedicated VRAM", 130), ("Shared GPU memory", 140), ("GPU %", 70) });
                gp.Controls.Add(lbl, 0, i * 2); gp.Controls.Add(lv, 0, i * 2 + 1);
                gpuLists.Add((g, lv, lbl));
            }
            if (ng == 0) gp.Controls.Add(new Label { AutoSize = true, Text = "No graphics cards were found.", ForeColor = Theme.Dim });
            gpuPage = gp; AddPage("GPU apps", gp);

            gpuDetail = new GpuPage(S, set) { OpenEngine = OpenEnginePopup, OpenMem = OpenMemPopup }; AddPage("GPUs", gpuDetail);

            // Settings page
            setPage = BuildSettings(); AddPage("Settings", setPage);

            var ovBtn = new Button { Text = "Mini overlay (Ctrl+Alt+M)", AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text, BackColor = Color.FromArgb(45, 47, 54), Margin = new Padding(24, 2, 2, 2) };
            ovBtn.FlatAppearance.BorderSize = 0; ovBtn.Click += (s, e) => ToggleOverlay();
            nav.Controls.Add(ovBtn);
            ShowPage(0);

            // Tray
            tray = new NotifyIcon { Icon = AppIcon, Text = "LiteMonitor", Visible = true };
            var tm = new ContextMenuStrip();
            tm.Items.Add("Open LiteMonitor", null, (s, e) => RestoreFromTray());
            tm.Items.Add("Mini overlay", null, (s, e) => ToggleOverlay());
            tm.Items.Add("Exit", null, (s, e) => { reallyExit = true; Close(); });
            tray.ContextMenuStrip = tm; tray.DoubleClick += (s, e) => RestoreFromTray();

            timer.Interval = Math.Clamp(set.IntervalMs, 250, 5000);
            timer.Tick += (s, e) => Tick();
        }

        Tile NewTile(object key, Color c, string sub)
        {
            var t = new Tile { C1 = c, Sub = sub ?? "" };
            if (key is Series se) t.S1 = se;
            if (key is Sampler) t.S1 = S.CpuHist;
            tiles[key] = t; perf.Controls.Add(t); return t;
        }

        ListView MakeList((string, int)[] cols)
        {
            var lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Clickable };
            foreach (var (n, w) in cols) lv.Columns.Add(n, LogicalToDeviceUnits(w), n == "Name" || n == "App" ? HorizontalAlignment.Left : HorizontalAlignment.Right);
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(lv, true);
            return lv;
        }

        void AddPage(string name, Control page)
        {
            int idx = pages.Count;
            var b = new Button { Text = name, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text, BackColor = Color.FromArgb(30, 30, 34), Margin = new Padding(2), Padding = new Padding(8, 2, 8, 2) };
            b.FlatAppearance.BorderSize = 0; b.Click += (s, e) => ShowPage(idx);
            nav.Controls.Add(b); navButtons.Add(b);
            page.Visible = false; content.Controls.Add(page); pages.Add(page);
        }

        int curPage;
        void ShowPage(int i)
        {
            curPage = i;
            for (int k = 0; k < pages.Count; k++) { pages[k].Visible = k == i; navButtons[k].BackColor = k == i ? Color.FromArgb(55, 90, 140) : Color.FromArgb(30, 30, 34); }
            lastSlow = 0; // refresh lists right away
            if (i == 0) Relayout();
        }

        Control BuildSettings()
        {
            var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16), AutoScroll = true };
            p.Controls.Add(new Label { AutoSize = true, Text = "Update speed:", Margin = new Padding(3, 6, 3, 3) });
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, BackColor = Theme.Card, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
            int[] vals = { 500, 1000, 2000 };
            cb.Items.AddRange(new object[] { "Every 0.5 second (smoothest)", "Every 1 second (recommended)", "Every 2 seconds (lightest)" });
            cb.SelectedIndex = Math.Max(0, Array.IndexOf(vals, set.IntervalMs) < 0 ? 1 : Array.IndexOf(vals, set.IntervalMs));
            cb.SelectedIndexChanged += (s, e) => { if (!ready) return; set.IntervalMs = vals[cb.SelectedIndex]; timer.Interval = set.IntervalMs; set.Save(); };
            p.Controls.Add(cb);
            var chkTray = new CheckBox { AutoSize = true, Text = "Hide to the tray (by the clock) when minimized or closed", Checked = set.MinimizeToTray, Margin = new Padding(3, 14, 3, 3) };
            chkTray.CheckedChanged += (s, e) => { if (!ready) return; set.MinimizeToTray = chkTray.Checked; set.Save(); };
            p.Controls.Add(chkTray);
            var chkRun = new CheckBox { AutoSize = true, Text = "Start LiteMonitor when Windows starts (opens in the tray)", Checked = Settings.StartWithWindows };
            chkRun.CheckedChanged += (s, e) => { if (!ready) return; Settings.StartWithWindows = chkRun.Checked; };
            p.Controls.Add(chkRun);
            var logBtn = new Button { Text = "Open settings / log folder", AutoSize = true, FlatStyle = FlatStyle.Flat, Margin = new Padding(3, 14, 3, 3) };
            logBtn.Click += (s, e) => { try { System.IO.Directory.CreateDirectory(Log.Dir); Process.Start("explorer.exe", Log.Dir); } catch (Exception ex) { Log.Write(ex); } };
            p.Controls.Add(logBtn);
            p.Controls.Add(new Label { AutoSize = true, ForeColor = Theme.Dim, Margin = new Padding(3, 14, 3, 3), Text = "Tip: Ctrl+Alt+M shows or hides the small always-on-top overlay. Drag it anywhere; it remembers where you put it." });
            p.Controls.Add(new Label { AutoSize = true, ForeColor = Theme.Dim, Text = "NVIDIA details: " + (Nvml.Available ? "connected to the NVIDIA driver" : "not available (using basic Windows GPU counters)") });
            return p;
        }

        void Relayout()
        {
            if (perf == null || S == null || !perf.Visible) return;
            try
            {
                var keep = perf.AutoScrollPosition;
                perf.SuspendLayout();
                int w = perf.ClientSize.Width - perf.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
                int min = LogicalToDeviceUnits(380), m = LogicalToDeviceUnits(12);
                int cols = Math.Max(1, w / min);
                int tw = Math.Max(LogicalToDeviceUnits(240), w / cols - m), th = LogicalToDeviceUnits(230);
                foreach (Control c in perf.Controls)
                {
                    if (c == tCores) c.Size = new Size(Math.Max(tw, w - m), LogicalToDeviceUnits(S.Cores > 16 ? 300 : 220));
                    else c.Size = new Size(tw, th);
                }
                perf.ResumeLayout();
                perf.AutoScrollPosition = new Point(-keep.X, -keep.Y); // stay where the user scrolled
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (startInTray) BeginInvoke(new Action(() => Hide()));
            if (set.OverlayOn) ToggleOverlay();
            Relayout();
            timer.Start();
            Tick();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!Native.RegisterHotKey(Handle, 1, 0x1 | 0x2 | 0x4000, (uint)Keys.M)) Log.Write("Ctrl+Alt+M hotkey is used by another app");
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x312 && m.WParam == (IntPtr)1) { ToggleOverlay(); return; }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ready && set != null && set.MinimizeToTray && WindowState == FormWindowState.Minimized) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && set.MinimizeToTray && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            try
            {
                var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                set.WinX = b.X; set.WinY = b.Y; set.WinW = b.Width; set.WinH = b.Height; set.WinMax = WindowState == FormWindowState.Maximized;
                set.OverlayOn = overlay != null && overlay.Visible;
                set.Save();
                Native.UnregisterHotKey(Handle, 1);
                timer.Stop(); if (tray != null) tray.Visible = false; S?.Dispose();
            }
            catch (Exception ex) { Log.Write(ex); }
            base.OnFormClosing(e);
        }

        void RestoreFromTray() { Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); Relayout(); }

        void ToggleOverlay()
        {
            try
            {
                overlay ??= new Overlay(set);
                if (overlay.Visible) overlay.Hide(); else { overlay.Show(); UpdateOverlay(); }
                set.OverlayOn = overlay.Visible; set.Save();
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        // ---------------- sampling loop ----------------
        void Tick()
        {
            if (S == null) return;
            try
            {
                bool mainVis = Visible && WindowState != FormWindowState.Minimized;
                bool ovVis = overlay != null && overlay.Visible;
                bool popsOpen = popups.Count > 0;
                if (!mainVis && !ovVis && !popsOpen) return; // nothing on screen -> do no work at all
                long now = Environment.TickCount64;
                bool slow = now - lastSlow >= 1950;
                bool listsVisible = mainVis && (curPage == 1 || curPage == 2);
                bool engVis = (mainVis && curPage == 3 || popsOpen) && now - lastEng >= 950;
                if (engVis) lastEng = now;
                bool needGpuPdh = engVis || slow && (S.HasNonNvml && (mainVis && curPage == 0 || ovVis) || listsVisible);
                if (slow && (listsVisible || needGpuPdh)) lastSlow = now;
                bool popDue = popsOpen && engVis;
                S.Sample(needGpuPdh, listsVisible && slow || popDue);
                if (popDue)
                {
                    if (slow && !listsVisible) { S.SampleProcesses(); lastSlow = now; }
                    foreach (var p in popups) (p as ILivePopup)?.RefreshList();
                }
                if (mainVis)
                {
                    if (curPage == 0) UpdatePerf();
                    else if (curPage == 1 && slow) { S.SampleProcesses(); RefreshProcView(); }
                    else if (curPage == 2 && slow) { S.SampleProcesses(); RefreshGpuLists(); }
                    else if (curPage == 3 && engVis && mainVis) gpuDetail.UpdateView();
                    status.Text = $"  CPU {S.CpuTotal:0}%   Memory {Theme.Bytes(S.MemTotal - S.MemAvail)} / {Theme.Bytes(S.MemTotal)}   Up {(int)S.Uptime.TotalDays}d {S.Uptime.Hours:00}:{S.Uptime.Minutes:00}:{S.Uptime.Seconds:00}";
                }
                if (ovVis) UpdateOverlay();
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        void UpdatePerf()
        {
            tCpu.Value = S.CpuTotal.ToString("0") + "%";
            tCpu.Lines = new[] { $"Speed: {S.CpuMHz / 1000:0.00} GHz", $"Base: {S.BaseMHz / 1000.0:0.00} GHz", $"Processes: {S.Procs}", $"Threads: {S.Threads:N0}", $"Handles: {S.Handles:N0}", $"Up: {(int)S.Uptime.TotalDays}d {S.Uptime.Hours:00}:{S.Uptime.Minutes:00}:{S.Uptime.Seconds:00}" };
            tCpu.Invalidate(); tCores.Invalidate();
            ulong used = S.MemTotal - S.MemAvail;
            tMem.Value = Theme.Bytes(used) + " / " + Theme.Bytes(S.MemTotal);
            tMem.Lines = new[] { $"In use: {Theme.Bytes(used)}", $"Available: {Theme.Bytes(S.MemAvail)}", $"Committed: {Theme.Bytes(S.CommitUsed)} / {Theme.Bytes(S.CommitLimit)}", $"Cached: {Theme.Bytes(S.Cached)}", $"Paged pool: {Theme.Bytes(S.Paged)}", $"Non-paged: {Theme.Bytes(S.NonPaged)}" };
            tMem.Invalidate();
            foreach (var g in S.Gpus)
            {
                var t = tiles[g];
                t.Value = g.Util.ToString("0") + "%";
                var l = new List<string>(10);
                if (g.MemTotal > 0) l.Add($"VRAM: {Theme.Bytes(g.MemUsed)} / {Theme.Bytes(g.MemTotal)}");
                else l.Add($"Shared mem: {Theme.Bytes(g.SharedUsed)}");
                if (g.Temp >= 0) l.Add($"Temp: {g.Temp:0} °C");
                if (g.PowerW >= 0) l.Add($"Power: {g.PowerW:0} W" + (g.PowerLimitW > 0 ? $" / {g.PowerLimitW:0} W" : ""));
                if (g.Fan >= 0) l.Add($"Fan: {g.Fan:0}%");
                if (g.ClockCore >= 0) l.Add($"Core clock: {g.ClockCore:0} MHz");
                if (g.ClockMem >= 0) l.Add($"Mem clock: {g.ClockMem:0} MHz");
                if (g.Enc >= 0) l.Add($"Encoder: {g.Enc:0}%");
                if (g.Dec >= 0) l.Add($"Decoder: {g.Dec:0}%");
                if (!g.IsNvml && g.MemTotal > 0) l.Add($"Shared mem: {Theme.Bytes(g.SharedUsed)}");
                t.Lines = l.ToArray(); t.Invalidate();
            }
            bool added = false;
            foreach (var d in S.Disks)
            {
                if (!tiles.TryGetValue(d, out var t))
                {
                    t = new Tile { C1 = Theme.Disk, C2 = Color.FromArgb(160, 240, 180), S1 = d.Hist, Title = d.Title, Sub = (d.Type.Length > 0 ? d.Type + "  " : "") + d.Model };
                    tiles[d] = t; perf.Controls.Add(t); added = true;
                }
                t.Value = d.Active.ToString("0") + "%";
                t.Lines = new[] { "Read: " + Theme.Rate(d.Read), "Write: " + Theme.Rate(d.Write) };
                t.Invalidate();
            }
            foreach (var n in S.Nets)
            {
                if (!n.Seen) continue;
                if (!tiles.TryGetValue(n, out var t))
                {
                    t = new Tile { C1 = Theme.Net, C2 = Color.FromArgb(255, 220, 140), S1 = n.HistR, S2 = n.HistS, FixedMax = 0, Title = "Network", Sub = n.Inst };
                    tiles[n] = t; perf.Controls.Add(t); added = true;
                }
                t.Value = "↓ " + Theme.Bits(n.Recv);
                t.Lines = new[] { "Receive (solid): " + Theme.Bits(n.Recv), "Send (dashed): " + Theme.Bits(n.Send) };
                t.Invalidate();
            }
            if (added) Relayout();
        }

        void UpdateOverlay()
        {
            GpuInfo g = null; foreach (var x in S.Gpus) if (x.IsNvml) { g = x; break; }
            if (g == null && S.Gpus.Count > 0) g = S.Gpus[0];
            ulong used = S.MemTotal - S.MemAvail;
            overlay.Lines = g == null
                ? new[] { $"CPU {S.CpuTotal,3:0}%  {S.CpuMHz / 1000:0.0} GHz", $"RAM {used / 1073741824.0:0.0}/{S.MemTotal / 1073741824.0:0} GB" }
                : new[] { $"CPU {S.CpuTotal,3:0}%  {S.CpuMHz / 1000:0.0} GHz", $"GPU {g.Util,3:0}%  " + (g.Temp >= 0 ? $"{g.Temp:0}°C  " : "") + (g.PowerW >= 0 ? $"{g.PowerW:0} W" : ""),
                          $"RAM  {used / 1073741824.0:0.0}/{S.MemTotal / 1073741824.0:0} GB", $"VRAM {g.MemUsed / 1073741824.0:0.0}/{g.MemTotal / 1073741824.0:0} GB" };
            overlay.Invalidate();
        }

        // ---------------- processes ----------------
        int CompareProc(ProcRow a, ProcRow b)
        {
            int c = sortCol switch
            {
                0 => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                1 => a.Pid.CompareTo(b.Pid),
                2 => a.Cpu.CompareTo(b.Cpu),
                3 => a.PrivateWs.CompareTo(b.PrivateWs),
                4 => a.IoRate.CompareTo(b.IoRate),
                5 => a.Gpu.CompareTo(b.Gpu),
                6 => a.Threads.CompareTo(b.Threads),
                _ => a.Handles.CompareTo(b.Handles),
            };
            if (c == 0) c = b.PrivateWs.CompareTo(a.PrivateWs) * (sortDesc ? -1 : 1);
            return sortDesc ? -c : c;
        }
        Comparison<ProcRow> cmp;
        readonly List<Form> popups = new();
        void OpenEnginePopup(GpuInfo g, string label)
        {
            try
            {
                var slot = g.FindEngine(label);
                if (slot == null) { MessageBox.Show(this, "That engine hasn't reported any data yet. Try again in a second.", "LiteMonitor"); return; }
                foreach (var p in popups) if (p is EngineUsers eu && eu.Slot == slot) { p.Activate(); return; }
                if (S.ProcMap.Count == 0) S.SampleProcesses();
                var w = new EngineUsers(S, g, slot, set, JumpToProcess) { Owner = this };
                w.FormClosed += (o, e) => popups.Remove(w);
                popups.Add(w); w.Show(); lastEng = 0;
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        void OpenMemPopup(GpuInfo g, bool shared)
        {
            try
            {
                foreach (var p in popups) if (p is MemUsers mu && mu.G == g && mu.Shared == shared) { p.Activate(); return; }
                if (S.ProcMap.Count == 0) S.SampleProcesses();
                var w = new MemUsers(S, g, shared, set, JumpToProcess) { Owner = this };
                w.FormClosed += (o, e) => popups.Remove(w);
                popups.Add(w); w.Show(); lastEng = 0;
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        void JumpToProcess(int pid)
        {
            try
            {
                if (!Visible) RestoreFromTray();
                ShowPage(1);
                S.SampleProcesses(); RefreshProcView();
                for (int i = 0; i < procView.Length; i++)
                    if (procView[i].Pid == pid) { lvProc.SelectedIndices.Clear(); lvProc.SelectedIndices.Add(i); lvProc.EnsureVisible(i); lvProc.Focus(); break; }
                Activate();
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        void RefreshProcView()
        {
            int selPid = lvProc.SelectedIndices.Count > 0 && lvProc.SelectedIndices[0] < procView.Length ? procView[lvProc.SelectedIndices[0]].Pid : -1;
            cmp ??= CompareProc;
            S.ProcList.Sort(cmp);
            procView = S.ProcList.ToArray();
            // remember the exact scroll row: changing the list size or selection makes Windows move it
            int before = ListScroll.Get(lvProc);
            int newSel = -1;
            if (selPid >= 0) for (int i = 0; i < procView.Length; i++) if (procView[i].Pid == selPid) { newSel = i; break; }
            lvProc.BeginUpdate();
            if (lvProc.VirtualListSize != procView.Length) lvProc.VirtualListSize = procView.Length;
            int curSel = lvProc.SelectedIndices.Count > 0 ? lvProc.SelectedIndices[0] : -1;
            if (curSel != newSel)
            {
                lvProc.SelectedIndices.Clear();
                if (newSel >= 0) lvProc.SelectedIndices.Add(newSel);
            }
            lvProc.EndUpdate();
            ListScroll.Restore(lvProc, before);
            lvProc.Invalidate();
        }
        ListViewItem ProcItem(int i)
        {
            if (i < 0 || i >= procView.Length) return new ListViewItem(new[] { "", "", "", "", "", "", "", "" });
            var r = procView[i];
            return new ListViewItem(new[] { r.Name, r.Pid.ToString(), r.Cpu.ToString("0.0"), Theme.Bytes(r.PrivateWs), r.IoRate > 0 ? Theme.Rate(r.IoRate) : "0", r.Gpu > 0 ? r.Gpu.ToString("0.0") : "", r.Threads.ToString(), r.Handles.ToString() });
        }
        void EndSelected()
        {
            if (lvProc.SelectedIndices.Count == 0) return;
            int i = lvProc.SelectedIndices[0]; if (i >= procView.Length) return;
            var r = procView[i];
            if (r.Pid <= 4) { MessageBox.Show("That is a core Windows process and can't be ended.", "LiteMonitor"); return; }
            if (MessageBox.Show($"End \"{r.Name}\" (PID {r.Pid})?\n\nAny unsaved work in it will be lost.", "End process", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { using var p = Process.GetProcessById(r.Pid); p.Kill(); }
            catch (Exception ex) { MessageBox.Show("Couldn't end it: " + ex.Message + "\n(Some system or admin apps can only be ended from an admin Task Manager.)", "LiteMonitor"); }
        }

        void RefreshGpuLists()
        {
            foreach (var (g, lv, lbl) in gpuLists)
            {
                foreach (var p in g.Procs) p.Name = S.ProcName(p.Pid);
                g.Procs.Sort((a, b) => b.Dedicated != a.Dedicated ? b.Dedicated.CompareTo(a.Dedicated) : b.Shared.CompareTo(a.Shared));
                ulong tot = 0; foreach (var p in g.Procs) tot += p.Dedicated;
                lbl.Text = $"{g.Name}   —   VRAM {Theme.Bytes(g.MemUsed)}" + (g.MemTotal > 0 ? $" / {Theme.Bytes(g.MemTotal)}" : "") + $"   (apps listed: {Theme.Bytes(tot)})   GPU {g.Util:0}%";
                int before = ListScroll.Get(lv);
                lv.BeginUpdate();
                int k = 0;
                foreach (var p in g.Procs)
                {
                    if (p.Dedicated == 0 && p.Shared == 0 && p.Util <= 0) continue;
                    string[] v = { p.Name, p.Pid.ToString(), Theme.Bytes(p.Dedicated), Theme.Bytes(p.Shared), p.Util > 0 ? p.Util.ToString("0.0") : "" };
                    if (k < lv.Items.Count) { var it = lv.Items[k]; for (int c = 0; c < v.Length; c++) if (it.SubItems[c].Text != v[c]) it.SubItems[c].Text = v[c]; }
                    else lv.Items.Add(new ListViewItem(v));
                    k++;
                }
                while (lv.Items.Count > k) lv.Items.RemoveAt(lv.Items.Count - 1);
                lv.EndUpdate();
                ListScroll.Restore(lv, before);
            }
        }
    }

    /// <summary>A scrolling panel that doesn't jump back to the top when something inside it updates or gets focus.</summary>
    class StillFlowPanel : FlowLayoutPanel
    {
        public StillFlowPanel() { DoubleBuffered = true; }
        protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
    }

    /// <summary>Saves and restores a list's scroll row exactly (report view scrolls by whole rows).</summary>
    static class ListScroll
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetScrollPos(IntPtr h, int bar);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        const int SB_VERT = 1, LVM_SCROLL = 0x1014;
        public static int Get(ListView lv) { try { return lv.IsHandleCreated ? GetScrollPos(lv.Handle, SB_VERT) : 0; } catch { return 0; } }
        public static void Restore(ListView lv, int before)
        {
            try
            {
                if (!lv.IsHandleCreated || lv.Items.Count == 0) return;
                int now = GetScrollPos(lv.Handle, SB_VERT);
                if (now == before) return;
                int row = Math.Max(1, lv.GetItemRect(0).Height);
                SendMessage(lv.Handle, LVM_SCROLL, IntPtr.Zero, (IntPtr)((before - now) * row));
            }
            catch { }
        }
    }
}
