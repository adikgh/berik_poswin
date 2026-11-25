using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BerikPOS
{
    // Программаның өз терезелері касса сайтының дизайнымен (user 2026-11-25: «Windows дизайны ұнамайды, сайттағыдай болсын»):
    // сұр фон + ақ карточкалар, бір жасыл акцент, қара негізгі батырма, дөңгелек бұрыштар, Nunito (болмаса Segoe UI).
    static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(246, 247, 245), Card = Color.White, Line = Color.FromArgb(230, 232, 228),
            Pill = Color.FromArgb(241, 243, 240), PillH = Color.FromArgb(233, 236, 232), Text = Color.FromArgb(28, 31, 29),
            Muted = Color.FromArgb(118, 124, 120), Green = Color.FromArgb(14, 167, 104), GreenInk = Color.FromArgb(8, 121, 75),
            GreenBg = Color.FromArgb(227, 245, 236), Red = Color.FromArgb(220, 38, 38), RedBg = Color.FromArgb(253, 236, 236), Dark = Color.FromArgb(28, 31, 29);

        // Nunito программаның ішінде (EmbeddedResource): GDI (TextRenderer) үшін AddFontMemResourceEx, GDI+ үшін PrivateFontCollection
        [DllImport("gdi32.dll")] static extern IntPtr AddFontMemResourceEx(IntPtr pb, uint cb, IntPtr pdv, ref uint pc);
        static readonly PrivateFontCollection pfc = new PrivateFontCollection();
        static FontFamily reg, semi;
        static bool loaded;
        static void Load()
        {
            if (loaded) return; loaded = true;
            foreach (var n in new[] { "Nunito-Regular.ttf", "Nunito-SemiBold.ttf" })
            {
                try
                {
                    using (var st = typeof(Ui).Assembly.GetManifestResourceStream(n))
                    {
                        if (st == null) continue;
                        var b = new byte[st.Length]; st.Read(b, 0, b.Length);
                        IntPtr p = Marshal.AllocCoTaskMem(b.Length); Marshal.Copy(b, 0, p, b.Length); // pfc үшін жад босатылмайды
                        pfc.AddMemoryFont(p, b.Length);
                        try { uint c = 0; AddFontMemResourceEx(p, (uint)b.Length, IntPtr.Zero, ref c); } catch { }
                    }
                }
                catch { }
            }
            foreach (var f in pfc.Families) { if (f.Name == "Nunito") reg = f; else if (f.Name == "Nunito SemiBold") semi = f; }
            try
            {
                using (var fc = new InstalledFontCollection())
                    foreach (var f in fc.Families) { if (reg == null && f.Name == "Nunito") reg = f; if (semi == null && f.Name == "Nunito SemiBold") semi = f; }
            }
            catch { }
        }
        public static string Family { get { Load(); return reg != null ? "Nunito" : "Segoe UI"; } }
        public static Font F(float size, bool bold = false)
        {
            Load();
            if (bold && semi != null) return new Font(semi, size);
            if (reg != null) return new Font(reg, size, bold ? FontStyle.Bold : FontStyle.Regular);
            return new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
        }
        public static Font Semi(float size)
        {
            Load();
            if (semi != null) return new Font(semi, size);
            try { return new Font(reg != null ? "Nunito" : "Segoe UI Semibold", size, reg != null ? FontStyle.Bold : FontStyle.Regular); } catch { return F(size, true); }
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
        public static void Box(Graphics g, Rectangle r, float rad, Color fill, Color? border = null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rf = new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1f, r.Height - 1f);
            using (var p = Round(rf, rad))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                if (border.HasValue) using (var pen = new Pen(border.Value)) g.DrawPath(pen, p);
            }
        }
        public static void Txt(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags fl)
        {
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            TextRenderer.DrawText(g, s, f, r, c, fl | TextFormatFlags.NoPadding);
        }

        // Көп жолды мәтін: биіктігі алдын ала есептеледі (AutoSize + MaximumSize кейде бір жолға қысқартады)
        public static Label Para(string text, Font font, Color color, int width)
        {
            var sz = TextRenderer.MeasureText(text ?? "", font, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            return new Label { Text = text, Font = font, ForeColor = color, AutoSize = false, Size = new Size(width, sz.Height + 4), BackColor = Color.Transparent, UseCompatibleTextRendering = false };
        }

        // ===== Хабар терезесі (MessageBox орнына) =====
        public static DialogResult Msg(IWin32Window owner, string text, string title = "Berik POS", bool yesNo = false, bool error = false)
        {
            using (var f = new UiForm(title, 460))
            {
                var lbl = Para(text, F(11f), error ? Red : Text, f.Inner);
                f.AddBody(lbl, 8);
                if (yesNo)
                {
                    var yes = new UiButton("Иә", UiButton.Kind.Dark) { DialogResult = DialogResult.Yes };
                    var no = new UiButton("Жоқ", UiButton.Kind.Light) { DialogResult = DialogResult.No };
                    f.AddFoot(yes, no); f.AcceptButton = yes; f.CancelButton = no;
                }
                else
                {
                    var ok = new UiButton("Жақсы", UiButton.Kind.Dark) { DialogResult = DialogResult.OK };
                    f.AddFoot(ok); f.AcceptButton = ok;
                }
                f.Finish();
                if (owner == null) { f.StartPosition = FormStartPosition.CenterScreen; f.ShowInTaskbar = true; }
                return owner != null ? f.ShowDialog(owner) : f.ShowDialog();
            }
        }
    }

    // Жақтаусыз терезе: өз тақырыбы, × батырмасы, сұр фон, көлеңке, дөңгелек бұрыш (Windows 11), тақырыптан сүйреледі
    class UiForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

        public const int Pad = 24;
        public readonly int W;
        public int Inner => W - Pad * 2;
        readonly Panel head = new Panel(), body = new Panel(), foot = new Panel();
        readonly Label title;
        readonly UiButton close;
        int by = 0;
        readonly List<Control> footBtns = new List<Control>();

        public UiForm(string text, int width)
        {
            W = width;
            Text = text;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; KeyPreview = true;
            StartPosition = FormStartPosition.CenterParent; BackColor = Ui.Bg; Font = Ui.F(10.5f);
            DoubleBuffered = true; AutoScaleMode = AutoScaleMode.Dpi;
            Padding = new Padding(1);

            head.Dock = DockStyle.Top; head.Height = 64; head.BackColor = Ui.Bg;
            title = new Label { Text = text, Font = Ui.Semi(15f), ForeColor = Ui.Text, AutoSize = true, Location = new Point(Pad, 20), BackColor = Color.Transparent };
            close = new UiButton("×", UiButton.Kind.Pill) { Size = new Size(40, 40), Location = new Point(W - Pad - 40 + 6, 14), Font = Ui.F(15f) };
            close.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            head.Controls.Add(title); head.Controls.Add(close);
            MouseEventHandler drag = (s, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } };
            head.MouseDown += drag; title.MouseDown += drag;

            body.Dock = DockStyle.Fill; body.BackColor = Ui.Bg; body.AutoScroll = true; body.Padding = new Padding(Pad, 0, Pad, 0);
            foot.Dock = DockStyle.Bottom; foot.BackColor = Ui.Bg; foot.Height = 84;

            Controls.Add(body); Controls.Add(foot); Controls.Add(head);
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } } // CS_DROPSHADOW

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int v = 2; DwmSetWindowAttribute(Handle, 33, ref v, 4); } catch { } // Windows 11: дөңгелек бұрыш
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Color.FromArgb(214, 218, 214))) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        // Денеге қосу (жоғарыдан төмен)
        public void AddBody(Control c, int gapAfter = 12)
        {
            c.Location = new Point(Pad, by);
            if (!(c is Label l && l.AutoSize)) c.Width = Inner;
            body.Controls.Add(c);
            by += c.Height + gapAfter;
        }
        // Екі баған: сол және оң жақтағы карточкалар (енін ColW-мен жасаңыз)
        public int ColW => (Inner - 16) / 2;
        public void AddCols(Control[] left, Control[] right, int gap = 12)
        {
            int ly = by, ry = by;
            foreach (var c in left) { c.Location = new Point(Pad, ly); body.Controls.Add(c); ly += c.Height + gap; }
            foreach (var c in right) { c.Location = new Point(Pad + ColW + 16, ry); body.Controls.Add(c); ry += c.Height + gap; }
            by = Math.Max(ly, ry);
        }
        public void AddFoot(params Control[] cs) { footBtns.AddRange(cs); foreach (var c in cs) foot.Controls.Add(c); }

        public void Finish(int maxBody = 620)
        {
            // Төменгі батырмалар: біріншісі (қара) кең, қалғандары өз енімен
            int x = Pad, gap = 10, fixedW = 0;
            for (int i = 1; i < footBtns.Count; i++) fixedW += footBtns[i].Width + gap;
            for (int i = 0; i < footBtns.Count; i++)
            {
                var b = footBtns[i];
                if (i == 0) b.Width = Math.Max(140, Inner - fixedW);
                b.Height = 52; b.Location = new Point(x, 12); x += b.Width + gap;
            }
            int bh = Math.Min(by, maxBody);
            ClientSize = new Size(W, head.Height + bh + foot.Height + 2);
            close.Left = W - Pad - close.Width + 8;
        }
    }

    // Батырма: қара (негізгі), ақ жиекті, сұр «таблетка», сілтеме
    class UiButton : Button
    {
        public enum Kind { Dark, Light, Pill, Link }
        public Kind K;
        bool hover, down;
        public UiButton(string text, Kind k)
        {
            Text = text; K = k;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand;
            Font = k == Kind.Link ? Ui.Semi(10f) : Ui.Semi(12f); Height = 52;
            BackColor = Ui.Bg;
            Width = Math.Max(k == Kind.Link ? 40 : 120, TextRenderer.MeasureText(text, Font).Width + (k == Kind.Link ? 8 : 44));
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Ui.Bg);
            var r = ClientRectangle; if (down) r.Inflate(-1, -1);
            Color fg = Ui.Text;
            switch (K)
            {
                case Kind.Dark: Ui.Box(g, r, 14, !Enabled ? Color.FromArgb(150, 153, 151) : hover ? Color.Black : Ui.Dark); fg = Color.White; break;
                case Kind.Light: Ui.Box(g, r, 14, hover ? Ui.Pill : Ui.Card, Ui.Line); break;
                case Kind.Pill: Ui.Box(g, r, 12, hover ? Ui.PillH : Ui.Pill); break;
                case Kind.Link: fg = hover ? Ui.Text : Ui.Muted; break;
            }
            var fl = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            Ui.Txt(g, Text, K == Kind.Link ? new Font(Font, FontStyle.Underline) : Font, fg, r, fl);
        }
    }

    // Карточка: ақ, дөңгелек, жиекті; ішінде «бөлім аты» және жолдар (сол жақта аты + түсіндірме, оң жақта басқару)
    class UiCard : Panel
    {
        public const int P = 18;
        int y = 6;
        readonly int w;
        bool first = true;
        public UiCard(string title, int width)
        {
            w = width; Width = width; BackColor = Ui.Card; DoubleBuffered = true;
            if (!string.IsNullOrEmpty(title))
            {
                var t = new Label { Text = title.ToUpperInvariant(), Font = Ui.Semi(8.5f), ForeColor = Ui.Muted, AutoSize = true, Location = new Point(P, 12), BackColor = Ui.Card };
                Controls.Add(t); y = 32;
            }
            Height = y + 6;
        }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(Ui.Bg); Ui.Box(e.Graphics, ClientRectangle, 16, Ui.Card, Ui.Line); }

        // col = true: басқару аттың астында, толық енімен
        public T Row<T>(string label, string note, T ctl, bool col = false) where T : Control
        {
            if (!first) Controls.Add(new Panel { BackColor = Ui.Line, Location = new Point(P, y), Size = new Size(w - P * 2, 1) });
            first = false;
            int top = y + 10;
            var l = new Label { Text = label, Font = Ui.Semi(11f), ForeColor = Ui.Text, AutoSize = true, BackColor = Ui.Card };
            Label n = null;
            int lh = l.PreferredHeight;
            if (!string.IsNullOrEmpty(note))
            {
                n = new Label { Text = note, Font = Ui.F(9f), ForeColor = Ui.Muted, AutoSize = true, MaximumSize = new Size(col ? w - P * 2 : w - P * 2 - ctl.Width - 16, 0), BackColor = Ui.Card };
                lh += n.PreferredHeight;
            }
            int ly;
            if (col)
            {
                ly = top;
                ctl.Location = new Point(P, top + lh + 8); ctl.Width = w - P * 2;
                y = ctl.Bottom + 10;
            }
            else
            {
                int h = Math.Max(lh, ctl.Height);
                ly = top + (h - lh) / 2;
                ctl.Location = new Point(w - P - ctl.Width, top + (h - ctl.Height) / 2);
                y = top + h + 10;
            }
            l.Location = new Point(P, ly); Controls.Add(l);
            if (n != null) { n.Location = new Point(P, ly + l.PreferredHeight); Controls.Add(n); }
            Controls.Add(ctl);
            Height = y + 4;
            return ctl;
        }
    }

    // Таңдау тізімі: ақ дөңгелек өріс + төмен бағыт; басқанда сайттағыдай ақ мәзір ашылады
    class UiPicker : Control
    {
        public readonly List<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>();
        int sel = -1; bool hover;
        public event EventHandler Changed;
        public UiPicker() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true); Height = 42; Width = 240; Cursor = Cursors.Hand; Font = Ui.F(10.5f); TabStop = true; }
        public void Add(string key, string label) => Items.Add(new KeyValuePair<string, string>(key, label));
        public int SelectedIndex { get => sel; set { sel = value; Invalidate(); } }
        public string Value { get => sel >= 0 && sel < Items.Count ? Items[sel].Key : null; set { sel = Items.FindIndex(x => x.Key == value); Invalidate(); } }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && sel < Items.Count - 1) { sel++; Invalidate(); Changed?.Invoke(this, EventArgs.Empty); }
            else if (e.KeyCode == Keys.Up && sel > 0) { sel--; Invalidate(); Changed?.Invoke(this, EventArgs.Empty); }
            else if (e.KeyCode == Keys.Space || e.KeyCode == Keys.F4) Drop();
            base.OnKeyDown(e);
        }
        protected override void OnClick(EventArgs e) { Focus(); Drop(); base.OnClick(e); }
        void Drop()
        {
            var m = new ContextMenuStrip { Renderer = new UiMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = false, Font = Font, BackColor = Ui.Card, Padding = new Padding(6) };
            for (int i = 0; i < Items.Count; i++)
            {
                int k = i;
                var it = new ToolStripMenuItem(Items[i].Value) { Padding = new Padding(8, 9, 8, 9), AutoSize = true, ForeColor = Ui.Text, Font = k == sel ? Ui.Semi(10.5f) : Font, Tag = k == sel };
                it.Click += (s, e) => { sel = k; Invalidate(); Changed?.Invoke(this, EventArgs.Empty); };
                m.Items.Add(it);
            }
            m.MinimumSize = new Size(Width, 0);
            m.Closed += (s, e) => BeginInvoke((Action)(() => m.Dispose()));
            m.Show(this, new Point(0, Height + 4));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Ui.Card);
            Ui.Box(g, ClientRectangle, 12, Ui.Card, Focused ? Ui.Green : hover ? Color.FromArgb(200, 205, 201) : Ui.Line);
            string t = Value != null ? Items[sel].Value : "—";
            Ui.Txt(g, t, Font, Ui.Text, new Rectangle(14, 0, Width - 44, Height), TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            int cx = Width - 22, cy = Height / 2;
            using (var p = new Pen(Ui.Muted, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(p, new[] { new Point(cx - 5, cy - 2), new Point(cx, cy + 3), new Point(cx + 5, cy - 2) });
        }
    }

    class UiMenuRenderer : ToolStripProfessionalRenderer
    {
        public UiMenuRenderer() : base(new Colors()) { RoundedEdges = true; }
        class Colors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Ui.Pill; public override Color MenuItemBorder => Ui.Pill;
            public override Color MenuBorder => Ui.Line; public override Color ToolStripDropDownBackground => Ui.Card;
            public override Color ImageMarginGradientBegin => Ui.Card; public override Color ImageMarginGradientMiddle => Ui.Card; public override Color ImageMarginGradientEnd => Ui.Card;
            public override Color MenuItemSelectedGradientBegin => Ui.Pill; public override Color MenuItemSelectedGradientEnd => Ui.Pill;
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            if (e.Item.Selected) Ui.Box(e.Graphics, r, 8, Ui.Pill);
            else if (e.Item.Tag is bool b && b) Ui.Box(e.Graphics, r, 8, Ui.GreenBg);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Tag is bool b && b) e.TextColor = Ui.GreenInk;
            base.OnRenderItemText(e);
        }
    }

    // Сегментті ауыстырғыш: сұр «таблетка», белсендісі жасыл
    class UiSeg : Control
    {
        public readonly List<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>();
        int sel; int hov = -1;
        public UiSeg(params string[] keyLabel)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Font = Ui.Semi(10f); Height = 40; Cursor = Cursors.Hand;
            for (int i = 0; i + 1 < keyLabel.Length; i += 2) Items.Add(new KeyValuePair<string, string>(keyLabel[i], keyLabel[i + 1]));
            int w = 6; foreach (var it in Items) w += TextRenderer.MeasureText(it.Value, Font).Width + 28;
            Width = w;
        }
        public string Value { get => Items[sel].Key; set { int i = Items.FindIndex(x => x.Key == value); sel = i < 0 ? 0 : i; Invalidate(); } }
        Rectangle Seg(int i)
        {
            int x = 3; for (int k = 0; k < i; k++) x += SegW(k) + 2;
            return new Rectangle(x, 3, SegW(i), Height - 6);
        }
        int SegW(int i) { int total = Width - 6 - (Items.Count - 1) * 2; int nat = 0; foreach (var it in Items) nat += TextRenderer.MeasureText(it.Value, Font).Width + 28; return (int)((TextRenderer.MeasureText(Items[i].Value, Font).Width + 28) * (double)total / nat); }
        protected override void OnMouseMove(MouseEventArgs e) { int h = -1; for (int i = 0; i < Items.Count; i++) if (Seg(i).Contains(e.Location)) h = i; if (h != hov) { hov = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { hov = -1; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e) { for (int i = 0; i < Items.Count; i++) if (Seg(i).Contains(e.Location)) { sel = i; Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Ui.Card);
            Ui.Box(g, ClientRectangle, 12, Ui.Pill);
            for (int i = 0; i < Items.Count; i++)
            {
                var r = Seg(i);
                if (i == sel) Ui.Box(g, r, 9, Ui.Green);
                Ui.Txt(g, Items[i].Value, Font, i == sel ? Color.White : i == hov ? Ui.Text : Ui.Muted, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    // Қосқыш (iOS сияқты), қосулысы жасыл
    class UiSwitch : Control
    {
        bool on;
        public UiSwitch() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); Size = new Size(50, 30); Cursor = Cursors.Hand; }
        public bool Checked { get => on; set { on = value; Invalidate(); } }
        protected override void OnClick(EventArgs e) { on = !on; Invalidate(); base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Ui.Card);
            Ui.Box(g, ClientRectangle, Height / 2f, on ? Ui.Green : Color.FromArgb(213, 217, 214));
            int d = Height - 6, x = on ? Width - d - 3 : 3;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillEllipse(b, x, 4, d, d);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x, 3, d, d);
        }
    }

    // Сан өрісі: ақ дөңгелек өріс, ішінде жиексіз мәтін өрісі және бірлік (мм, %)
    class UiNum : Control
    {
        readonly TextBox tb;
        readonly int min, max; readonly string unit;
        bool focus;
        public UiNum(int value, int min, int max, string unit = "")
        {
            this.min = min; this.max = max; this.unit = unit ?? "";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(this.unit == "" ? 92 : 118, 42); Font = Ui.F(11f);
            tb = new TextBox { BorderStyle = BorderStyle.None, Font = Font, ForeColor = Ui.Text, BackColor = Ui.Card, TextAlign = HorizontalAlignment.Center, Text = value.ToString(), MaxLength = 5 };
            tb.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            tb.GotFocus += (s, e) => { focus = true; Invalidate(); BeginInvoke((Action)tb.SelectAll); };
            tb.LostFocus += (s, e) => { focus = false; tb.Text = Value.ToString(); Invalidate(); };
            Controls.Add(tb);
            Layout2();
            Cursor = Cursors.IBeam;
        }
        void Layout2()
        {
            int uw = unit == "" ? 0 : 34;
            tb.Width = Width - 20 - uw; tb.Location = new Point(10, (Height - tb.PreferredHeight) / 2 + 1);
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (tb != null) Layout2(); }
        protected override void OnClick(EventArgs e) { tb.Focus(); base.OnClick(e); }
        public int Value { get { int v; if (!int.TryParse(tb.Text, out v)) v = min; return Math.Max(min, Math.Min(max, v)); } set => tb.Text = value.ToString(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Ui.Card);
            Ui.Box(g, ClientRectangle, 12, Ui.Card, focus ? Ui.Green : Ui.Line);
            if (unit != "") Ui.Txt(g, unit, Ui.F(9.5f), Ui.Muted, new Rectangle(Width - 40, 0, 32, Height), TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }
    }

    // Мәтін өрісі (кассаны байлау терезесі үшін)
    class UiText : Control
    {
        public readonly TextBox Box;
        bool focus;
        public UiText(string text = "", int maxLen = 200)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Height = 48; Font = Ui.F(11f); Cursor = Cursors.IBeam;
            Box = new TextBox { BorderStyle = BorderStyle.None, Font = Font, ForeColor = Ui.Text, BackColor = Ui.Card, Text = text, MaxLength = maxLen };
            Box.GotFocus += (s, e) => { focus = true; Invalidate(); };
            Box.LostFocus += (s, e) => { focus = false; Invalidate(); };
            Controls.Add(Box);
        }
        public override string Text { get => Box?.Text ?? ""; set { if (Box != null) Box.Text = value; } }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (Box != null) { Box.Width = Width - 28; Box.Location = new Point(14, (Height - Box.PreferredHeight) / 2 + 1); } }
        protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Ui.Bg);
            Ui.Box(g, ClientRectangle, 12, Ui.Card, focus ? Ui.Green : Ui.Line);
        }
    }
}

