using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor
{
    // Small popup: which apps are using one GPU engine right now (plus the last ~10 s, greyed).
    interface ILivePopup { void RefreshList(); }

    class EngineUsers : Form, ILivePopup
    {
        public readonly EngineSlot Slot;
        readonly Sampler S; readonly GpuInfo G; readonly Settings set; readonly Action<int> jump;
        readonly ListView lv; readonly Label empty, head;
        readonly List<(int pid, double val, bool recent)> rows = new();
        readonly Color dimRow = Color.FromArgb(120, 122, 130);

        public EngineUsers(Sampler s, GpuInfo g, EngineSlot slot, Settings st, Action<int> jumpTo)
        {
            S = s; G = g; Slot = slot; set = st; jump = jumpTo;
            Slot.Watchers++;
            Text = Short(g.Name) + " - " + slot.Label;
            Icon = MainForm.AppIcon; BackColor = Theme.Bg; ForeColor = Theme.Text; Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi; ShowInTaskbar = false; KeyPreview = true; StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(360, 220);
            Size = st.EngPopupW >= 360 && st.EngPopupH >= 220 ? new Size(st.EngPopupW, st.EngPopupH) : new Size(520, 360);
            head = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Theme.GpuColor(g), Font = new Font("Segoe UI Semibold", 10f), Padding = new Padding(6, 5, 0, 0), Text = Text };
            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
            lv.Columns.Add("App", 200); lv.Columns.Add("PID", 70, HorizontalAlignment.Right);
            lv.Columns.Add("% of this engine", 110, HorizontalAlignment.Right); lv.Columns.Add("VRAM on this card", 120, HorizontalAlignment.Right);
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(lv, true);
            lv.DoubleClick += (o, e) => { if (lv.SelectedItems.Count > 0 && lv.SelectedItems[0].Tag is int pid) jump?.Invoke(pid); };
            empty = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = Theme.Dim, Padding = new Padding(6, 4, 0, 0), Text = "Nothing is using this engine right now" };
            var hint = new Label { Dock = DockStyle.Bottom, Height = 20, ForeColor = Theme.Dim, Padding = new Padding(6, 2, 0, 0), Text = "Grey = used in the last 10 seconds. Double-click an app to find it in Processes. Esc closes." };
            Controls.Add(lv); Controls.Add(head); Controls.Add(empty); Controls.Add(hint);
            RefreshList();
        }

        static string Short(string n) => n.Replace("NVIDIA ", "").Replace("GeForce ", "").Replace("(R)", "").Trim();

        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { Close(); return; } base.OnKeyDown(e); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Slot.Watchers = Math.Max(0, Slot.Watchers - 1);
            if (Slot.Watchers == 0) Slot.Pids.Clear();
            try { if (WindowState == FormWindowState.Normal) { set.EngPopupW = Width; set.EngPopupH = Height; set.Save(); } } catch { }
            base.OnFormClosed(e);
        }

        List<int> stale;
        public void RefreshList()
        {
            if (IsDisposed) return;
            try
            {
                long now = Environment.TickCount64;
                rows.Clear();
                foreach (var kv in Slot.Pids)
                {
                    var pu = kv.Value;
                    if (pu.Value > 0) rows.Add((kv.Key, pu.Value, false));
                    else if (pu.LastSeen > 0 && now - pu.LastSeen < 10000) rows.Add((kv.Key, 0, true));
                    else (stale ??= new()).Add(kv.Key);
                }
                if (stale != null && stale.Count > 0) { foreach (var p in stale) Slot.Pids.Remove(p); stale.Clear(); }
                rows.Sort((a, b) => a.recent != b.recent ? a.recent.CompareTo(b.recent) : b.val.CompareTo(a.val));
                head.Text = Text + "   " + Slot.Value.ToString("0") + "%";
                empty.Visible = rows.Count == 0 || rows[0].recent;

                int selPid = lv.SelectedItems.Count > 0 && lv.SelectedItems[0].Tag is int sp ? sp : -1;
                int before = ListScroll.Get(lv);
                lv.BeginUpdate();
                for (int k = 0; k < rows.Count; k++)
                {
                    var r = rows[k];
                    ulong vram = 0; foreach (var gp in G.Procs) if (gp.Pid == r.pid) { vram = gp.Dedicated; break; }
                    string name = S.ProcName(r.pid);
                    string pct = r.val.ToString("0.0"), vr = vram > 0 ? Theme.Bytes(vram) : "";
                    ListViewItem it;
                    if (k < lv.Items.Count) it = lv.Items[k];
                    else { it = new ListViewItem(new[] { "", "", "", "" }); lv.Items.Add(it); }
                    if (it.SubItems[0].Text != name) it.SubItems[0].Text = name;
                    string pidS = r.pid.ToString(); if (it.SubItems[1].Text != pidS) it.SubItems[1].Text = pidS;
                    if (it.SubItems[2].Text != pct) it.SubItems[2].Text = pct;
                    if (it.SubItems[3].Text != vr) it.SubItems[3].Text = vr;
                    it.Tag = r.pid;
                    var fc = r.recent ? dimRow : Theme.Text; if (it.ForeColor != fc) it.ForeColor = fc;
                    bool sel = r.pid == selPid; if (it.Selected != sel) it.Selected = sel;
                }
                while (lv.Items.Count > rows.Count) lv.Items.RemoveAt(lv.Items.Count - 1);
                lv.EndUpdate();
                ListScroll.Restore(lv, before);
            }
            catch (Exception ex) { Log.Write(ex); }
        }
    }
}
