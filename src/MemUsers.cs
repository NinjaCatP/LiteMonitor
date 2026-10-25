using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LiteMonitor
{
    // Popup: which apps use a card's dedicated or shared GPU memory.
    class MemUsers : Form, ILivePopup
    {
        public readonly GpuInfo G; public readonly bool Shared;
        readonly Sampler S; readonly Settings set; readonly Action<int> jump;
        readonly ListView lv; readonly Label empty, head, foot;
        readonly List<GpuProc> rows = new();
        readonly string kind;

        public MemUsers(Sampler s, GpuInfo g, bool shared, Settings st, Action<int> jumpTo)
        {
            S = s; G = g; Shared = shared; set = st; jump = jumpTo;
            kind = shared ? "Shared GPU memory" : "Dedicated GPU memory";
            Text = g.Name.Replace("NVIDIA ", "").Replace("GeForce ", "").Replace("(R)", "").Trim() + " - " + kind;
            Icon = MainForm.AppIcon; BackColor = Theme.Bg; ForeColor = Theme.Text; Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi; ShowInTaskbar = false; KeyPreview = true; StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(380, 220);
            Size = st.MemPopupW >= 380 && st.MemPopupH >= 220 ? new Size(st.MemPopupW, st.MemPopupH) : new Size(600, 380);
            head = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Theme.GpuColor(g), Font = new Font("Segoe UI Semibold", 10f), Padding = new Padding(6, 5, 0, 0), Text = Text };
            lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
            lv.Columns.Add("App", 200); lv.Columns.Add("PID", 70, HorizontalAlignment.Right);
            lv.Columns.Add(shared ? "Shared" : "Dedicated", 100, HorizontalAlignment.Right);
            lv.Columns.Add("% of card", 80, HorizontalAlignment.Right);
            lv.Columns.Add(shared ? "Dedicated" : "Shared", 100, HorizontalAlignment.Right);
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(lv, true);
            lv.DoubleClick += (o, e) => { if (lv.SelectedItems.Count > 0 && lv.SelectedItems[0].Tag is int pid) jump?.Invoke(pid); };
            foot = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = Theme.Text, Padding = new Padding(6, 4, 0, 0) };
            empty = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = Theme.Dim, Padding = new Padding(6, 3, 0, 0), Text = "No apps are using this memory right now (it can take a second to load)" };
            var hint = new Label { Dock = DockStyle.Bottom, Height = 20, ForeColor = Theme.Dim, Padding = new Padding(6, 2, 0, 0), Text = "The rest of the used memory is reserved by Windows and the driver. Double-click an app to find it in Processes. Esc closes." };
            Controls.Add(lv); Controls.Add(head); Controls.Add(empty); Controls.Add(foot); Controls.Add(hint);
            RefreshList();
        }

        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { Close(); return; } base.OnKeyDown(e); }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { if (WindowState == FormWindowState.Normal) { set.MemPopupW = Width; set.MemPopupH = Height; set.Save(); } } catch { }
            base.OnFormClosed(e);
        }

        ulong Amt(GpuProc p) => Shared ? p.Shared : p.Dedicated;
        int Cmp(GpuProc a, GpuProc b) => Amt(b).CompareTo(Amt(a));
        Comparison<GpuProc> cmp;

        public void RefreshList()
        {
            if (IsDisposed) return;
            try
            {
                ulong used = Shared ? G.SharedUsed : G.MemUsed, total = Shared ? G.SharedTotal : G.MemTotal;
                head.Text = Text + "   " + Theme.Bytes(used) + " / " + Theme.Bytes(total);
                rows.Clear(); ulong sum = 0;
                foreach (var p in G.Procs) if (Amt(p) > 0) { rows.Add(p); sum += Amt(p); }
                cmp ??= Cmp; rows.Sort(cmp);
                foot.Text = $"Listed apps: {Theme.Bytes(sum)} of {Theme.Bytes(used)} used";
                empty.Visible = rows.Count == 0;
                int selPid = lv.SelectedItems.Count > 0 && lv.SelectedItems[0].Tag is int sp ? sp : -1;
                int before = ListScroll.Get(lv);
                lv.BeginUpdate();
                for (int k = 0; k < rows.Count; k++)
                {
                    var r = rows[k];
                    string[] v = { S.ProcName(r.Pid), r.Pid.ToString(), Theme.Bytes(Amt(r)), total > 0 ? (100.0 * Amt(r) / total).ToString("0.0") + "%" : "",
                                   (Shared ? r.Dedicated : r.Shared) > 0 ? Theme.Bytes(Shared ? r.Dedicated : r.Shared) : "" };
                    ListViewItem it;
                    if (k < lv.Items.Count) it = lv.Items[k]; else { it = new ListViewItem(new[] { "", "", "", "", "" }); lv.Items.Add(it); }
                    for (int c = 0; c < v.Length; c++) if (it.SubItems[c].Text != v[c]) it.SubItems[c].Text = v[c];
                    it.Tag = r.Pid;
                    bool sel = r.Pid == selPid; if (it.Selected != sel) it.Selected = sel;
                }
                while (lv.Items.Count > rows.Count) lv.Items.RemoveAt(lv.Items.Count - 1);
                lv.EndUpdate();
                ListScroll.Restore(lv, before);
            }
            catch (Exception ex) { Log.Write(ex); }
        }
    }
}
