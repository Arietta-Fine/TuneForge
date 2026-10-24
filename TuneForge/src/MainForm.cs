using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace TuneForge
{
    enum DragMode { None, CreateNote, MoveNote, ResizeNote, PanView, SeekHead, TrackVolume, TrackPan, BpmDrag, BottomVolume, BottomPan, NoteVel }

    class UiBtn
    {
        public RectangleF R;
        public string IconKind;
        public string Label;
        public Action Click;
        public bool Active;
        public bool Hover;
        public int Style;     // 0 = 图标, 1 = 胶囊, 2 = 圆形, 3 = 小方块
        public bool Visible = true;
        public string Tip;
    }

    public partial class MainForm : Form
    {
        // 系统内存（面板显示用）
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        internal static void SysMem(out long total, out long avail)
        {
            total = 0; avail = 0;
            try
            {
                MEMORYSTATUSEX m = new MEMORYSTATUSEX();
                m.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref m))
                {
                    total = (long)m.ullTotalPhys;
                    avail = (long)m.ullAvailPhys;
                }
            }
            catch { }
        }

        const int TOP_H = 58;
        const int BOT_H = 48;
        const int TRACK_W = 244;
        const int RULER_H = 30;
        const int KEY_W = 68;
        const int ROW_H = 100;

        Project project;
        AudioEngine engine;
        Timer uiTimer;
        Bitmap cache;
        bool cacheDirty = true;

        RectangleF topRect, bottomRect, trackRect, rulerRect, keyRect, gridRect;

        float pxPerBeat = 54f;
        double viewBeat = 0;
        int viewY = 0;
        double rowH = 18;
        int selectedTrack = 0;
        Note selectedNote;
        Note hoverNote;
        Note rcNote; DateTime rcTime;
        ContextMenuStrip noteMenuOpen;
        Note pendingMenuNote;
        System.Windows.Forms.Timer menuTimer;

        List<UiBtn> btns = new List<UiBtn>();
        UiBtn btnPlay, btnStop, btnLoop, btnMetro, btnBpm, btnBpmUp, btnBpmDown;
        UiBtn btnSave, btnOpen, btnExport, btnHelp, btnAddTrack, btnNew, btnMenu;
        UiBtn btnInstrument, btnMute, btnSolo;
        RectangleF volumeRect, panRect, velRect;
        RectangleF dragSliderRect;   // 本次拖动使用的滑块矩形（区分轨道卡片 / 底栏）
        RectangleF bpmRect, timeSigRect, barsRect;

        DragMode drag = DragMode.None;
        Point dragStartPx;
        float dragStartX, dragStartY;
        double dragValue;
        bool dragMoved;
        Note dragNote;
        double dragNoteOrigStart;
        double dragNoteOrigLen;
        int dragNoteOrigPitch;
        double createAnchorBeat;
        int createPitch;
        double panDragStart;

        double headBeat = 0;
        bool wasPlaying = false;
        internal static int DesignTheme = -1;
        bool showHelp = false;
        double bpmDragValue;
        int hoverTrack = -1;
        bool scrollTracks = false;

        string statusText = "";
        string folderDefault = "";

        ContextMenuStrip instMenu;

        static float U(float v) { return v * Theme.S; }
        static int UI(float v) { return (int)Math.Round(v * Theme.S); }

        public MainForm()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Text = "编曲工坊";
            BackColor = Theme.Bg0;
            ForeColor = Theme.Text;
            KeyPreview = true;
            MinimumSize = new Size(1120, 720);
            StartPosition = FormStartPosition.CenterScreen;

            project = Demo.Build();
            engine = new AudioEngine();
            engine.Attach(project);
            engine.SetLoop(true, project.LengthBeats);
            engine.SetLength(project.LengthBeats);

            // 工程统一放在 exe 同级的「工程」文件夹，便于查找与备份
            try
            {
                folderDefault = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "工程");
                if (!Directory.Exists(folderDefault)) Directory.CreateDirectory(folderDefault);
            }
            catch
            {
                folderDefault = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                if (string.IsNullOrEmpty(folderDefault))
                    folderDefault = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            BuildInstrumentMenu();

            uiTimer = new Timer();
            uiTimer.Interval = 28;
            uiTimer.Tick += OnUiTick;
            uiTimer.Start();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            float dpi = 96f;
            try
            {
                using (Graphics g = CreateGraphics()) dpi = g.DpiY;
            }
            catch { }
            Theme.S = dpi / 96f;
            if (Theme.S < 0.75f) Theme.S = 0.75f;
            if (Theme.S > 3f) Theme.S = 3f;

            Theme.Apply(MainForm.DesignTheme >= 0 ? MainForm.DesignTheme : LoadSettings());
            BackColor = Theme.Bg0;

            ClientSize = new Size(UI(1400), UI(880));
            MinimumSize = new Size(UI(1120), UI(720));

            pxPerBeat = U(54);
            rowH = U(18);
            CenterPitch(60);
            LayoutUi();
            PositionForm.centerOnScreen(this);

            TryDarkTitleBar();
            ApplyIcon();
            cacheDirty = true;

            // 采样音源：后台扫描「音源」目录，避免启动卡顿
            string sf2Dir = System.IO.Path.Combine(Application.StartupPath, "音源");
            if (System.IO.Directory.Exists(sf2Dir))
            {
                System.Threading.Thread t = new System.Threading.Thread(delegate ()
                {
                    Synth.LoadSamples(sf2Dir);
                    if (!IsDisposed)
                    {
                        try
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                if (Synth.SampleInfo.Length > 0) statusText = "采样音源已就绪：" + Synth.SampleInfo;
                                else statusText = "音源目录存在，但没有可用的采样文件";
                                ApplySampleRenames();
                                engine.Sync();
                                cacheDirty = true;
                                Invalidate();
                            });
                        }
                        catch { }
                    }
                });
                t.IsBackground = true;
                t.Priority = System.Threading.ThreadPriority.BelowNormal;
                t.Start();
            }
            else statusText = "未发现采样音源，使用合成音色";
        }

        void CenterPitch(int pitch)
        {
            int viewH = (int)(gridRect.Height > 0 ? gridRect.Height : ClientSize.Height - U(TOP_H + BOT_H + RULER_H));
            int target = (int)((127 - pitch) * rowH - viewH * 0.45);
            viewY = target;
            ClampView();
        }

        void ClampView()
        {
            int totalH = (int)(128 * rowH);
            int viewH = (int)gridRect.Height;
            if (viewH <= 0) viewH = (int)(ClientSize.Height - U(TOP_H + BOT_H + RULER_H));
            if (viewY < 0) viewY = 0;
            if (viewY > totalH - viewH) viewY = totalH - viewH;
            if (viewY < 0) viewY = 0;
        }

        void LayoutUi()
        {
            float W = ClientSize.Width, H = ClientSize.Height;
            topRect = new RectangleF(0, 0, W, U(TOP_H));
            bottomRect = new RectangleF(0, H - U(BOT_H), W, U(BOT_H));
            float midY = topRect.Bottom;
            float midH = bottomRect.Top - midY;
            trackRect = new RectangleF(0, midY, U(TRACK_W), midH);
            rulerRect = new RectangleF(trackRect.Right, midY, W - trackRect.Right, U(RULER_H));
            keyRect = new RectangleF(trackRect.Right, rulerRect.Bottom, U(KEY_W), midH - U(RULER_H));
            gridRect = new RectangleF(keyRect.Right, keyRect.Top, W - keyRect.Right, keyRect.Height);
            ClampView();
            BuildButtons();
            cacheDirty = true;
        }

        void BuildButtons()
        {
            btns.Clear();
            float cy = topRect.Height * 0.5f;
            float cx = ClientSize.Width * 0.5f - U(26);

            float pd = U(46);
            btnPlay = AddBtn(new RectangleF(cx - U(176), cy - pd * 0.5f, pd, pd), "play", "", 2);
            btnStop = AddBtn(new RectangleF(cx - U(120), cy - U(17), U(34), U(34)), "pause", "", 2);
            btnLoop = AddBtn(new RectangleF(cx - U(80), cy - U(17), U(34), U(34)), "loop", "", 2);
            btnLoop.Active = true;

            bpmRect = new RectangleF(cx - U(34), cy - U(17), U(108), U(34));
            btnBpm = AddBtn(bpmRect, "", "", 1);
            btnBpmUp = AddBtn(new RectangleF(bpmRect.Right - U(22), bpmRect.Y + U(4), U(18), U(13)), "up", "", 3);
            btnBpmDown = AddBtn(new RectangleF(bpmRect.Right - U(22), bpmRect.Bottom - U(17), U(18), U(13)), "down", "", 3);

            timeSigRect = new RectangleF(cx + U(84), cy - U(17), U(54), U(34));
            barsRect = new RectangleF(cx + U(148), cy - U(17), U(82), U(34));

            float right = ClientSize.Width - U(18);
            float bs = U(38);
            btnMenu = AddBtn(new RectangleF(right - bs, cy - bs * 0.5f, bs, bs), "more", "", 0); right -= bs + U(6);
            btnHelp = AddBtn(new RectangleF(right - bs, cy - bs * 0.5f, bs, bs), "help", "", 0); right -= bs + U(6);
            btnExport = AddBtn(new RectangleF(right - bs, cy - bs * 0.5f, bs, bs), "export", "", 0); right -= bs + U(6);
            btnOpen = AddBtn(new RectangleF(right - bs, cy - bs * 0.5f, bs, bs), "open", "", 0); right -= bs + U(6);
            btnSave = AddBtn(new RectangleF(right - bs, cy - bs * 0.5f, bs, bs), "save", "", 0); right -= bs + U(6);
            btnNew = AddBtn(new RectangleF(right - bs, cy - bs * 0.5f, bs, bs), "new", "", 0);
            btnMenu.Tip = "主菜单";
            btnHelp.Tip = "操作指南 (F1)";
            btnExport.Tip = "导出 WAV (Ctrl+E)";
            btnOpen.Tip = "打开工程 (Ctrl+O)";
            btnSave.Tip = "保存工程 (Ctrl+S)";
            btnNew.Tip = "新建工程 (Ctrl+N)";
            right -= bs + U(14);
            btnMetro = AddBtn(new RectangleF(right - U(96), cy - U(17), U(96), U(34)), "metro", "节拍器", 1);

            float bcy = bottomRect.Y + bottomRect.Height * 0.5f;
            float bx = U(150);
            btnInstrument = AddBtn(new RectangleF(bx, bcy - U(15), U(206), U(30)), "", "音色", 1);
            btnInstrument.Click = ShowInstrumentMenu;

            btnAddTrack = AddBtn(new RectangleF(trackRect.Right - U(40), trackRect.Top + U(9), U(26), U(26)), "plus", "", 3);

            btnPlay.Click = TogglePlay;
            btnStop.Click = StopPlayback;
            btnLoop.Click = ToggleLoop;
            btnMetro.Click = ToggleMetro;
            btnBpmUp.Click = delegate { ChangeBpm(1); };
            btnBpmDown.Click = delegate { ChangeBpm(-1); };
            btnSave.Click = delegate { SaveProject(false); };
            btnOpen.Click = OpenProject;
            btnExport.Click = ExportWav;
            btnHelp.Click = ToggleHelp;
            btnNew.Click = delegate { NewProject(); };
            btnMenu.Click = ShowMainMenu;

            volumeRect = new RectangleF(U(430), bcy - U(2), U(140), U(4));
            panRect = new RectangleF(U(650), bcy - U(2), U(120), U(4));
            velRect = new RectangleF(U(838), bcy - U(2), U(110), U(4));
        }

        UiBtn AddBtn(RectangleF r, string icon, string label, int style)
        {
            UiBtn b = new UiBtn();
            b.R = r; b.IconKind = icon; b.Label = label; b.Style = style;
            btns.Add(b);
            return b;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                EnsureCache(e.Graphics);
                if (cache != null) e.Graphics.DrawImageUnscaled(cache, 0, 0);
                if (!samplerOpen) DrawPlayhead(e.Graphics);
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tf-paint.log"), ex.ToString() + Environment.NewLine); } catch { }
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                LayoutUi();
                Invalidate();
            }
        }

        void EnsureCache(Graphics g)
        {
            if (cache == null || cache.Width != ClientSize.Width || cache.Height != ClientSize.Height)
            {
                if (cache != null) cache.Dispose();
                cache = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
                try { cache.SetResolution(g.DpiX, g.DpiY); } catch { }
                cacheDirty = true;
            }
            if (!cacheDirty) return;
            using (Graphics cg = Graphics.FromImage(cache))
            {
                cg.SmoothingMode = SmoothingMode.AntiAlias;
                cg.PixelOffsetMode = PixelOffsetMode.Half;
                cg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                cg.CompositingQuality = CompositingQuality.HighSpeed;
                RenderStatic(cg);
            }
            cacheDirty = false;
        }

        void RenderStatic(Graphics g)
        {
            g.Clear(Theme.Bg0);
            DrawTop(g);
            DrawTrackPanel(g);
            DrawRuler(g);
            DrawGrid(g);
            DrawKeyboard(g);
            DrawNotes(g);
            DrawBottom(g);
            if (showHelp) DrawHelp(g);
            if (samplerOpen) DrawSampler(g);
        }

        // ================= 顶栏 =================

        void DrawTop(Graphics g)
        {
            RectangleF r = topRect;
            using (LinearGradientBrush lg = new LinearGradientBrush(
                new RectangleF(0, 0, r.Width, r.Height + 1),
                Theme.TopA, Theme.TopB, LinearGradientMode.Vertical))
                g.FillRectangle(lg, r);

            using (Pen p = new Pen(Theme.Line))
                g.DrawLine(p, 0, r.Bottom - 1, r.Width, r.Bottom - 1);

            // Logo
            RectangleF logo = new RectangleF(U(18), U(11), U(36), U(36));
            using (LinearGradientBrush lg = new LinearGradientBrush(logo, Color.FromArgb(70, 220, 198), Color.FromArgb(78, 122, 240), 45f))
                Theme.FillRound(g, lg, logo, U(11));
            Theme.TextC(g, "\u266A", Theme.FB(17f), Color.White, new RectangleF(logo.X, logo.Y + U(1), logo.Width, logo.Height));

            Theme.Txt(g, "编曲工坊", Theme.FB(15f), Theme.Text, U(64), U(7));
            using (Font f = new Font("Segoe UI", 7.2f, FontStyle.Regular))
                Theme.Txt(g, "T U N E F O R G E   S T U D I O", f, Theme.TextFaint, U(65), U(34));

            DrawButton(g, btnPlay, (engine.IsPlaying ? "pause" : "play"), 2);
            DrawButton(g, btnStop, "stop", 2);
            DrawButton(g, btnLoop, "loop", 2);
            if (btnLoop.Active)
            {
                using (Pen p = new Pen(Theme.Accent, U(1.6f)))
                    g.DrawEllipse(p, btnLoop.R);
            }

            DrawBpmPanel(g);
            DrawChip(g, timeSigRect, project.BeatsPerBar + "/" + project.BeatUnit, "拍号");
            DrawChip(g, barsRect, project.Bars.ToString("0") + " 小节", "长度");

            DrawButton(g, btnMetro, "metro", 1);
            DrawButton(g, btnNew, "new", 0);
            DrawButton(g, btnSave, "save", 0);
            DrawButton(g, btnOpen, "open", 0);
            DrawButton(g, btnExport, "export", 0);
            DrawButton(g, btnHelp, "help", 0);
            DrawButton(g, btnMenu, "more", 0);

            DrawTopTip(g);
        }

        void DrawTopTip(Graphics g)
        {
            UiBtn[] all = new UiBtn[] { btnNew, btnSave, btnOpen, btnExport, btnHelp, btnMenu, btnMetro };
            for (int i = 0; i < all.Length; i++)
            {
                UiBtn b = all[i];
                if (b == null || b.Tip == null || !b.Hover) continue;
                Font f = Theme.F(8f);
                SizeF sz = g.MeasureString(b.Tip, f);
                float w = sz.Width + U(14);
                float h = U(22);
                float x = b.R.X + b.R.Width * 0.5f - w * 0.5f;
                float y = topRect.Bottom + U(3);
                x = Math.Max(U(4), Math.Min(x, ClientSize.Width - w - U(4)));
                Theme.FillRound(g, Theme.Alpha(Theme.Bg3, 240), new RectangleF(x, y, w, h), U(6));
                using (Pen p = new Pen(Theme.Alpha(Theme.Line, 220)))
                    Theme.DrawRound(g, p, new RectangleF(x, y, w, h), U(6));
                Theme.TextC(g, b.Tip, f, Theme.Text, new RectangleF(x, y + U(2), w, h - U(4)));
            }
        }

        void DrawBpmPanel(Graphics g)
        {
            RectangleF r = bpmRect;
            Theme.FillRound(g, Theme.Bg2, r, U(9));
            using (Pen p = new Pen(btnBpm.Hover ? Theme.Line : Theme.LineSoft))
                Theme.DrawRound(g, p, r, U(9));

            Theme.Txt(g, "BPM", Theme.F(6.8f), Theme.TextFaint, r.X + U(11), r.Y + U(5));
            Theme.TextC(g, project.Bpm.ToString("0"), Theme.FM(12.5f), Theme.Text,
                new RectangleF(r.X + U(6), r.Y + U(6), r.Width - U(34), r.Height - U(8)));

            DrawTinyArrow(g, btnBpmUp, true);
            DrawTinyArrow(g, btnBpmDown, false);
        }

        void DrawTinyArrow(Graphics g, UiBtn b, bool up)
        {
            Color c = b.Hover ? Theme.Text : Theme.TextDim;
            float cx = b.R.X + b.R.Width * 0.5f;
            float cy = b.R.Y + b.R.Height * 0.5f;
            float s = U(3.6f);
            using (SolidBrush br = new SolidBrush(c))
            {
                PointF[] pts = up
                    ? new PointF[] { new PointF(cx, cy - s), new PointF(cx - s, cy + s), new PointF(cx + s, cy + s) }
                    : new PointF[] { new PointF(cx, cy + s), new PointF(cx - s, cy - s), new PointF(cx + s, cy - s) };
                g.FillPolygon(br, pts);
            }
        }

        void DrawChip(Graphics g, RectangleF r, string value, string caption)
        {
            Theme.FillRound(g, Theme.Bg2, r, U(9));
            using (Pen p = new Pen(Theme.LineSoft)) Theme.DrawRound(g, p, r, U(9));
            Theme.Txt(g, caption, Theme.F(6.8f), Theme.TextFaint, r.X + U(11), r.Y + U(4));
            Theme.TextC(g, value, Theme.F(10.5f), Theme.Text, new RectangleF(r.X, r.Y + U(9), r.Width, r.Height - U(10)));
        }

        void DrawButton(Graphics g, UiBtn b, string icon, int style)
        {
            if (!b.Visible) return;
            RectangleF r = b.R;
            bool act = b.Active;
            bool hov = b.Hover;

            if (style == 2)
            {
                Color c1 = act ? Color.FromArgb(96, 235, 214) : Theme.Bg3;
                Color c2 = act ? Color.FromArgb(50, 190, 172) : Theme.Bg3;
                if (hov)
                {
                    c1 = act ? Color.FromArgb(116, 245, 224) : Theme.Bg4;
                    c2 = act ? Color.FromArgb(62, 205, 186) : Theme.Bg3;
                }
                using (LinearGradientBrush lg = new LinearGradientBrush(r, c1, c2, 90f))
                    g.FillEllipse(lg, r);
                if (!act)
                    using (Pen p = new Pen(Theme.Line)) g.DrawEllipse(p, r);
                DrawIcon(g, icon, new RectangleF(r.X + r.Width * 0.18f, r.Y + r.Height * 0.18f, r.Width * 0.64f, r.Height * 0.64f),
                    act ? Color.FromArgb(10, 30, 34) : Theme.Text);
            }
            else if (style == 0)
            {
                if (hov) Theme.FillRound(g, Theme.Bg3, r, U(9));
                DrawIcon(g, icon, new RectangleF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.24f, r.Width * 0.52f, r.Height * 0.52f),
                    hov ? Theme.Text : Theme.TextDim);
            }
            else if (style == 1)
            {
                Theme.FillRound(g, act ? Theme.AccentDeep : Theme.Bg2, r, U(8));
                if (!act) using (Pen p = new Pen(hov ? Theme.Line : Theme.LineSoft)) Theme.DrawRound(g, p, r, U(8));
                float iw = b.IconKind.Length > 0 ? U(17) : 0;
                if (iw > 0)
                    DrawIcon(g, b.IconKind, new RectangleF(r.X + U(11), r.Y + r.Height * 0.5f - iw * 0.5f, iw, iw),
                        act ? Theme.Text : Theme.TextDim);
                if (b.Label.Length > 0)
                {
                    Font f = Theme.F(9f);
                    if (act) f = Theme.FB(9f);
                    float tx = r.X + U(11) + iw;
                    Theme.TextL(g, b.Label, f, act ? Color.FromArgb(8, 26, 28) : Theme.TextDim,
                        new RectangleF(tx, r.Y, r.Width - (tx - r.X) - U(8), r.Height));
                }
            }
            else
            {
                if (hov) Theme.FillRound(g, Theme.Bg4, r, U(5));
                DrawIcon(g, icon, new RectangleF(r.X + r.Width * 0.22f, r.Y + r.Height * 0.22f, r.Width * 0.56f, r.Height * 0.56f),
                    hov ? Theme.Text : Theme.TextDim);
            }
        }

        // ================= 轨道栏 =================

        void DrawTrackPanel(Graphics g)
        {
            g.FillRectangle(new SolidBrush(Theme.PanelBg), trackRect);
            using (Pen p = new Pen(Theme.LineSoft))
                g.DrawLine(p, trackRect.Right - 1, trackRect.Top, trackRect.Right - 1, trackRect.Bottom);

            Theme.Txt(g, "轨道", Theme.FB(10f), Theme.TextDim, U(18), trackRect.Top + U(12));
            Theme.Txt(g, project.Tracks.Count.ToString(), Theme.F(8.5f), Theme.TextFaint, U(50), trackRect.Top + U(14));

            // 添加轨道按钮
            RectangleF add = btnAddTrack.R;
            Theme.FillRound(g, btnAddTrack.Hover ? Theme.Bg4 : Theme.Bg3, add, U(6));
            using (Pen p = new Pen(btnAddTrack.Hover ? Theme.Accent : Theme.TextDim, U(1.5f)))
            {
                float mx = add.X + add.Width * 0.5f, my = add.Y + add.Height * 0.5f;
                g.DrawLine(p, mx - U(5), my, mx + U(5), my);
                g.DrawLine(p, mx, my - U(5), mx, my + U(5));
            }

            float y = trackRect.Top + U(42);
            for (int i = 0; i < project.Tracks.Count; i++)
            {
                float rh = U(ROW_H);
                if (y + rh > trackRect.Bottom + U(4)) break;
                DrawTrackRow(g, i, new RectangleF(trackRect.X, y, trackRect.Width, rh));
                y += rh;
            }
        }

        void DrawTrackRow(Graphics g, int idx, RectangleF r)
        {
            Track t = project.Tracks[idx];
            bool sel = idx == selectedTrack;
            bool hov = idx == hoverTrack;
            Color col = Theme.TrackColors[t.ColorIndex % Theme.TrackColors.Length];

            Color bg = sel ? Theme.CardSel : (hov ? Theme.CardHov : Theme.CardBg);
            g.FillRectangle(new SolidBrush(bg), r);

            if (sel)
            {
                using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(r.X, r.Y, U(3), r.Height),
                    col, Theme.Alpha(col, 40), LinearGradientMode.Vertical))
                    g.FillRectangle(lg, r.X, r.Y, U(3), r.Height);
            }

            using (Pen p = new Pen(Theme.LineSoft))
                g.DrawLine(p, r.X + U(10), r.Bottom - 1, r.Right - U(10), r.Bottom - 1);

            // 名称
            Theme.TextW(g, t.Name, sel ? Theme.FB(11f) : Theme.F(11f), sel ? Theme.Text : Theme.Text,
                new RectangleF(r.X + U(16), r.Y + U(11), r.Width - U(90), U(20)));

            // 序号
            Theme.TextR(g, (idx + 1).ToString("00"), Theme.FM(8f), Theme.TextFaint,
                new RectangleF(r.Right - U(34), r.Y + U(12), U(20), U(16)));

            // 音色
            using (SolidBrush b = new SolidBrush(Theme.Alpha(col, 200)))
                g.FillEllipse(b, r.X + U(17), r.Y + U(39), U(5), U(5));
            Theme.TextW(g, Synth.All[t.Instrument].Name + "  ·  " + Synth.All[t.Instrument].Tag,
                Theme.F(8.2f), Theme.TextDim, new RectangleF(r.X + U(27), r.Y + U(33), r.Width - U(46), U(16)));

            // M / S
            RectangleF m = new RectangleF(r.X + U(16), r.Y + U(57), U(26), U(21));
            RectangleF s = new RectangleF(r.X + U(46), r.Y + U(57), U(26), U(21));
            DrawToggle(g, m, "M", t.Mute, Theme.Danger);
            DrawToggle(g, s, "S", t.Solo, Theme.Amber);

            // 音量条
            RectangleF vol = new RectangleF(r.X + U(80), r.Y + U(65), r.Width - U(96), U(4));
            DrawTrackVolume(g, vol, t.Volume, col, sel);

            // 迷你预览
            DrawMiniPreview(g, new RectangleF(r.X + U(16), r.Y + U(79), r.Width - U(32), U(13)), t, col);
        }

        void DrawToggle(Graphics g, RectangleF r, string label, bool on, Color col)
        {
            Theme.FillRound(g, on ? Theme.Alpha(col, 210) : Theme.Bg3, r, U(4));
            if (!on) using (Pen p = new Pen(Theme.LineSoft)) Theme.DrawRound(g, p, r, U(4));
            Theme.TextC(g, label, Theme.FB(7.5f), on ? Theme.Bg0 : Theme.TextDim, r);
        }

        void DrawTrackVolume(Graphics g, RectangleF r, double value, Color col, bool sel)
        {
            Theme.FillRound(g, Theme.Bg4, r, r.Height * 0.5f);
            float w = (float)(r.Width * Math.Max(0, Math.Min(1, value)));
            if (w > 1)
                Theme.FillRound(g, Theme.Alpha(col, sel ? 255 : 170), new RectangleF(r.X, r.Y, w, r.Height), r.Height * 0.5f);
            float kx = r.X + w;
            using (SolidBrush b = new SolidBrush(sel ? Color.White : Theme.TextDim))
                g.FillEllipse(b, kx - U(4), r.Y + r.Height * 0.5f - U(4), U(8), U(8));
        }

        void DrawMiniPreview(Graphics g, RectangleF r, Track t, Color col)
        {
            g.FillRectangle(new SolidBrush(Theme.Bg0), r);
            using (Pen p = new Pen(Theme.LineSoft))
            {
                int bars = (int)Math.Max(1, project.Bars);
                for (int b = 0; b <= bars; b++)
                {
                    float x = r.X + r.Width * b / bars;
                    g.DrawLine(p, x, r.Y, x, r.Bottom);
                }
            }
            int maxP = 127, minP = 0;
            double len = project.LengthBeats;
            using (SolidBrush b = new SolidBrush(Theme.Alpha(col, 150)))
            {
                for (int i = 0; i < t.Notes.Count; i++)
                {
                    Note n = t.Notes[i];
                    float x = r.X + (float)(n.Start / len * r.Width);
                    float w = (float)Math.Max((double)U(1.5f), n.Length / len * r.Width);
                    float y = r.Y + r.Height * (1f - (n.Pitch - minP) / (float)(maxP - minP)) - U(1.2f);
                    g.FillRectangle(b, x, y, w, U(2.4f));
                }
            }
        }

        // ================= 时间尺 =================

        void DrawRuler(Graphics g)
        {
            g.FillRectangle(new SolidBrush(Theme.RulerBg), rulerRect);
            using (Pen p = new Pen(Theme.LineSoft))
                g.DrawLine(p, rulerRect.X, rulerRect.Bottom - 1, rulerRect.Right, rulerRect.Bottom - 1);

            double bpb = project.BeatsPerBar;
            int bar = (int)Math.Floor(viewBeat / bpb);
            float labelY = rulerRect.Y + U(5);
            using (Pen penBar = new Pen(Theme.Line, U(1)))
            using (Pen penBeat = new Pen(Theme.Bg4, U(1)))
            {
                for (int i = 0; i < 400; i++)
                {
                    double b = (bar + i) * bpb;
                    float x = BeatToX(b);
                    if (x > gridRect.Right + 2) break;
                    if (x < gridRect.Left - 40) continue;
                    g.DrawLine(penBar, x, rulerRect.Bottom - U(9), x, rulerRect.Bottom);
                    Theme.Txt(g, (bar + i + 1).ToString(), Theme.FM(8.5f), Theme.TextDim, x + U(5), labelY);
                }
            }
        }

        // ================= 网格 =================

        float BeatToX(double beat)
        {
            return gridRect.X + (float)((beat - viewBeat) * pxPerBeat);
        }

        double XToBeat(float x)
        {
            return viewBeat + (x - gridRect.X) / pxPerBeat;
        }

        float PitchToY(int pitch)
        {
            return gridRect.Y + (float)((127 - pitch) * rowH) - viewY;
        }

        int YToPitch(float y)
        {
            double v = (y - gridRect.Y + viewY) / rowH;
            return 127 - (int)Math.Floor(v);
        }

        void DrawGrid(Graphics g)
        {
            g.FillRectangle(new SolidBrush(Theme.GridBg), gridRect);

            int p0 = YToPitch(gridRect.Bottom) - 1;
            int p1 = YToPitch(gridRect.Top) + 1;
            if (p0 < 0) p0 = 0;
            if (p1 > 127) p1 = 127;

            using (SolidBrush blackRow = new SolidBrush(Theme.RowDark))
            using (Brush whiteRow = new SolidBrush(Theme.RowLight))
            using (Pen line = new Pen(Theme.GridLine))
            {
                for (int p = p0; p <= p1; p++)
                {
                    float y = PitchToY(p);
                    if (y > gridRect.Bottom || y + rowH < gridRect.Top) continue;
                    if (Synth.IsBlackKey(p)) g.FillRectangle(blackRow, gridRect.X, y, gridRect.Width, (float)rowH);
                    else g.FillRectangle(whiteRow, gridRect.X, y, gridRect.Width, (float)rowH);
                    g.DrawLine(line, gridRect.X, y + (float)rowH, gridRect.Right, y + (float)rowH);
                }
            }

            // 垂直线
            double beat0 = Math.Floor(viewBeat);
            double bpb = project.BeatsPerBar;
            using (Pen penBeat = new Pen(Theme.GridLine, U(1)))
            using (Pen penBar = new Pen(Theme.Line, U(1)))
            {
                for (int i = 0; i < 800; i++)
                {
                    double b = beat0 + i;
                    float x = BeatToX(b);
                    if (x > gridRect.Right + 1) break;
                    if (x < gridRect.Left - 1) continue;
                    bool isBar = Math.Abs(b % bpb) < 0.001;
                    g.DrawLine(isBar ? penBar : penBeat, x, gridRect.Y, x, gridRect.Bottom);
                }
            }
        }

        void DrawKeyboard(Graphics g)
        {
            g.FillRectangle(new SolidBrush(Theme.PanelBg), keyRect);

            int p0 = YToPitch(gridRect.Bottom);
            int p1 = YToPitch(gridRect.Top);
            if (p0 < 0) p0 = 0;
            if (p1 > 127) p1 = 127;

            bool drums = Synth.All[project.Tracks.Count > 0 ? project.Tracks[selectedTrack].Instrument : 0].Kind == 1;

            using (Brush whiteB = new SolidBrush(Theme.KeyWhite))
            using (Brush whiteB2 = new SolidBrush(Theme.KeyWhite2))
            using (Brush blackB = new SolidBrush(Theme.KeyBlack))
            using (Pen sep = new Pen(Theme.KeySep))
            {
                for (int p = p0; p <= p1; p++)
                {
                    if (Synth.IsBlackKey(p)) continue;
                    float y = PitchToY(p);
                    if (y > keyRect.Bottom || y + rowH < keyRect.Top) continue;
                    RectangleF r = new RectangleF(keyRect.X + U(4), y + U(0.5f), keyRect.Width - U(8), (float)rowH - U(1));
                    g.FillRectangle(p % 12 == 0 ? whiteB2 : whiteB, r);
                    g.DrawLine(sep, r.X, r.Bottom, r.Right, r.Bottom);
                }
                for (int p = p0; p <= p1; p++)
                {
                    if (!Synth.IsBlackKey(p)) continue;
                    float y = PitchToY(p);
                    if (y > keyRect.Bottom || y + rowH < keyRect.Top) continue;
                    RectangleF r = new RectangleF(keyRect.X + U(4), y + U(0.5f), keyRect.Width - U(22), (float)rowH - U(1));
                    g.FillRectangle(blackB, r);
                }
            }

            // 音名
            for (int p = p0; p <= p1; p++)
            {
                if (p % 12 != 0) continue;
                float y = PitchToY(p);
                if (y > keyRect.Bottom || y + rowH < keyRect.Top) continue;
                if (drums && Synth.IsDrumPitch(p))
                    Theme.Txt(g, Synth.DrumLabel(p), Theme.F(6.8f), Theme.TextFaint, keyRect.X + U(8), y + U(2));
                else
                    Theme.Txt(g, Synth.NoteName(p), Theme.F(6.8f), Theme.TextFaint, keyRect.X + U(8), y + U(2));
            }

            if (drums)
            {
                for (int p = p0; p <= p1; p++)
                {
                    if (!Synth.IsDrumPitch(p) || p % 12 == 0) continue;
                    float y = PitchToY(p);
                    if (y > keyRect.Bottom || y + rowH < keyRect.Top) continue;
                    Theme.Txt(g, Synth.DrumLabel(p), Theme.F(6.4f), Theme.TextDim, keyRect.X + U(10), y + U(2));
                }
            }

            using (Pen p = new Pen(Theme.LineSoft))
                g.DrawLine(p, keyRect.Right - 1, keyRect.Top, keyRect.Right - 1, keyRect.Bottom);
        }

        void DrawNotes(Graphics g)
        {
            if (project.Tracks.Count == 0) return;
            if (selectedTrack < 0 || selectedTrack >= project.Tracks.Count) selectedTrack = 0;
            Track t = project.Tracks[selectedTrack];
            Color col = Theme.TrackColors[t.ColorIndex % Theme.TrackColors.Length];

            Region old = g.Clip;
            g.SetClip(gridRect);

            for (int i = 0; i < t.Notes.Count; i++)
            {
                Note n = t.Notes[i];
                float x1 = BeatToX(n.Start);
                float x2 = BeatToX(n.End);
                float y = PitchToY(n.Pitch);
                if (x2 < gridRect.Left - 4 || x1 > gridRect.Right + 4) continue;
                if (y + rowH < gridRect.Top - 2 || y > gridRect.Bottom + 2) continue;

                float w = Math.Max(x2 - x1, U(3));
                float h = Math.Max((float)rowH - U(2.2f), U(3));
                RectangleF r = new RectangleF(x1, y + U(1.1f), w, h);

                bool isSel = n == selectedNote;
                bool isHov = n == hoverNote;

                Color fill = isSel ? Theme.Mix(col, Color.White, 0.28f) : col;
                if (isHov && !isSel) fill = Theme.Mix(col, Color.White, 0.15f);

                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new RectangleF(r.X, r.Y, r.Width, r.Height + 1),
                    Theme.Mix(fill, Color.White, 0.18f), Theme.Mix(fill, Color.Black, 0.30f), LinearGradientMode.Vertical))
                    Theme.FillRound(g, lg, r, U(4));

                using (Pen p = new Pen(Theme.Alpha(Theme.Mix(fill, Color.White, 0.5f), 220), U(1)))
                    g.DrawLine(p, r.X + U(2), r.Y + U(0.8f), r.Right - U(2), r.Y + U(0.8f));

                if (isSel)
                    using (Pen p = new Pen(Color.White, U(1.6f)))
                        Theme.DrawRound(g, p, r, U(4));
            }

            g.Clip = old;
        }

        void DrawPlayhead(Graphics g)
        {
            double beat = engine.IsPlaying ? engine.UiPosition : headBeat;
            float x = BeatToX(beat);
            if (x < gridRect.X - 1 || x > gridRect.Right + 1) return;

            using (Pen p = new Pen(Theme.Playhead, U(1.4f)))
            {
                g.DrawLine(p, x, rulerRect.Y + U(6), x, gridRect.Bottom);
            }
            using (SolidBrush b = new SolidBrush(Theme.Playhead))
            {
                PointF[] pts = new PointF[]
                {
                    new PointF(x - U(5), rulerRect.Y + U(4)),
                    new PointF(x + U(5), rulerRect.Y + U(4)),
                    new PointF(x, rulerRect.Y + U(13)),
                };
                g.FillPolygon(b, pts);
            }
            using (SolidBrush b = new SolidBrush(Theme.Alpha(Theme.Playhead, 40)))
                g.FillRectangle(b, x - U(9), rulerRect.Y + U(4), U(18), U(22));
        }

        // ================= 底栏 =================

        void DrawBottom(Graphics g)
        {
            RectangleF r = bottomRect;
            using (LinearGradientBrush lg = new LinearGradientBrush(new RectangleF(0, r.Y, r.Width, r.Height + 1),
                Theme.BottomA, Theme.BottomB, LinearGradientMode.Vertical))
                g.FillRectangle(lg, r);
            using (Pen p = new Pen(Theme.Line))
                g.DrawLine(p, 0, r.Y, r.Width, r.Y);

            float cy = r.Y + r.Height * 0.5f;
            Track t = project.Tracks.Count > 0 ? project.Tracks[Math.Min(selectedTrack, project.Tracks.Count - 1)] : null;
            if (t == null) return;

            Color col = Theme.TrackColors[t.ColorIndex % Theme.TrackColors.Length];
            using (SolidBrush b = new SolidBrush(col))
                g.FillEllipse(b, U(20), cy - U(4), U(8), U(8));
            Theme.TextW(g, t.Name, Theme.FB(9.5f), Theme.Text, new RectangleF(U(34), r.Y, U(110), r.Height));

            // 音色按钮
            RectangleF ib = btnInstrument.R;
            Theme.FillRound(g, btnInstrument.Hover ? Theme.Bg4 : Theme.Bg2, ib, U(7));
            using (Pen p = new Pen(btnInstrument.Hover ? Theme.Line : Theme.LineSoft)) Theme.DrawRound(g, p, ib, U(7));
            Theme.Txt(g, "音色", Theme.F(6.6f), Theme.TextFaint, ib.X + U(11), ib.Y + U(3));
            Theme.TextW(g, Synth.All[t.Instrument].Name, Theme.FB(9.5f), Theme.Text,
                new RectangleF(ib.X + U(11), ib.Y + U(9), ib.Width - U(40), U(17)));
            using (SolidBrush b = new SolidBrush(Theme.TextDim))
            {
                float ax = ib.Right - U(15), ay = ib.Y + ib.Height * 0.5f;
                g.FillPolygon(b, new PointF[] { new PointF(ax - U(3), ay - U(2)), new PointF(ax + U(3), ay - U(2)), new PointF(ax, ay + U(2.5f)) });
            }

            // 音量
            Theme.Txt(g, "音量", Theme.F(7.5f), Theme.TextFaint, volumeRect.X - U(34), cy - U(8));
            DrawSlider(g, volumeRect, t.Volume, col, drag == DragMode.TrackVolume && hoverTrack == selectedTrack);
            Theme.TextL(g, ((int)(t.Volume * 100)).ToString(), Theme.FM(7.5f), Theme.TextDim,
                new RectangleF(volumeRect.Right + U(8), cy - U(8), U(30), U(16)));

            // 声像
            Theme.Txt(g, "声像", Theme.F(7.5f), Theme.TextFaint, panRect.X - U(34), cy - U(8));
            DrawPanSlider(g, panRect, t.Pan);
            string ptxt = Math.Abs(t.Pan) < 0.02 ? "C" : (t.Pan < 0 ? "L" + (int)(-t.Pan * 100) : "R" + (int)(t.Pan * 100));
            Theme.TextL(g, ptxt, Theme.FM(7.5f), Theme.TextDim, new RectangleF(panRect.Right + U(8), cy - U(8), U(34), U(16)));

            // 力度（作用于选中音符）
            Theme.Txt(g, "力度", Theme.F(7.5f), Theme.TextFaint, velRect.X - U(34), cy - U(8));
            if (selectedNote != null)
            {
                DrawSlider(g, velRect, selectedNote.Vel / 127.0, Theme.Amber, drag == DragMode.NoteVel);
                Theme.TextL(g, selectedNote.Vel.ToString(), Theme.FM(7.5f), Theme.TextDim,
                    new RectangleF(velRect.Right + U(8), cy - U(8), U(34), U(16)));
            }
            else
            {
                Theme.FillRound(g, Theme.Bg4, velRect, velRect.Height * 0.5f);
                Theme.TextL(g, "-", Theme.FM(7.5f), Theme.TextFaint,
                    new RectangleF(velRect.Right + U(8), cy - U(8), U(34), U(16)));
            }

            // 右侧提示
            if (statusText.Length > 0)
                Theme.TextR(g, statusText, Theme.F(8f), Theme.TextFaint,
                    new RectangleF(velRect.Right + U(44), r.Y, r.Width - velRect.Right - U(64), r.Height));
        }

        void DrawSlider(Graphics g, RectangleF r, double value, Color col, bool active)
        {
            Theme.FillRound(g, Theme.Bg4, r, r.Height * 0.5f);
            float w = (float)(r.Width * Math.Max(0, Math.Min(1, value)));
            if (w > U(2))
                Theme.FillRound(g, Theme.Alpha(col, 230), new RectangleF(r.X, r.Y, w, r.Height), r.Height * 0.5f);
            float kx = r.X + w;
            using (SolidBrush b = new SolidBrush(active ? Color.White : Theme.Text))
                g.FillEllipse(b, kx - U(5), r.Y + r.Height * 0.5f - U(5), U(10), U(10));
        }

        void DrawPanSlider(Graphics g, RectangleF r, double value)
        {
            Theme.FillRound(g, Theme.Bg4, r, r.Height * 0.5f);
            float cx = r.X + r.Width * 0.5f;
            float kx = r.X + (float)((value + 1) * 0.5 * r.Width);
            using (SolidBrush b = new SolidBrush(Theme.Line))
                g.FillRectangle(b, cx - U(0.5f), r.Y - U(3), U(1), r.Height + U(6));
            using (Pen p = new Pen(Theme.Accent, U(2)))
                g.DrawLine(p, cx, r.Y + r.Height * 0.5f, kx, r.Y + r.Height * 0.5f);
            using (SolidBrush b = new SolidBrush(Theme.Text))
                g.FillEllipse(b, kx - U(5), r.Y + r.Height * 0.5f - U(5), U(10), U(10));
        }

        void DrawHelp(Graphics g)
        {
            using (SolidBrush b = new SolidBrush(Theme.Alpha(Theme.Bg0, 180)))
                g.FillRectangle(b, 0, 0, ClientSize.Width, ClientSize.Height);

            float w = U(560), h = U(430);
            RectangleF card = new RectangleF((ClientSize.Width - w) * 0.5f, (ClientSize.Height - h) * 0.5f, w, h);
            Theme.FillRound(g, Theme.Bg2, card, U(14));
            using (Pen p = new Pen(Theme.Line)) Theme.DrawRound(g, p, card, U(14));

            Theme.Txt(g, "操作指南", Theme.FB(16f), Theme.Text, card.X + U(28), card.Y + U(22));
            using (Font f = new Font("Segoe UI", 8f))
                Theme.Txt(g, "TUNEFORGE  ·  QUICK GUIDE", f, Theme.TextFaint, card.X + U(29), card.Y + U(48));

            string[] lines = new string[]
            {
                "空格键            播放 / 暂停",
                "左键拖空白      新建音符（拖动可调整长度）",
                "拖动音符          移动音高与位置",
                "拖动音符右边缘  调整时值",
                "右键 / 双击       删除音符",
                "滚轮              垂直滚动音域",
                "Shift + 滚轮      水平滚动时间",
                "Ctrl + 滚轮       缩放时间轴",
                "点击时间尺        定位播放头",
                "Ctrl + S / O / E   保存 / 打开 / 导出 WAV",
                "Ctrl + Z          撤销",
                "Delete            删除选中音符",
            };

            float y = card.Y + U(78);
            for (int i = 0; i < lines.Length; i++)
            {
                Theme.Txt(g, lines[i], Theme.F(10f), i % 2 == 0 ? Theme.Text : Theme.TextDim, card.X + U(30), y);
                y += U(26);
            }

            Theme.TextC(g, "点击任意位置关闭", Theme.F(8.5f), Theme.TextFaint,
                new RectangleF(card.X, card.Bottom - U(26), card.Width, U(20)));
        }

        // ================= 图标 =================

        void DrawIcon(Graphics g, string kind, RectangleF r, Color c)
        {
            using (SolidBrush b = new SolidBrush(c))
            using (Pen p = new Pen(c, Math.Max(1.6f, U(1.7f))))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                float x = r.X, y = r.Y, w = r.Width, h = r.Height;
                switch (kind)
                {
                    case "play":
                        g.FillPolygon(b, new PointF[]
                        {
                            new PointF(x + w * 0.30f, y + h * 0.16f),
                            new PointF(x + w * 0.82f, y + h * 0.5f),
                            new PointF(x + w * 0.30f, y + h * 0.84f)
                        });
                        break;
                    case "pause":
                        g.FillRectangle(b, x + w * 0.26f, y + h * 0.16f, w * 0.16f, h * 0.68f);
                        g.FillRectangle(b, x + w * 0.58f, y + h * 0.16f, w * 0.16f, h * 0.68f);
                        break;
                    case "stop":
                        g.FillRectangle(b, x + w * 0.28f, y + h * 0.28f, w * 0.44f, h * 0.44f);
                        break;
                    case "loop":
                        {
                            GraphicsPath path = new GraphicsPath();
                            path.AddArc(x + w * 0.06f, y + h * 0.14f, w * 0.80f, h * 0.72f, 200, 220);
                            g.DrawPath(p, path);
                            path.Dispose();
                            PointF tip = new PointF(x + w * 0.90f, y + h * 0.40f);
                            g.FillPolygon(b, new PointF[]
                            {
                                new PointF(tip.X + w*0.06f, tip.Y - h*0.08f),
                                new PointF(tip.X - w*0.16f, tip.Y - h*0.02f),
                                new PointF(tip.X - w*0.02f, tip.Y + h*0.18f)
                            });
                        }
                        break;
                    case "save":
                        {
                            RectangleF rr = new RectangleF(x + w * 0.12f, y + h * 0.12f, w * 0.76f, h * 0.76f);
                            g.DrawRectangle(p, rr.X, rr.Y, rr.Width, rr.Height);
                            g.FillRectangle(b, x + w * 0.30f, y + h * 0.12f, w * 0.40f, h * 0.26f);
                            g.DrawRectangle(p, x + w * 0.28f, y + h * 0.54f, w * 0.44f, h * 0.34f);
                        }
                        break;
                    case "open":
                        {
                            PointF[] pts = new PointF[]
                            {
                                new PointF(x + w*0.08f, y + h*0.78f),
                                new PointF(x + w*0.08f, y + h*0.26f),
                                new PointF(x + w*0.40f, y + h*0.26f),
                                new PointF(x + w*0.48f, y + h*0.38f),
                                new PointF(x + w*0.92f, y + h*0.38f),
                                new PointF(x + w*0.92f, y + h*0.78f),
                            };
                            g.DrawPolygon(p, pts);
                        }
                        break;
                    case "export":
                        {
                            g.DrawLine(p, x + w * 0.5f, y + h * 0.14f, x + w * 0.5f, y + h * 0.58f);
                            g.FillPolygon(b, new PointF[]
                            {
                                new PointF(x + w*0.5f, y + h*0.74f),
                                new PointF(x + w*0.30f, y + h*0.48f),
                                new PointF(x + w*0.70f, y + h*0.48f)
                            });
                            g.DrawLine(p, x + w * 0.20f, y + h * 0.86f, x + w * 0.80f, y + h * 0.86f);
                        }
                        break;
                    case "help":
                        Theme.TextC(g, "?", Theme.FB(13f), c, r);
                        break;
                    case "new":
                        {
                            PointF[] pts = new PointF[]
                            {
                                new PointF(x + w*0.14f, y + h*0.10f),
                                new PointF(x + w*0.60f, y + h*0.10f),
                                new PointF(x + w*0.80f, y + h*0.30f),
                                new PointF(x + w*0.80f, y + h*0.90f),
                                new PointF(x + w*0.14f, y + h*0.90f),
                            };
                            g.DrawPolygon(p, pts);
                            g.DrawLine(p, x + w * 0.60f, y + h * 0.10f, x + w * 0.60f, y + h * 0.30f);
                            g.DrawLine(p, x + w * 0.60f, y + h * 0.30f, x + w * 0.80f, y + h * 0.30f);
                            g.DrawLine(p, x + w * 0.31f, y + h * 0.47f, x + w * 0.31f, y + h * 0.69f);
                            g.DrawLine(p, x + w * 0.20f, y + h * 0.58f, x + w * 0.42f, y + h * 0.58f);
                        }
                        break;
                    case "more":
                        for (int i = 0; i < 3; i++)
                            g.FillEllipse(b, x + w * (0.14f + i * 0.26f), y + h * 0.42f, w * 0.13f, w * 0.13f);
                        break;
                    case "metro":
                        {
                            g.DrawPolygon(p, new PointF[]
                            {
                                new PointF(x + w*0.5f, y + h*0.10f),
                                new PointF(x + w*0.86f, y + h*0.84f),
                                new PointF(x + w*0.14f, y + h*0.84f),
                            });
                            g.DrawLine(p, x + w * 0.5f, y + h * 0.34f, x + w * 0.34f, y + h * 0.72f);
                        }
                        break;
                    case "up":
                        g.FillPolygon(b, new PointF[]
                        {
                            new PointF(x + w*0.5f, y + h*0.22f),
                            new PointF(x + w*0.08f, y + h*0.78f),
                            new PointF(x + w*0.92f, y + h*0.78f),
                        });
                        break;
                    case "down":
                        g.FillPolygon(b, new PointF[]
                        {
                            new PointF(x + w*0.5f, y + h*0.78f),
                            new PointF(x + w*0.08f, y + h*0.22f),
                            new PointF(x + w*0.92f, y + h*0.22f),
                        });
                        break;
                }
            }
        }
    }
}