namespace BerikPOS
{
    // Іске қосылу экраны: ортада Berik белгісі, аты, күйі және жүгіретін жасыл жолақ
    class UiSplash : Control
    {
        readonly Timer t = new Timer { Interval = 16 };
        float ph;
        public string Version = "";
        public UiSplash()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg; Text = "Іске қосылуда...";
            t.Tick += (s, e) => { ph = (ph + 0.012f) % 1f; if (Visible) Invalidate(new Rectangle(0, Height / 2 + 60, Width, 20)); };
            t.Start();
        }
        public override string Text { get => base.Text; set { base.Text = value; Invalidate(); } }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); t.Enabled = Visible; }
        protected override void Dispose(bool d) { if (d) t.Dispose(); base.Dispose(d); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Ui.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = Width / 2, cy = Height / 2;
            // Белгі: жасыл дөңгелек шаршы, ішінде «B»
            var logo = new Rectangle(cx - 36, cy - 120, 72, 72);
            Ui.Box(g, logo, 20, Ui.Green);
            using (var f = Ui.F(30f, true)) Ui.Txt(g, "B", f, Color.White, logo, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (var f = Ui.Semi(22f)) Ui.Txt(g, "Berik POS", f, Ui.Text, new Rectangle(0, cy - 34, Width, 44), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (var f = Ui.F(11.5f)) Ui.Txt(g, Text, f, Ui.Muted, new Rectangle(20, cy + 14, Width - 40, 30), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            // Жүгіретін жолақ
            var track = new Rectangle(cx - 110, cy + 64, 220, 6);
            Ui.Box(g, track, 3, Ui.Line);
            float seg = 70, x = track.X - seg + (track.Width + seg) * ph;
            var s = RectangleF.Intersect(new RectangleF(x, track.Y, seg, track.Height), track);
            if (s.Width > 1) Ui.Box(g, Rectangle.Round(s), 3, Ui.Green);
            if (Version != "") using (var f = Ui.F(9f)) Ui.Txt(g, "нұсқа " + Version, f, Color.FromArgb(160, 165, 161), new Rectangle(0, Height - 44, Width, 24), TextFormatFlags.HorizontalCenter);
        }
    }
}
