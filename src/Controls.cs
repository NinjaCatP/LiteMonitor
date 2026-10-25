using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LiteMonitor
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(24, 24, 27), Card = Color.FromArgb(34, 35, 40), Grid = Color.FromArgb(52, 54, 60),
            Text = Color.FromArgb(230, 230, 235), Dim = Color.FromArgb(150, 152, 160),
            Cpu = Color.FromArgb(70, 160, 255), Mem = Color.FromArgb(190, 110, 255), Disk = Color.FromArgb(80, 210, 120),
            Net = Color.FromArgb(240, 170, 60), Gpu = Color.FromArgb(118, 200, 60), Gpu2 = Color.FromArgb(0, 200, 200), GpuIntel = Color.FromArgb(125, 195, 255), GpuAmd = Color.FromArgb(237, 28, 36);
        /// <summary>Brand color per GPU maker: NVIDIA green, Intel blue, AMD red.</summary>
        public static Color GpuColor(GpuInfo g) => g == null ? Gpu2 : g.Vendor == 0x10DE ? Gpu : g.Vendor == 0x8086 ? GpuIntel : g.Vendor == 0x1002 ? GpuAmd : Gpu2;
        public static string Bytes(double b)
        {
            if (b >= 1L << 30) return (b / (1L << 30)).ToString("0.0") + " GB";
            if (b >= 1 << 20) return (b / (1 << 20)).ToString("0.0") + " MB";
            if (b >= 1 << 10) return (b / (1 << 10)).ToString("0") + " KB";
            return b.ToString("0") + " B";
        }
        public static string Rate(double b) => Bytes(b) + "/s";
        public static string Bits(double bytesPerSec)
        {
            double b = bytesPerSec * 8;
            if (b >= 1e9) return (b / 1e9).ToString("0.00") + " Gbps";
            if (b >= 1e6) return (b / 1e6).ToString("0.0") + " Mbps";
            if (b >= 1e3) return (b / 1e3).ToString("0") + " Kbps";
            return b.ToString("0") + " bps";
        }
    }

    // Card with a title, a big value, a smooth graph (1 or 2 series) and detail lines. Fully custom drawn.
    class Tile : Control
    {
        public string Title = "", Sub = "", Value = "";
        public string[] Lines = new string[0];
        public Series S1, S2; public Color C1, C2; public double FixedMax = 100; // 0 = auto
        PointF[] pts = new PointF[122], lpts = new PointF[120];
        public Tile() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Theme.Bg; ForeColor = Theme.Text; Margin = new Padding(6); }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { Draw(e.Graphics); } catch (Exception ex) { Log.Write(ex); }
        }

        void Draw(Graphics g)
        {
            float s = DeviceDpi / 96f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var path = Round(r, 8 * s)) using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
            using var fT = new Font("Segoe UI Semibold", 11f); using var fV = new Font("Segoe UI", 15f); using var fS = new Font("Segoe UI", 8.5f);
            using var bt = new SolidBrush(Theme.Text); using var bd = new SolidBrush(Theme.Dim); using var ba = new SolidBrush(C1);
            float pad = 10 * s;
            g.DrawString(Title, fT, bt, pad, pad);
            var vs = g.MeasureString(Value, fV);
            g.DrawString(Value, fV, ba, Width - pad - vs.Width, pad - 4 * s);
            float y = pad + fT.GetHeight(g) + 1;
            if (Sub.Length > 0) { g.DrawString(Sub, fS, bd, new RectangleF(pad, y, Width - 2 * pad - vs.Width, fS.GetHeight(g) + 1)); }
            y += fS.GetHeight(g) + 4 * s;
            float lineH = fS.GetHeight(g) + 1;
            int rows = (Lines.Length + 1) / 2;
            float gh = Height - y - pad - rows * lineH - 4 * s;
            if (gh > 20 && S1 != null)
            {
                var gr = new RectangleF(pad, y, Width - 2 * pad, gh);
                using (var p = new Pen(Theme.Grid)) { for (int i = 1; i < 4; i++) g.DrawLine(p, gr.Left, gr.Top + gr.Height * i / 4, gr.Right, gr.Top + gr.Height * i / 4); g.DrawRectangle(p, gr.X, gr.Y, gr.Width, gr.Height); }
                double max = FixedMax;
                if (max <= 0) { max = Math.Max(S1.Max(), S2?.Max() ?? 0); max = Math.Max(max * 1.15, 1024); }
                Plot(g, gr, S1, C1, max, true);
                if (S2 != null) Plot(g, gr, S2, C2, max, false);
                if (FixedMax <= 0) g.DrawString(Theme.Rate(max), fS, bd, gr.Right - g.MeasureString(Theme.Rate(max), fS).Width - 2, gr.Top + 1);
                y += gh + 4 * s;
            }
            float colW = (Width - 2 * pad) / 2;
            for (int i = 0; i < Lines.Length; i++)
                g.DrawString(Lines[i], fS, bt, new RectangleF(pad + (i % 2) * colW, y + (i / 2) * lineH, colW, lineH));
        }

        void Plot(Graphics g, RectangleF gr, Series s, Color c, double max, bool fill)
        {
            int n = s.D.Length;
            for (int i = 0; i < n; i++)
            {
                float v = s.D[(s.Head + 1 + i) % n];
                pts[i] = lpts[i] = new PointF(gr.Left + gr.Width * i / (n - 1), gr.Bottom - (float)Math.Min(1, v / max) * gr.Height);
            }
            if (fill)
            {
                pts[n] = new PointF(gr.Right, gr.Bottom); pts[n + 1] = new PointF(gr.Left, gr.Bottom);
                using var fb = new SolidBrush(Color.FromArgb(50, c)); g.FillPolygon(fb, pts);
            }
            using var pen = new Pen(c, 1.6f * DeviceDpi / 96f);
            if (!fill) pen.DashStyle = DashStyle.Dash;
            g.DrawLines(pen, lpts);
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
    }

    // Grid of small per-core graphs
    class CoresTile : Control
    {
        public Sampler S;
        PointF[] pts = new PointF[120];
        public CoresTile() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Theme.Bg; Margin = new Padding(6); }
        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                var g = e.Graphics; float s = DeviceDpi / 96f; g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Tile.Round(new RectangleF(0, 0, Width - 1, Height - 1), 8 * s)) using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                if (S == null) return;
                int n = S.Cores; float pad = 10 * s;
                using var fT = new Font("Segoe UI Semibold", 11f); using var fS = new Font("Segoe UI", 7.5f);
                using var bt = new SolidBrush(Theme.Text); using var bd = new SolidBrush(Theme.Dim);
                g.DrawString("CPU cores (" + n + " logical)", fT, bt, pad, pad);
                float top = pad + fT.GetHeight(g) + 4 * s;
                float aw = Width - 2 * pad, ah = Height - top - pad;
                int cols = Math.Max(1, (int)Math.Round(Math.Sqrt(n * aw / Math.Max(1, ah) / 1.6)));
                cols = Math.Min(n, Math.Max(cols, 1)); int rows = (n + cols - 1) / cols;
                float cw = aw / cols, ch = ah / rows;
                using var grid = new Pen(Theme.Grid); using var pen = new Pen(Theme.Cpu, 1.2f * s); using var fill = new SolidBrush(Color.FromArgb(45, Theme.Cpu));
                for (int i = 0; i < n; i++)
                {
                    var r = new RectangleF(pad + (i % cols) * cw + 2, top + (i / cols) * ch + 2, cw - 4, ch - 4);
                    g.DrawRectangle(grid, r.X, r.Y, r.Width, r.Height);
                    var se = S.CoreHist[i]; int m = se.D.Length;
                    for (int k = 0; k < m; k++) pts[k] = new PointF(r.Left + r.Width * k / (m - 1), r.Bottom - Math.Min(1f, se.D[(se.Head + 1 + k) % m] / 100f) * r.Height);
                    g.DrawLines(pen, pts);
                    g.DrawString(i + ": " + S.CoreUtil[i].ToString("0") + "%", fS, bd, r.X + 2, r.Y + 1);
                }
            }
            catch (Exception ex) { Log.Write(ex); }
        }
    }

    // Small always-on-top overlay
    class Overlay : Form
    {
        public string[] Lines = { "LiteMonitor" };
        readonly Settings set;
        public Overlay(Settings st)
        {
            set = st;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; Opacity = 0.85;
            BackColor = Color.FromArgb(16, 16, 18); StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Size = new Size(230, 96);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = st.OverlayX >= 0 ? new Point(st.OverlayX, st.OverlayY) : new Point(wa.Right - 260, wa.Top + 40);
            bool onScreen = false; foreach (var sc in Screen.AllScreens) if (sc.WorkingArea.Contains(Location)) onScreen = true;
            if (!onScreen) Location = new Point(wa.Right - 260, wa.Top + 40);
            MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } };
            LocationChanged += (s, e) => { if (set != null && Visible) { set.OverlayX = Left; set.OverlayY = Top; } };
        }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; } } // TOOLWINDOW | NOACTIVATE
        protected override bool ShowWithoutActivation => true;
        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                var g = e.Graphics; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var f = new Font("Consolas", 10.5f, FontStyle.Bold); using var b = new SolidBrush(Color.FromArgb(120, 230, 140));
                float y = 6, lh = f.GetHeight(g) + 1, w = 0;
                foreach (var l in Lines) { g.DrawString(l, f, b, 8, y); w = Math.Max(w, g.MeasureString(l, f).Width); y += lh; }
                var want = new Size((int)w + 18, (int)y + 6);
                if (Math.Abs(want.Width - Width) > 4 || want.Height != Height) Size = want;
            }
            catch (Exception ex) { Log.Write(ex); }
        }
    }
}
