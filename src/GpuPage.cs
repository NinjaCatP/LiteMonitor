using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LiteMonitor
{
    // One engine graph; optionally with a dropdown in the title to choose which engine it shows.
    class EngineGraph : Control
    {
        public GpuInfo G; public string Label; public ComboBox Combo; int comboVer = -1;
        public Action<EngineGraph> Changed, Clicked;
        readonly ToolTip tip = new ToolTip();
        readonly PointF[] pts = new PointF[122], lpts = new PointF[120];
        Color C => Theme.GpuColor(G);

        public EngineGraph(GpuInfo g, string label, bool selectable)
        {
            G = g; Label = label;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg; Margin = new Padding(6);
            Cursor = Cursors.Hand; tip.SetToolTip(this, "Click to see which apps use this engine");
            if (selectable)
            {
                Combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Card, ForeColor = Theme.Text, Width = 170, Location = new Point(8, 6) };
                Combo.SelectedIndexChanged += (s, e) => { if (Combo.SelectedItem is string l && l != Label) { Label = l; Invalidate(); Changed?.Invoke(this); } };
                Controls.Add(Combo);
                SyncItems();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (e.Button == MouseButtons.Left) Clicked?.Invoke(this); }
        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }

        public void SyncItems()
        {
            if (Combo == null || comboVer == G.EngVersion || Combo.DroppedDown) return;
            comboVer = G.EngVersion;
            Combo.BeginUpdate(); Combo.Items.Clear();
            foreach (var e in G.Engines) Combo.Items.Add(e.Label);
            if (!Combo.Items.Contains(Label)) Combo.Items.Insert(0, Label); // not seen yet; keep the choice
            Combo.SelectedItem = Label; Combo.EndUpdate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                var g = e.Graphics; float s = DeviceDpi / 96f; float pad = 8 * s;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Tile.Round(new RectangleF(0, 0, Width - 1, Height - 1), 8 * s)) using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                var en = G.FindEngine(Label);
                using var fT = new Font("Segoe UI Semibold", 10f); using var fV = new Font("Segoe UI", 11f);
                using var bt = new SolidBrush(Theme.Text); using var bc = new SolidBrush(C);
                float top;
                if (Combo != null) top = Combo.Bottom + 4 * s;
                else { g.DrawString(Label, fT, bt, pad, pad); top = pad + fT.GetHeight(g) + 4 * s; }
                string v = en == null ? "–" : en.Value.ToString("0") + "%";
                var vs = g.MeasureString(v, fV);
                g.DrawString(v, fV, bc, Width - pad - vs.Width, Combo != null ? Combo.Top + 1 : pad - 2 * s);
                var gr = new RectangleF(pad, top, Width - 2 * pad, Height - top - pad);
                if (gr.Height < 10 || gr.Width < 10) return;
                using (var p = new Pen(Theme.Grid)) { for (int i = 1; i < 4; i++) g.DrawLine(p, gr.Left, gr.Top + gr.Height * i / 4, gr.Right, gr.Top + gr.Height * i / 4); g.DrawRectangle(p, gr.X, gr.Y, gr.Width, gr.Height); }
                if (en?.Hist == null) return;
                var d = en.Hist; int n = d.D.Length;
                for (int i = 0; i < n; i++) pts[i] = lpts[i] = new PointF(gr.Left + gr.Width * i / (n - 1), gr.Bottom - Math.Min(1f, d.D[(d.Head + 1 + i) % n] / 100f) * gr.Height);
                pts[n] = new PointF(gr.Right, gr.Bottom); pts[n + 1] = new PointF(gr.Left, gr.Bottom);
                using (var fb = new SolidBrush(Color.FromArgb(50, C))) g.FillPolygon(fb, pts);
                using (var pen = new Pen(C, 1.5f * s)) g.DrawLines(pen, lpts);
            }
            catch (Exception ex) { Log.Write(ex); }
        }
    }

    // Task Manager-style detail page for each GPU.
    class GpuPage : Panel
    {
        readonly Sampler S; readonly Settings set;
        readonly FlowLayoutPanel top; readonly StillFlowPanel body; readonly CheckBox chkAll;
        readonly List<Button> sel = new();
        readonly List<EngineGraph> graphs = new();
        Label header; Tile tDed, tSh, tStats;
        GpuInfo cur; int builtVer = -1; bool showAll, building;

        public Action<GpuInfo, string> OpenEngine; public Action<GpuInfo, bool> OpenMem;
        readonly ToolTip memTip = new ToolTip();
        public GpuPage(Sampler s, Settings st)
        {
            S = s; set = st; Dock = DockStyle.Fill; BackColor = Theme.Bg;
            body = new StillFlowPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(6) };
            top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 4, 6, 2), BackColor = Theme.Bg };
            header = new Label { Dock = DockStyle.Top, AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), ForeColor = Theme.Text, Padding = new Padding(10, 4, 6, 0), Text = "" };
            Controls.Add(body); Controls.Add(header); Controls.Add(top);
            // vertical scrolling only
            body.AutoScroll = false; body.HorizontalScroll.Maximum = 0; body.HorizontalScroll.Visible = false; body.HorizontalScroll.Enabled = false; body.AutoScroll = true;
            for (int i = 0; i < (S?.Gpus.Count ?? 0); i++)
            {
                int idx = i;
                var b = new Button { Text = S.Gpus[i].Name, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text, Margin = new Padding(2), Padding = new Padding(8, 2, 8, 2) };
                b.FlatAppearance.BorderSize = 0; b.Click += (o, e) => Select(idx);
                top.Controls.Add(b); sel.Add(b);
            }
            chkAll = new CheckBox { Text = "Show all engines", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(24, 6, 3, 3) };
            chkAll.CheckedChanged += (o, e) => { if (building || cur == null) return; set.GpuShowAll[cur.Name] = chkAll.Checked; set.Save(); Rebuild(); };
            top.Controls.Add(chkAll);
            body.Resize += (o, e) => Relayout();
            if (sel.Count == 0) body.Controls.Add(new Label { AutoSize = true, ForeColor = Theme.Dim, Text = "No graphics cards were found." });
            else Select(Math.Clamp(set.GpuSelected, 0, sel.Count - 1));
        }

        void Select(int i)
        {
            cur = S.Gpus[i]; set.GpuSelected = i;
            for (int k = 0; k < sel.Count; k++) sel[k].BackColor = k == i ? Color.FromArgb(55, 90, 140) : Color.FromArgb(30, 30, 34);
            building = true; chkAll.Checked = set.GpuShowAll.TryGetValue(cur.Name, out bool a) && a; building = false;
            Rebuild();
        }

        string[] Chosen()
        {
            if (set.GpuEngines.TryGetValue(cur.Name, out var c) && c != null && c.Length == 4) return c;
            return cur.Vendor == 0x8086 ? new[] { "3D", "Copy", "Video Decode", "Video Processing" } : new[] { "3D", "Copy", "Video Encode", "Video Decode" };
        }

        void Rebuild()
        {
            if (cur == null) return;
            try
            {
                body.SuspendLayout();
                var old = new Control[body.Controls.Count]; body.Controls.CopyTo(old, 0);
                body.Controls.Clear(); graphs.Clear();
                foreach (var c in old) c.Dispose();
                showAll = chkAll.Checked; builtVer = cur.EngVersion;
                header.Text = cur.Name; header.ForeColor = Theme.GpuColor(cur);
                if (showAll)
                {
                    foreach (var e in cur.Engines) graphs.Add(new EngineGraph(cur, e.Label, false));
                    if (graphs.Count == 0) graphs.Add(new EngineGraph(cur, "3D", false)); // engines appear after the first sample
                }
                else
                {
                    var ch = Chosen();
                    for (int i = 0; i < 4; i++)
                    {
                        var g = new EngineGraph(cur, ch[i], true);
                        g.Changed = OnPick; graphs.Add(g);
                    }
                }
                foreach (var g in graphs) { g.Clicked = eg => OpenEngine?.Invoke(eg.G, eg.Label); body.Controls.Add(g); }
                tDed = new Tile { Title = "Dedicated GPU memory", C1 = Theme.GpuColor(cur), S1 = cur.DedHist };
                tSh = new Tile { Title = "Shared GPU memory", C1 = Theme.GpuColor(cur), S1 = cur.ShHist };
                foreach (var mt in new[] { tDed, tSh })
                {
                    var t = mt; bool sh = t == tSh; t.Cursor = Cursors.Hand; memTip.SetToolTip(t, "Click to see which apps use this memory");
                    t.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) OpenMem?.Invoke(cur, sh); };
                }
                tStats = new Tile { Title = "Details", C1 = Theme.Text };
                body.Controls.Add(tDed); body.Controls.Add(tSh); body.Controls.Add(tStats);
                body.ResumeLayout();
                Relayout(); UpdateView();
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        void OnPick(EngineGraph g)
        {
            var arr = new string[4];
            for (int i = 0; i < 4 && i < graphs.Count; i++) arr[i] = graphs[i].Label;
            set.GpuEngines[cur.Name] = arr; set.Save();
        }

        void Relayout()
        {
            if (cur == null || graphs.Count == 0) return;
            try
            {
                var keep = body.AutoScrollPosition;
                body.SuspendLayout();
                float s = DeviceDpi / 96f;
                int m = graphs[0].Margin.Horizontal + 1; // every tile uses the same margin
                // usable width: always reserve the vertical scrollbar so the layout never overflows sideways
                int w = body.ClientSize.Width - body.Padding.Horizontal - (body.VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth) - 2;
                if (w < 100) w = 100;
                int cols = showAll ? Math.Max(1, w / (int)(300 * s)) : 2;
                while (cols > 1 && w / cols - m < (int)(160 * s)) cols--;
                int gw = Math.Max(40, w / cols - m);
                int gh = showAll ? (int)(170 * s) : Math.Max((int)(170 * s), Math.Min((int)(320 * s), (body.ClientSize.Height - (int)(420 * s)) / 2));
                for (int i = 0; i < graphs.Count; i++)
                {
                    graphs[i].Size = new Size(gw, gh);
                    body.SetFlowBreak(graphs[i], (i + 1) % cols == 0);
                }
                body.SetFlowBreak(graphs[graphs.Count - 1], true);
                bool two = w / 2 - m >= (int)(220 * s);
                int hw = Math.Max(40, two ? w / 2 - m : w - m);
                tDed.Size = tSh.Size = new Size(hw, (int)(180 * s));
                body.SetFlowBreak(tDed, !two); body.SetFlowBreak(tSh, true);
                tStats.Size = new Size(Math.Max(40, w - m), Math.Max(tStats.Height, (int)(200 * s)));
                body.ResumeLayout();
                body.AutoScrollPosition = new Point(-keep.X, -keep.Y);
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        public void UpdateView()
        {
            if (cur == null || tStats == null) return;
            try
            {
                if (cur.EngVersion != builtVer)
                {
                    if (showAll) { Rebuild(); return; }
                    builtVer = cur.EngVersion;
                    foreach (var g in graphs) g.SyncItems();
                }
                // NVIDIA: the driver's own load figure (same as the Performance tile); others: busiest engine like Task Manager
                double util = cur.IsNvml ? cur.Util : (cur.EngSampled ? cur.EngineMax : cur.Util);
                header.Text = cur.Name + "   " + util.ToString("0") + "%";
                tDed.Value = Theme.Bytes(cur.MemUsed) + " / " + Theme.Bytes(cur.MemTotal);
                tSh.Value = Theme.Bytes(cur.SharedUsed) + " / " + Theme.Bytes(cur.SharedTotal);
                var l = new List<string>(18)
                {
                    cur.IsNvml ? $"GPU load (driver): {cur.Util:0}%" : $"Utilization: {util:0}%",
                    cur.EngSampled ? $"Busiest engine: {cur.EngineMax:0}%" : "Busiest engine: –",
                    $"Dedicated GPU memory: {Theme.Bytes(cur.MemUsed)} / {Theme.Bytes(cur.MemTotal)}",
                    $"Shared GPU memory: {Theme.Bytes(cur.SharedUsed)} / {Theme.Bytes(cur.SharedTotal)}",
                    $"GPU memory: {Theme.Bytes(cur.MemUsed + cur.SharedUsed)} / {Theme.Bytes(cur.MemTotal + cur.SharedTotal)}",
                };
                if (cur.Temp >= 0) l.Add($"Temperature: {cur.Temp:0} °C");
                if (cur.DriverVer.Length > 0) l.Add("Driver version: " + cur.DriverVer);
                if (cur.DriverDate.Length > 0) l.Add("Driver date: " + cur.DriverDate);
                if (cur.PowerW >= 0) l.Add($"Power: {cur.PowerW:0} W" + (cur.PowerLimitW > 0 ? $" / {cur.PowerLimitW:0} W" : ""));
                if (cur.ClockCore >= 0) l.Add($"Core clock: {cur.ClockCore:0} MHz");
                if (cur.ClockMem >= 0) l.Add($"Memory clock: {cur.ClockMem:0} MHz");
                if (cur.Fan >= 0) l.Add($"Fan: {cur.Fan:0}%");
                if (cur.Enc >= 0) l.Add($"Encoder (NVIDIA): {cur.Enc:0}%");
                if (cur.Dec >= 0) l.Add($"Decoder (NVIDIA): {cur.Dec:0}%");
                tStats.Lines = l.ToArray();
                // make sure all detail lines fit
                float s = DeviceDpi / 96f; int need = (int)((40 + ((l.Count + 1) / 2) * 17) * s);
                if (tStats.Height < need) tStats.Height = need;
                foreach (var g in graphs) g.Invalidate();
                tDed.Invalidate(); tSh.Invalidate(); tStats.Invalidate();
            }
            catch (Exception ex) { Log.Write(ex); }
        }
    }
}
