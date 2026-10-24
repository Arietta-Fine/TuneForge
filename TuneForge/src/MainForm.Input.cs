using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TuneForge
{
    static class PositionForm
    {
        public static void centerOnScreen(Form f)
        {
            Rectangle wa = Screen.FromPoint(new Point(f.Left + f.Width / 2, f.Top + f.Height / 2)).WorkingArea;
            f.Left = wa.X + (wa.Width - f.Width) / 2;
            f.Top = wa.Y + (wa.Height - f.Height) / 2;
            if (f.Top < wa.Y) f.Top = wa.Y;
            if (f.Left < wa.X) f.Left = wa.X;
        }
    }

    public partial class MainForm
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        Stack<Project> undoStack = new Stack<Project>();
        const int UNDO_MAX = 40;
        bool suppressDirty = false;

        void TryDarkTitleBar()
        {
            try
            {
                int v = 1;
                int rc = DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int));
                if (rc != 0) DwmSetWindowAttribute(Handle, 19, ref v, sizeof(int));
            }
            catch { }
        }

        void ApplyIcon()
        {
            try
            {
                // 优先用 exe 内嵌图标（build 时 /win32icon:app.ico）
                Icon embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (embedded != null)
                {
                    Icon = embedded;
                    return;
                }
            }
            catch { }
            try
            {
                Bitmap bmp = new Bitmap(64, 64);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    RectangleF r = new RectangleF(2, 2, 60, 60);
                    using (LinearGradientBrush lg = new LinearGradientBrush(r, Color.FromArgb(70, 220, 198), Color.FromArgb(78, 122, 240), 45f))
                        Theme.FillRound(g, lg, r, 14);
                    using (Font f = new Font("Segoe UI Symbol", 30f, FontStyle.Bold))
                        Theme.TextC(g, "\u266A", f, Color.White, r);
                }
                Icon = Icon.FromHandle(bmp.GetHicon());
            }
            catch { }
        }

        // ---------------- 主菜单 ----------------

        void ShowMainMenu()
        {
            ContextMenuStrip m = BuildMainMenu();
            Point pt = new Point((int)btnMenu.R.X, (int)btnMenu.R.Bottom + 2);
            m.Show(this, pt);
        }

        ContextMenuStrip BuildMainMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.ShowImageMargin = false;
            m.ShowCheckMargin = false;
            m.BackColor = Theme.Bg2;
            m.ForeColor = Theme.Text;
            m.Font = Theme.F(9.5f);
            m.Padding = new Padding(2, 4, 2, 4);

            AddMenuItem(m, "新建工程", "Ctrl+N", NewProject);
            AddMenuItem(m, "打开工程…", "Ctrl+O", OpenProject);
            AddMenuItem(m, "保存", "Ctrl+S", delegate { SaveProject(false); });
            AddMenuItem(m, "另存为…", "Ctrl+Shift+S", delegate { SaveProject(true); });
            m.Items.Add(new ToolStripSeparator());
            AddMenuItem(m, "导出 WAV…", "Ctrl+E", ExportWav);
            m.Items.Add(new ToolStripSeparator());

            // 主题子菜单
            ToolStripMenuItem themeRoot = new ToolStripMenuItem("主题");
            ContextMenuStrip themeMenu = new ContextMenuStrip();
            themeMenu.Renderer = new DarkMenuRenderer();
            themeMenu.ShowImageMargin = false;
            themeMenu.ShowCheckMargin = false;
            themeMenu.BackColor = Theme.Bg2;
            themeMenu.ForeColor = Theme.Text;
            themeMenu.Font = Theme.F(9.5f);
            themeMenu.Padding = new Padding(2, 4, 2, 4);
            for (int ti = 0; ti < Theme.Names.Length; ti++)
            {
                int id = ti;
                ToolStripMenuItem item = new ToolStripMenuItem(Theme.Names[ti]);
                if (ti == Theme.Current) item.Checked = true;
                item.Click += delegate { SetTheme(id); };
                themeMenu.Items.Add(item);
            }
            themeRoot.DropDown = themeMenu;
            m.Items.Add(themeRoot);

            AddMenuItem(m, "音源管理…", "", OpenSampler);
            AddMenuItem(m, "导入采样音源…", "", delegate { ImportSample(true); });
            AddMenuItem(m, "临时导入（不复制）…", "", delegate { ImportSample(false); });
            m.Items.Add(new ToolStripSeparator());
            AddMenuItem(m, "操作指南", "F1", ToggleHelp);
            AddMenuItem(m, "关于编曲工坊", "", ShowAbout);
            return m;
        }

        /// <summary>自动化探针：执行「新建工程」（跳过未保存确认）。</summary>
        public void ProbeNew()
        {
            project.Dirty = false;
            NewProject();
        }

        /// <summary>自动化探针：确认主菜单项已构建。</summary>
        public string ProbeMenu()
        {
            ContextMenuStrip m = BuildMainMenu();
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (ToolStripItem it in m.Items)
                sb.Append(it is ToolStripSeparator ? "--" : it.Text).Append('|');
            m.Dispose();
            return sb.ToString();
        }

        void AddMenuItem(ContextMenuStrip m, string text, string shortcut, Action act)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            if (shortcut != null && shortcut.Length > 0) it.ShortcutKeyDisplayString = shortcut;
            if (act != null) it.Click += delegate { act(); };
            m.Items.Add(it);
        }

        void ShowAbout()
        {
            MessageBox.Show(this,
                "编曲工坊 TuneForge  1.0\n\n" +
                "轻量级音乐编曲软件\n" +
                "钢琴卷帘 · 合成器 · 鼓组 · WAV 导出\n\n" +
                "示例工程与全部音色均为实时合成",
                "关于编曲工坊", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------- 音色菜单 ----------------

        void BuildInstrumentMenu()
        {
            instMenu = new ContextMenuStrip();
            instMenu.Renderer = new DarkMenuRenderer();
            instMenu.ShowImageMargin = false;
            instMenu.ShowCheckMargin = false;
            instMenu.BackColor = Theme.Bg2;
            instMenu.ForeColor = Theme.Text;
            instMenu.Font = Theme.F(9.5f);
            instMenu.Padding = new Padding(2, 4, 2, 4);
            instMenu.ItemClicked += OnInstrumentMenuClicked;
        }

        void ShowInstrumentMenu()
        {
            if (project.Tracks.Count == 0) return;
            if (instMenu.Visible) { instMenu.Close(); return; }
            instMenu.Items.Clear();
            int cur = project.Tracks[selectedTrack].Instrument;
            for (int i = 0; i < Synth.All.Length; i++)
            {
                Preset pr = Synth.All[i];
                ToolStripMenuItem it = new ToolStripMenuItem();
                it.Text = pr.Name;
                it.Tag = i;
                it.Checked = (i == cur);
                it.ForeColor = (i == cur) ? Theme.Accent : Theme.Text;
                it.Padding = new Padding(10, 4, 18, 4);
                instMenu.Items.Add(it);
            }
            Point sp = PointToScreen(new Point((int)btnInstrument.R.Left, (int)btnInstrument.R.Top));
            instMenu.Show(sp.X, sp.Y - instMenu.PreferredSize.Height - 4);
        }

        void OnInstrumentMenuClicked(object sender, ToolStripItemClickedEventArgs e)
        {
            int idx = (int)e.ClickedItem.Tag;
            PushUndo();
            project.Tracks[selectedTrack].Instrument = idx;
            project.Dirty = true;
            engine.Sync();
            cacheDirty = true;
            Invalidate();
        }

        // ---------------- 播放控制 ----------------

        void TogglePlay()
        {
            if (engine.IsPlaying)
            {
                headBeat = engine.UiPosition;
                engine.Pause();
                statusText = "已暂停";
            }
            else
            {
                if (headBeat >= project.LengthBeats - 0.01) headBeat = 0;
                engine.SetLoop(btnLoop.Active, project.LengthBeats);
                engine.SetLength(project.LengthBeats);
                engine.SetTempo(project.Bpm);
                engine.Sync();
                try
                {
                    engine.Play(headBeat);
                    statusText = "播放中";
                }
                catch (Exception)
                {
                    statusText = "音频设备不可用，请检查扬声器设置";
                }
            }
            cacheDirty = true;
            Invalidate();
        }

        void StopPlayback()
        {
            engine.Stop();
            headBeat = 0;
            statusText = "";
            cacheDirty = true;
            Invalidate();
        }

        void SeekTo(double beat)
        {
            if (beat < 0) beat = 0;
            if (beat > project.LengthBeats) beat = project.LengthBeats;
            headBeat = beat;
            engine.Seek(beat);
            cacheDirty = true;
            Invalidate();
        }

        void OnUiTick(object sender, EventArgs e)
        {
            bool playing = engine.IsPlaying;
            if (playing)
            {
                if (engine.UiPosition > project.LengthBeats)
                {
                    // 等待引擎自身停止
                }
                Invalidate();
            }
            else if (wasPlaying)
            {
                if (headBeat < 0.01 && engine.RawPosition > 0.01) headBeat = engine.RawPosition;
                cacheDirty = true;
                Invalidate();
            }
            wasPlaying = playing;
        }

        // ---------------- 鼠标 ----------------

        PointF ToPt(MouseEventArgs e) { return new PointF(e.X, e.Y); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            PointF p = ToPt(e);

            if (samplerOpen)
            {
                if (e.Button == MouseButtons.Left) HandleSamplerDown(p);
                return;
            }

            if (showHelp)
            {
                showHelp = false;
                cacheDirty = true;
                Invalidate();
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                // 顶栏按钮
                foreach (UiBtn b in btns)
                {
                    if (b.Visible && b.R.Contains(p) && b.Click != null)
                    {
                        b.Click();
                        Invalidate();
                        return;
                    }
                }
                if (timeSigRect.Contains(p)) { ShowTimeSigMenu(); return; }
                if (barsRect.Contains(p)) { ShowBarsMenu(); return; }
                if (bpmRect.Contains(p) && !btnBpmUp.R.Contains(p) && !btnBpmDown.R.Contains(p))
                {
                    drag = DragMode.BpmDrag;
                    dragStartPx = e.Location;
                    bpmDragValue = project.Bpm;
                    return;
                }
            }

            if (bottomRect.Contains(p)) { HandleBottomDown(e, p); return; }
            if (trackRect.Contains(p)) { HandleTrackDown(e, p); return; }
            if (rulerRect.Contains(p))
            {
                drag = DragMode.SeekHead;
                SeekTo(Snap(XToBeat(p.X)));
                return;
            }
            if (keyRect.Contains(p))
            {
                int pitch = YToPitch(p.Y);
                PreviewPitch(pitch);
                return;
            }
            if (gridRect.Contains(p)) { HandleGridDown(e, p); }
        }

        void HandleBottomDown(MouseEventArgs e, PointF p)
        {
            if (e.Button != MouseButtons.Left) return;
            if (btnInstrument.R.Contains(p)) { ShowInstrumentMenu(); return; }
            if (NearRect(volumeRect, p, 10) && project.Tracks.Count > 0)
            {
                PushUndo(); drag = DragMode.BottomVolume; dragSliderRect = volumeRect; SetBottomVolume(p.X);
            }
            else if (NearRect(panRect, p, 10) && project.Tracks.Count > 0)
            {
                PushUndo(); drag = DragMode.BottomPan; dragSliderRect = panRect; SetBottomPan(p.X);
            }
            else if (selectedNote != null && NearRect(velRect, p, 12))
            {
                PushUndo(); drag = DragMode.NoteVel; SetNoteVelByX(p.X);
            }
        }

        void SetBottomVolume(float x)
        {
            if (project.Tracks.Count == 0) return;
            Track t = project.Tracks[Math.Min(selectedTrack, project.Tracks.Count - 1)];
            double v = (x - volumeRect.X) / volumeRect.Width;
            if (v < 0) v = 0;
            if (v > 1) v = 1;
            t.Volume = v;
            project.Dirty = true;
            engine.Sync();
            cacheDirty = true;
        }

        void SetBottomPan(float x)
        {
            if (project.Tracks.Count == 0) return;
            Track t = project.Tracks[Math.Min(selectedTrack, project.Tracks.Count - 1)];
            double v = (x - panRect.X) / panRect.Width * 2.0 - 1.0;
            if (v < -1) v = -1;
            if (v > 1) v = 1;
            t.Pan = v;
            project.Dirty = true;
            engine.Sync();
            cacheDirty = true;
        }

        void SetNoteVelByX(float x)
        {
            if (selectedNote == null) return;
            double v = (x - velRect.X) / velRect.Width;
            if (v < 0) v = 0;
            if (v > 1) v = 1;
            int vel = (int)Math.Round(v * 126.0) + 1;
            if (vel < 1) vel = 1;
            if (vel > 127) vel = 127;
            selectedNote.Vel = vel;
            project.Dirty = true;
            engine.Sync();
            cacheDirty = true;
        }

        bool NearRect(RectangleF r, PointF p, float pad)
        {
            return p.X >= r.X - pad && p.X <= r.Right + pad && p.Y >= r.Y - pad && p.Y <= r.Bottom + pad;
        }

        void HandleTrackDown(MouseEventArgs e, PointF p)
        {
            if (project.Tracks.Count == 0) return;

            if (btnAddTrack != null && btnAddTrack.R.Contains(p))
            {
                AddTrack();
                return;
            }

            if (e.Button == MouseButtons.Right)
            {
                int ridx = TrackIndexAt(p);
                if (ridx >= 0) { selectedTrack = ridx; ShowTrackMenu(p); }
                return;
            }

            float top = trackRect.Y + U(42);
            int idx = (int)((p.Y - top) / U(ROW_H));
            if (idx < 0 || idx >= project.Tracks.Count) return;

            Track t = project.Tracks[idx];
            float rowTop = top + idx * U(ROW_H);

            RectangleF m = new RectangleF(trackRect.X + U(16), rowTop + U(57), U(26), U(21));
            RectangleF s = new RectangleF(trackRect.X + U(46), rowTop + U(57), U(26), U(21));
            if (m.Contains(p)) { PushUndo(); t.Mute = !t.Mute; project.Dirty = true; engine.Sync(); cacheDirty = true; Invalidate(); return; }
            if (s.Contains(p)) { PushUndo(); t.Solo = !t.Solo; project.Dirty = true; engine.Sync(); cacheDirty = true; Invalidate(); return; }

            RectangleF vol = new RectangleF(trackRect.X + U(80), rowTop + U(63), trackRect.Width - U(96), U(8));
            if (vol.Contains(p))
            {
                selectedTrack = idx;
                PushUndo();
                drag = DragMode.TrackVolume;
                dragSliderRect = vol;
                SetTrackVolume(t, p.X, vol);
                engine.Sync();
                cacheDirty = true;
                Invalidate();
                return;
            }

            selectedTrack = idx;
            selectedNote = null;
            cacheDirty = true;
            Invalidate();
        }

        void SetTrackVolume(Track t, float x, RectangleF r)
        {
            double v = (x - r.X) / r.Width;
            if (v < 0) v = 0;
            if (v > 1) v = 1;
            t.Volume = v;
        }

        // ---------------- 轨道右键菜单 ----------------

        int TrackIndexAt(PointF p)
        {
            float top = trackRect.Y + U(42);
            int idx = (int)((p.Y - top) / U(ROW_H));
            if (idx < 0 || idx >= project.Tracks.Count) return -1;
            return idx;
        }

        void ShowTrackMenu(PointF p)
        {
            int idx = TrackIndexAt(p);
            if (idx < 0) return;
            Track t = project.Tracks[idx];
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.Font = Theme.F(9f);
            m.BackColor = Theme.Bg2;
            m.ForeColor = Theme.Text;
            AddMenuItem(m, "重命名…", "", delegate { RenameTrack(idx); });
            AddMenuItem(m, "复制轨道", "", delegate { DuplicateTrack(idx); });
            AddMenuItem(m, "删除轨道", "", delegate { DeleteTrack(idx); });
            m.Items.Add(new ToolStripSeparator());

            ContextMenuStrip colors = new ContextMenuStrip();
            colors.Renderer = new DarkMenuRenderer();
            colors.Font = Theme.F(9f);
            colors.BackColor = Theme.Bg2;
            colors.ForeColor = Theme.Text;
            for (int c = 0; c < Theme.TrackColors.Length; c++)
            {
                int ci = c;
                ToolStripMenuItem it = new ToolStripMenuItem("● " + Theme.ColorNames[c] + "色");
                it.ForeColor = Theme.TrackColors[c];
                it.Checked = (t.ColorIndex == c);
                it.Click += delegate { PushUndo(); t.ColorIndex = ci; project.Dirty = true; cacheDirty = true; Invalidate(); engine.Sync(); };
                colors.Items.Add(it);
            }
            ToolStripMenuItem croot = new ToolStripMenuItem("轨道颜色");
            croot.DropDown = colors;
            m.Items.Add(croot);

            AddMenuItem(m, "上移", "", delegate { MoveTrack(idx, -1); });
            AddMenuItem(m, "下移", "", delegate { MoveTrack(idx, 1); });
            m.Show(PointToScreen(new Point((int)p.X, (int)p.Y)));
        }

        void RenameTrack(int idx)
        {
            if (idx < 0 || idx >= project.Tracks.Count) return;
            Track t = project.Tracks[idx];
            using (PromptForm f = new PromptForm("重命名轨道", t.Name))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    string v = f.Value.Trim();
                    if (v.Length > 0)
                    {
                        PushUndo(); t.Name = v; project.Dirty = true; cacheDirty = true; Invalidate();
                    }
                }
            }
        }

        void DuplicateTrack(int idx)
        {
            if (idx < 0 || idx >= project.Tracks.Count) return;
            PushUndo();
            Track src = project.Tracks[idx];
            Track n = new Track
            {
                Name = src.Name + " 副本", Instrument = src.Instrument,
                Volume = src.Volume, Pan = src.Pan, Mute = src.Mute, Solo = src.Solo,
                ColorIndex = src.ColorIndex
            };
            for (int i = 0; i < src.Notes.Count; i++)
            {
                Note o = src.Notes[i];
                n.Notes.Add(new Note(o.Start, o.Length, o.Pitch, o.Vel));
            }
            project.Tracks.Insert(idx + 1, n);
            selectedTrack = idx + 1;
            project.Dirty = true;
            engine.Sync(); cacheDirty = true; Invalidate();
        }

        void DeleteTrack(int idx)
        {
            if (project.Tracks.Count <= 1) { statusText = "至少要保留一条轨道"; return; }
            if (idx < 0 || idx >= project.Tracks.Count) return;
            PushUndo();
            project.Tracks.RemoveAt(idx);
            if (selectedTrack >= project.Tracks.Count) selectedTrack = project.Tracks.Count - 1;
            if (selectedNote != null && !project.Tracks[selectedTrack].Notes.Contains(selectedNote)) selectedNote = null;
            project.Dirty = true;
            engine.Sync(); cacheDirty = true; Invalidate();
        }

        void MoveTrack(int idx, int dir)
        {
            int j = idx + dir;
            if (j < 0 || j >= project.Tracks.Count) return;
            PushUndo();
            Track a = project.Tracks[idx];
            project.Tracks[idx] = project.Tracks[j];
            project.Tracks[j] = a;
            selectedTrack = j;
            project.Dirty = true;
            engine.Sync(); cacheDirty = true; Invalidate();
        }

        // ---------------- 音符右键菜单 ----------------

        void ShowNoteMenu(Note n)
        {
            if (n == null) return;
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.Font = Theme.F(9f);
            m.BackColor = Theme.Bg2;
            m.ForeColor = Theme.Text;
            AddMenuItem(m, "力度：" + n.Vel + "（↑ +1）", "", delegate { NudgeNoteVel(1); });
            AddMenuItem(m, "力度：" + n.Vel + "（↓ -1）", "", delegate { NudgeNoteVel(-1); });
            AddMenuItem(m, "力度 +10", "", delegate { SetNoteVel(n, n.Vel + 10); });
            AddMenuItem(m, "力度 -10", "", delegate { SetNoteVel(n, n.Vel - 10); });
            AddMenuItem(m, "输入力度…", "", delegate { AskNoteVel(n); });
            m.Items.Add(new ToolStripSeparator());
            AddMenuItem(m, "长度：" + n.Length.ToString("0.##") + " 拍", "", delegate { AskNoteLen(n); });
            m.Items.Add(new ToolStripSeparator());

            // 单个音的音色覆盖
            ToolStripMenuItem instRoot = new ToolStripMenuItem("音色：" + (n.Inst >= 0 ? Synth.All[n.Inst].Name : "跟随轨道"));
            ContextMenuStrip instMenu = new ContextMenuStrip();
            instMenu.Renderer = new DarkMenuRenderer();
            instMenu.BackColor = Theme.Bg2;
            instMenu.ForeColor = Theme.Text;
            instMenu.Font = Theme.F(9f);
            ToolStripMenuItem follow = new ToolStripMenuItem("跟随轨道");
            if (n.Inst < 0) follow.Checked = true;
            follow.Click += delegate { SetNoteInst(n, -1); };
            instMenu.Items.Add(follow);
            instMenu.Items.Add(new ToolStripSeparator());
            for (int k = 0; k < Synth.All.Length; k++)
            {
                int id = k;
                ToolStripMenuItem it = new ToolStripMenuItem((k == Synth.DrumPreset ? "★ " : "") + Synth.All[k].Name);
                if (n.Inst == k) it.Checked = true;
                it.Click += delegate { SetNoteInst(n, id); };
                instMenu.Items.Add(it);
            }
            instRoot.DropDown = instMenu;
            m.Items.Add(instRoot);

            m.Items.Add(new ToolStripSeparator());
            AddMenuItem(m, "删除音符", "Del", delegate { DeleteSelectedNote(); });
            Point mp = MousePosition;
            noteMenuOpen = m;
            m.Show(new Point(mp.X + 3, mp.Y + 2));
        }

        void SetNoteInst(Note n, int inst)
        {
            if (n == null) return;
            if (inst >= Synth.All.Length) inst = -1;
            PushUndo();
            n.Inst = inst;
            project.Dirty = true;
            engine.Sync(); cacheDirty = true; Invalidate();
            selectedNote = n;
        }

        void DeleteSelectedNote()
        {
            if (selectedNote == null) return;
            Track t = project.Tracks[selectedTrack];
            PushUndo();
            t.Notes.Remove(selectedNote);
            selectedNote = null;
            project.Dirty = true;
            engine.Sync(); cacheDirty = true; Invalidate();
        }

        void SetNoteVel(Note n, int v)
        {
            if (n == null) return;
            if (v < 1) v = 1;
            if (v > 127) v = 127;
            PushUndo();
            n.Vel = v;
            project.Dirty = true;
            engine.Sync(); cacheDirty = true; Invalidate();
        }

        void NudgeNoteVel(int d)
        {
            if (selectedNote == null) { statusText = "先选中一个音符"; return; }
            SetNoteVel(selectedNote, selectedNote.Vel + d);
            statusText = "力度 " + selectedNote.Vel;
        }

        void AskNoteVel(Note n)
        {
            if (n == null) return;
            using (PromptForm f = new PromptForm("输入力度（1-127）", n.Vel.ToString()))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    int v;
                    if (int.TryParse(f.Value.Trim(), out v)) SetNoteVel(n, v);
                }
            }
        }

        void AskNoteLen(Note n)
        {
            if (n == null) return;
            using (PromptForm f = new PromptForm("输入长度（拍）", n.Length.ToString("0.###")))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    double v;
                    if (double.TryParse(f.Value.Trim(), out v) && v >= 0.0625)
                    {
                        PushUndo();
                        n.Length = Math.Round(v * 1000.0) / 1000.0;
                        project.Dirty = true;
                        engine.Sync(); cacheDirty = true; Invalidate();
                    }
                }
            }
        }
        void AddTrack()
        {
            PushUndo();
            Track t = new Track();
            t.Name = "轨道 " + (project.Tracks.Count + 1);
            t.ColorIndex = project.Tracks.Count % Theme.TrackColors.Length;
            t.Instrument = 0;
            project.Tracks.Add(t);
            project.Dirty = true;
            selectedTrack = project.Tracks.Count - 1;
            engine.Sync();
            cacheDirty = true;
            Invalidate();
        }

        void HandleGridDown(MouseEventArgs e, PointF p)
        {
            if (project.Tracks.Count == 0) return;
            Track t = project.Tracks[selectedTrack];

            if (e.Button == MouseButtons.Right)
            {
                Note hit = FindNote(t, p);
                // 右键双击 = 删除该音符
                if (hit != null && hit == rcNote && (DateTime.Now - rcTime).TotalMilliseconds < SystemInformation.DoubleClickTime)
                {
                    if (noteMenuOpen != null && noteMenuOpen.Visible) noteMenuOpen.Close();
                    PushUndo();
                    t.Notes.Remove(hit);
                    if (selectedNote == hit) selectedNote = null;
                    hoverNote = null;
                    rcNote = null;
                    pendingMenuNote = null;
                    if (menuTimer != null) menuTimer.Stop();
                    project.Dirty = true;
                    engine.Sync(); cacheDirty = true; Invalidate();
                    return;
                }
                rcNote = hit; rcTime = DateTime.Now;
                if (hit != null)
                {
                    selectedNote = hit;
                    cacheDirty = true;
                    Invalidate();
                    // 立即弹菜单（右下偏移，避免挡住音符），菜单关闭后再右键同一音符即删除
                    ShowNoteMenu(hit);
                }
                else
                {
                    pendingMenuNote = null;
                    if (menuTimer != null) menuTimer.Stop();
                    drag = DragMode.PanView;
                    dragStartPx = e.Location;
                    dragValue = viewBeat;
                }
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            Note n = FindNote(t, p);
            if (n != null)
            {
                selectedNote = n;
                PushUndo();
                dragNote = n;
                dragNoteOrigStart = n.Start;
                dragNoteOrigLen = n.Length;
                dragNoteOrigPitch = n.Pitch;
                dragStartPx = e.Location;
                dragStartX = p.X;
                dragStartY = p.Y;
                float nx1 = BeatToX(n.Start);
                float nx2 = BeatToX(n.End);
                drag = (p.X > nx2 - U(9) && nx2 - nx1 > U(12)) ? DragMode.ResizeNote : DragMode.MoveNote;
                cacheDirty = true;
                Invalidate();
                return;
            }

            // 空白创建
            double beat = Snap(XToBeat(p.X));
            if (beat < 0) beat = 0;
            int pitch = YToPitch(p.Y);
            if (pitch < 0) pitch = 0;
            if (pitch > 127) pitch = 127;

            PushUndo();
            Note nn = new Note(beat, 0.25, pitch, 100);
            t.Notes.Add(nn);
            selectedNote = nn;
            drag = DragMode.CreateNote;
            dragNote = nn;
            createAnchorBeat = beat;
            dragStartPx = e.Location;
            project.Dirty = true;
            engine.Sync();
            PreviewPitch(pitch);
            cacheDirty = true;
            Invalidate();
        }

        Note FindNote(Track t, PointF p)
        {
            for (int i = t.Notes.Count - 1; i >= 0; i--)
            {
                Note n = t.Notes[i];
                float x1 = BeatToX(n.Start);
                float x2 = BeatToX(n.End);
                float y = PitchToY(n.Pitch);
                if (p.X >= x1 - 6 && p.X <= x2 + 6 && p.Y >= y - 2 && p.Y <= y + rowH + 2)
                    return n;
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            PointF p = ToPt(e);
            bool changed = false;

            // 按钮 hover
            foreach (UiBtn b in btns)
            {
                if (!b.Visible) continue;
                bool h = b.R.Contains(p);
                if (h != b.Hover) { b.Hover = h; changed = true; }
            }
            if (bpmRect.Contains(p) && !btnBpmUp.R.Contains(p) && !btnBpmDown.R.Contains(p))
            {
                if (!btnBpm.Hover) { btnBpm.Hover = true; changed = true; }
            }
            else if (btnBpm.Hover) { btnBpm.Hover = false; changed = true; }

            if (btnAddTrack != null)
            {
                bool h = btnAddTrack.R.Contains(p);
                if (h != btnAddTrack.Hover) { btnAddTrack.Hover = h; changed = true; }
            }

            // 轨道 hover
            int th = -1;
            if (trackRect.Contains(p))
            {
                float top = trackRect.Y + U(42);
                int idx = (int)((p.Y - top) / U(ROW_H));
                if (idx >= 0 && idx < project.Tracks.Count && p.Y >= top) th = idx;
            }
            if (th != hoverTrack) { hoverTrack = th; changed = true; }

            // 音符 hover
            Note nh = null;
            if (gridRect.Contains(p) && project.Tracks.Count > 0 && drag == DragMode.None)
                nh = FindNote(project.Tracks[selectedTrack], p);
            if (nh != hoverNote) { hoverNote = nh; changed = true; }


            if (drag != DragMode.None) { DoDrag(e, p); changed = true; }

            if (changed) { cacheDirty = true; Invalidate(); }
        }

        void DoDrag(MouseEventArgs e, PointF p)
        {
            switch (drag)
            {
                case DragMode.CreateNote:
                    {
                        Track t = project.Tracks[selectedTrack];
                        double cur = Snap(XToBeat(p.X));
                        double len = cur - createAnchorBeat;
                        if (len < 0.25) len = 0.25;
                        dragNote.Length = len;
                        engine.Sync();
                        cacheDirty = true;
                        break;
                    }
                case DragMode.MoveNote:
                    {
                        Track t = project.Tracks[selectedTrack];
                        double db = (p.X - dragStartX) / pxPerBeat;
                        double ns = Snap(dragNoteOrigStart + db);
                        if (ns < 0) ns = 0;
                        int dp = (int)Math.Round((dragStartY - p.Y) / rowH);
                        int np = dragNoteOrigPitch + dp;
                        if (np < 0) np = 0;
                        if (np > 127) np = 127;
                        if (Math.Abs(ns - dragNote.Start) > 1e-6 || np != dragNote.Pitch)
                        {
                            dragNote.Start = ns;
                            dragNote.Pitch = np;
                            dragMoved = true;
                            engine.Sync();
                            cacheDirty = true;
                        }
                        break;
                    }
                case DragMode.ResizeNote:
                    {
                        double cur = XToBeat(p.X);
                        double delta = cur - (dragNoteOrigStart + dragNoteOrigLen);
                        delta = Snap(delta);
                        double len = dragNoteOrigLen + delta;
                        if (len < 0.0625) len = 0.0625;
                        dragNote.Length = Math.Round(len * 1000.0) / 1000.0;
                        statusText = "长度 " + dragNote.Length.ToString("0.##") + " 拍";
                        engine.Sync();
                        cacheDirty = true;
                        break;
                    }
                case DragMode.PanView:
                    {
                        double db = (dragStartPx.X - e.X) / pxPerBeat;
                        viewBeat = Math.Max(0, dragValue + db);
                        cacheDirty = true;
                        break;
                    }
                case DragMode.BpmDrag:
                    {
                        double d = (dragStartPx.Y - e.Y) / U(4);
                        double v = Math.Round(bpmDragValue + d);
                        v = Math.Max(40, Math.Min(240, v));
                        if (Math.Abs(v - project.Bpm) > 0.01)
                        {
                            project.Bpm = v;
                            engine.SetTempo(v);
                            engine.Sync();
                            cacheDirty = true;
                        }
                        break;
                    }
                case DragMode.TrackVolume:
                    {
                        if (project.Tracks.Count == 0) break;
                        Track t = project.Tracks[Math.Min(selectedTrack, project.Tracks.Count - 1)];
                        double v = (p.X - dragSliderRect.X) / dragSliderRect.Width;
                        if (v < 0) v = 0;
                        if (v > 1) v = 1;
                        t.Volume = v;
                        project.Dirty = true;
                        engine.Sync();
                        cacheDirty = true;
                        break;
                    }
                case DragMode.TrackPan:
                    {
                        Track t = project.Tracks[Math.Min(selectedTrack, project.Tracks.Count - 1)];
                        double v = (p.X - panRect.X) / panRect.Width * 2.0 - 1.0;
                        if (v < -1) v = -1;
                        if (v > 1) v = 1;
                        t.Pan = v;
                        project.Dirty = true;
                        engine.Sync();
                        cacheDirty = true;
                        break;
                    }
                case DragMode.SeekHead:
                    if (rulerRect.Contains(p) || gridRect.Contains(p))
                        SeekTo(Snap(XToBeat(p.X)));
                    break;
                case DragMode.BottomVolume:
                    SetBottomVolume(p.X);
                    break;
                case DragMode.BottomPan:
                    SetBottomPan(p.X);
                    break;
                case DragMode.NoteVel:
                    SetNoteVelByX(p.X);
                    break;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (drag != DragMode.None)
            {
                if (drag == DragMode.CreateNote || drag == DragMode.MoveNote || drag == DragMode.ResizeNote)
                {
                    if (project.Tracks.Count > 0)
                    {
                        project.Tracks[selectedTrack].SortNotes();
                        project.Dirty = true;
                    }
                }
                if (drag != DragMode.PanView) project.Dirty = true;
                drag = DragMode.None;
                dragMoved = false;
                engine.Sync();
                cacheDirty = true;
                Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            PointF p = ToPt(e);
            if (gridRect.Contains(p) && project.Tracks.Count > 0)
            {
                Note n = FindNote(project.Tracks[selectedTrack], p);
                if (n == null) n = selectedNote;
                if (n != null)
                {
                    PushUndo();
                    project.Tracks[selectedTrack].Notes.Remove(n);
                    if (selectedNote == n) selectedNote = null;
                    project.Dirty = true;
                    engine.Sync();
                    cacheDirty = true;
                    Invalidate();
                }
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            bool ctrl = (ModifierKeys & Keys.Control) == Keys.Control;
            bool shift = (ModifierKeys & Keys.Shift) == Keys.Shift;
            PointF wp = ToPt(e);

            // 顶栏数字区：滚轮连续调节
            if (bpmRect.Contains(wp))
            {
                SetBpm(project.Bpm + (e.Delta > 0 ? 1 : -1));
                return;
            }
            if (timeSigRect.Contains(wp))
            {
                CycleTimeSig(e.Delta > 0 ? 1 : -1);
                return;
            }
            if (barsRect.Contains(wp))
            {
                SetBars((int)project.Bars + (e.Delta > 0 ? 1 : -1));
                return;
            }

            if (ctrl)
            {
                double anchorBeat = XToBeat(e.X);
                float factor = e.Delta > 0 ? 1.12f : 0.89f;
                pxPerBeat = Math.Max(U(14), Math.Min(U(260), pxPerBeat * factor));
                viewBeat = anchorBeat - (e.X - gridRect.X) / pxPerBeat;
                if (viewBeat < 0) viewBeat = 0;
            }
            else if (shift)
            {
                viewBeat -= e.Delta / 120.0 * 0.75;
                if (viewBeat < 0) viewBeat = 0;
            }
            else
            {
                viewY -= (int)(e.Delta / 120.0 * U(56));
                if (e.Y < rulerRect.Bottom) { }
                ClampView();
            }
            cacheDirty = true;
            Invalidate();
        }

        // ---------------- 键盘 ----------------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Space)
            {
                TogglePlay();
                return true;
            }
            if (keyData == Keys.Home)
            {
                SeekTo(0);
                return true;
            }
            if (keyData == Keys.Delete)
            {
                if (selectedNote != null && project.Tracks.Count > 0)
                {
                    PushUndo();
                    project.Tracks[selectedTrack].Notes.Remove(selectedNote);
                    selectedNote = null;
                    project.Dirty = true;
                    engine.Sync();
                    cacheDirty = true;
                    Invalidate();
                }
                return true;
            }
            if (keyData == Keys.Up || keyData == Keys.Down)
            {
                if (project.Tracks.Count > 0)
                {
                    selectedTrack = (selectedTrack + (keyData == Keys.Up ? -1 : 1) + project.Tracks.Count) % project.Tracks.Count;
                    selectedNote = null;
                    cacheDirty = true;
                    Invalidate();
                }
                return true;
            }
            if (keyData == (Keys.Control | Keys.S)) { SaveProject(false); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.S)) { SaveProject(true); return true; }
            if (keyData == (Keys.Control | Keys.O)) { OpenProject(); return true; }
            if (keyData == (Keys.Control | Keys.E)) { ExportWav(); return true; }
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keyData == (Keys.Control | Keys.N)) { NewProject(); return true; }
            if (keyData == (Keys.Control | Keys.Up)) { NudgeNoteVel(1); return true; }
            if (keyData == (Keys.Control | Keys.Down)) { NudgeNoteVel(-1); return true; }
            if (keyData == Keys.F1) { ToggleHelp(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------------- 顶栏数字区 ----------------

        void SetBpm(double v)
        {
            int b = (int)Math.Round(v);
            if (b < 40) b = 40;
            if (b > 240) b = 240;
            if (b == (int)Math.Round(project.Bpm)) return;
            project.Bpm = b;
            project.Dirty = true;
            engine.SetTempo(project.Bpm);
            engine.Sync();
            cacheDirty = true;
            Invalidate();
            statusText = "BPM " + b;
        }

        static readonly int[][] TimeSigs = new int[][]
        {
            new int[] { 4, 4 }, new int[] { 3, 4 }, new int[] { 2, 4 }, new int[] { 6, 8 },
            new int[] { 5, 4 }, new int[] { 7, 8 }, new int[] { 12, 8 }, new int[] { 9, 8 }
        };

        void ApplyTimeSig(int num, int den)
        {
            project.BeatsPerBar = num;
            project.BeatUnit = den;
            project.Dirty = true;
            engine.SetLength(project.LengthBeats * project.BeatSeconds);
            engine.Sync();
            cacheDirty = true;
            Invalidate();
            statusText = "拍号 " + num + "/" + den;
        }

        void CycleTimeSig(int dir)
        {
            int cur = 0;
            for (int i = 0; i < TimeSigs.Length; i++)
                if (TimeSigs[i][0] == project.BeatsPerBar && TimeSigs[i][1] == project.BeatUnit) { cur = i; break; }
            int n = (cur + dir + TimeSigs.Length) % TimeSigs.Length;
            ApplyTimeSig(TimeSigs[n][0], TimeSigs[n][1]);
        }

        void ShowTimeSigMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.BackColor = Theme.Bg2;
            m.ForeColor = Theme.Text;
            m.Font = Theme.F(9.5f);
            for (int i = 0; i < TimeSigs.Length; i++)
            {
                int num = TimeSigs[i][0];
                int den = TimeSigs[i][1];
                ToolStripMenuItem it = new ToolStripMenuItem(num + "/" + den);
                if (num == project.BeatsPerBar && den == project.BeatUnit) it.Checked = true;
                it.Click += delegate { PushUndo(); ApplyTimeSig(num, den); };
                m.Items.Add(it);
            }
            m.Show(PointToScreen(new Point((int)timeSigRect.X, (int)timeSigRect.Bottom + 2)));
        }

        void SetBars(int bars)
        {
            if (bars < 1) bars = 1;
            if (bars > 128) bars = 128;
            if (bars == (int)project.Bars) return;
            project.Bars = bars;
            project.Dirty = true;
            engine.SetLength(project.LengthBeats * project.BeatSeconds);
            engine.Sync();
            cacheDirty = true;
            Invalidate();
            statusText = "长度 " + bars + " 小节";
        }

        void ShowBarsMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.BackColor = Theme.Bg2;
            m.ForeColor = Theme.Text;
            m.Font = Theme.F(9.5f);
            int[] opts = new int[] { 2, 4, 8, 12, 16, 24, 32, 48, 64 };
            for (int i = 0; i < opts.Length; i++)
            {
                int b = opts[i];
                ToolStripMenuItem it = new ToolStripMenuItem(b + " 小节");
                if (b == (int)project.Bars) it.Checked = true;
                it.Click += delegate { PushUndo(); SetBars(b); };
                m.Items.Add(it);
            }
            m.Show(PointToScreen(new Point((int)barsRect.X, (int)barsRect.Bottom + 2)));
        }
        // ---------------- 默认加载配置 ----------------

        static string SettingsFile { get { return System.IO.Path.Combine(Application.StartupPath, "设置.txt"); } }

        static System.Collections.Generic.List<string> ReadAutoList()
        {
            System.Collections.Generic.List<string> list = new System.Collections.Generic.List<string>();
            try
            {
                if (!System.IO.File.Exists(SettingsFile)) return list;
                string[] lines = System.IO.File.ReadAllLines(SettingsFile);
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i].Trim();
                    if (!ln.StartsWith("autoload=")) continue;
                    string v = ln.Substring(9);
                    if (v.Length == 0) break;
                    string[] parts = v.Split(new char[] { '|' });
                    for (int k = 0; k < parts.Length; k++)
                        if (parts[k].Trim().Length > 0) list.Add(parts[k].Trim());
                }
            }
            catch { }
            return list;
        }

        static void WriteAutoList(System.Collections.Generic.List<string> list)
        {
            try
            {
                string[] old = System.IO.File.Exists(SettingsFile) ? System.IO.File.ReadAllLines(SettingsFile) : new string[0];
                System.Collections.Generic.List<string> keep = new System.Collections.Generic.List<string>();
                for (int i = 0; i < old.Length; i++)
                    if (!old[i].Trim().StartsWith("autoload=")) keep.Add(old[i]);
                keep.Add("autoload=" + string.Join("|", list.ToArray()));
                System.IO.File.WriteAllLines(SettingsFile, keep.ToArray());
            }
            catch { }
        }

        bool IsAutoLoaded(string fileName)
        {
            System.Collections.Generic.List<string> l = ReadAutoList();
            if (l.Count == 0) return true;   // 没有配置 = 全部默认加载
            return l.Contains(fileName);
        }

        void ToggleAutoLoad(string filePath)
        {
            string fn = System.IO.Path.GetFileName(filePath);
            System.Collections.Generic.List<string> l = ReadAutoList();
            bool all = l.Count == 0;
            if (all)
            {
                // 当前是「全部」，转成显式列表后去掉这一项
                string[] repo = Synth.RepoFiles(SourceDir);
                for (int i = 0; i < repo.Length; i++)
                {
                    string name = System.IO.Path.GetFileName(repo[i]);
                    if (!string.Equals(name, fn, StringComparison.OrdinalIgnoreCase)) l.Add(name);
                }
            }
            else if (l.Contains(fn)) l.Remove(fn);
            else l.Add(fn);
            WriteAutoList(l);
            statusText = "已更新默认加载列表（重启后生效）";
            cacheDirty = true; Invalidate();
        }
        // ---------------- 导入采样音源 ----------------

        void ImportSample(bool persist)
        {
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.Title = persist ? "选择 .sf2 音源（会复制进音源文件夹，以后自动加载）" : "选择 .sf2 音源（仅本次运行使用）";
            ofd.Filter = "SoundFont (*.sf2)|*.sf2|所有文件 (*.*)|*.*";
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            string src = ofd.FileName;
            string dir = SourceDir;
            statusText = "正在加载音源…";
            cacheDirty = true; Invalidate();

            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                string dest = src;
                try
                {
                    if (persist)
                    {
                        if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                        dest = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(src));
                        if (!string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
                            System.IO.File.Copy(src, dest, true);
                    }
                }
                catch { dest = src; }

                Sf2Preset set = Synth.LoadSf2File(dest);
                string baseName = System.IO.Path.GetFileNameWithoutExtension(dest);
                BeginInvoke((Action)delegate
                {
                    if (set == null)
                    {
                        statusText = "音源加载失败：" + System.IO.Path.GetFileName(dest);
                        cacheDirty = true; Invalidate();
                        return;
                    }
                    Preset p = Synth.AddSamplePreset(set, baseName, dest);
                    if (p != null && !persist) p.Temp = true;
                    engine.Sync();
                    cacheDirty = true; Invalidate();
                    long mb = Synth.SampleBytes() / 1048576;
                    statusText = "已导入音源：" + (p != null ? p.Name : baseName) + "（总占用 " + mb + " MB）" +
                                 (mb > 1200 ? " · 内存占用偏高，建议在「音源管理」里卸载不用的音源" : "");
                });
            });
            th.IsBackground = true;
            th.Start();
        }
        // ---------------- 撤销 ----------------

        void PushUndo()
        {
            try
            {
                undoStack.Push(project.Clone());
                if (undoStack.Count > UNDO_MAX)
                {
                    Project[] arr = undoStack.ToArray();   // 新 -> 旧
                    undoStack.Clear();
                    for (int i = UNDO_MAX - 1; i >= 0; i--) undoStack.Push(arr[i]);
                }
            }
            catch { }
        }

        void ToggleLoop()
        {
            btnLoop.Active = !btnLoop.Active;
            engine.SetLoop(btnLoop.Active, project.LengthBeats);
            statusText = btnLoop.Active ? "循环播放：开" : "循环播放：关";
            cacheDirty = true;
            Invalidate();
        }

        void ToggleMetro()
        {
            btnMetro.Active = !btnMetro.Active;
            engine.Metronome = btnMetro.Active;
            statusText = btnMetro.Active ? "节拍器：开" : "节拍器：关";
            cacheDirty = true;
            Invalidate();
        }

        void ChangeBpm(int delta)
        {
            double v = Math.Max(40, Math.Min(240, project.Bpm + delta));
            if (Math.Abs(v - project.Bpm) < 0.01) return;
            project.Bpm = v;
            project.Dirty = true;
            engine.SetTempo(v);
            engine.Sync();
            statusText = "速度 " + v.ToString("0") + " BPM";
            cacheDirty = true;
            Invalidate();
        }

        void Undo()
        {
            if (undoStack.Count == 0)
            {
                statusText = "没有可撤销的操作";
                cacheDirty = true;
                Invalidate();
                return;
            }
            Project prev = undoStack.Pop();
            project = prev;
            if (selectedTrack >= project.Tracks.Count) selectedTrack = Math.Max(0, project.Tracks.Count - 1);
            selectedNote = null;
            hoverNote = null;
            project.Dirty = true;
            engine.Attach(project);
            engine.Sync();
            engine.SetLength(project.LengthBeats);
            engine.SetLoop(btnLoop.Active, project.LengthBeats);
            engine.SetTempo(project.Bpm);
            statusText = "已撤销";
            cacheDirty = true;
            Invalidate();
        }

        // ---------------- 文件 ----------------

        void NewProject()
        {
            if (!ConfirmDiscard()) return;
            Synth.RemoveTempPresets(project);
            project = Demo.Empty();
            selectedTrack = 0;
            selectedNote = null;
            headBeat = 0;
            undoStack.Clear();
            engine.Stop();
            engine.Attach(project);
            UpdateTitle();
            statusText = "已新建工程";
            cacheDirty = true;
            Invalidate();
        }

        bool ConfirmDiscard()
        {
            if (!project.Dirty) return true;
            DialogResult r = MessageBox.Show(this, "当前工程尚未保存，是否放弃修改？", "编曲工坊",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            return r == DialogResult.Yes;
        }

        string SuggestPath(string ext)
        {
            string dir = folderDefault;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch { dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
            return Path.Combine(dir, "未命名工程" + ext);
        }

        void SaveProject(bool saveAs)
        {
            string path = project.FilePath;
            if (saveAs || string.IsNullOrEmpty(path))
            {
                SaveFileDialog dlg = new SaveFileDialog();
                dlg.Filter = "编曲工坊工程 (*.tforge)|*.tforge";
                dlg.DefaultExt = "tforge";
                dlg.FileName = string.IsNullOrEmpty(path) ? "未命名工程.tforge" : Path.GetFileName(path);
                dlg.InitialDirectory = folderDefault;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            try
            {
                File.WriteAllText(path, ProjectJson.Write(project), new System.Text.UTF8Encoding(false));
                project.FilePath = path;
                project.Dirty = false;
                folderDefault = Path.GetDirectoryName(path);
                UpdateTitle();
                statusText = "已保存：" + Path.GetFileName(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：\n" + ex.Message, "编曲工坊", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            cacheDirty = true;
            Invalidate();
        }

        void OpenProject()
        {
            if (!ConfirmDiscard()) return;
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "编曲工坊工程 (*.tforge)|*.tforge|所有文件 (*.*)|*.*";
            dlg.InitialDirectory = folderDefault;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                Synth.RemoveTempPresets(project);
                Project p = ProjectJson.Read(File.ReadAllText(dlg.FileName));
                p.FilePath = dlg.FileName;
                p.Dirty = false;
                project = p;
                selectedTrack = 0;
                selectedNote = null;
                headBeat = 0;
                undoStack.Clear();
                folderDefault = Path.GetDirectoryName(dlg.FileName);
                engine.Stop();
                engine.Attach(project);
                engine.SetLoop(btnLoop.Active, project.LengthBeats);
                engine.SetLength(project.LengthBeats);
                project.Bpm = Math.Max(40, Math.Min(240, project.Bpm));
                FitViewToNotes();
                UpdateTitle();
                statusText = "已打开：" + Path.GetFileName(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开失败：\n" + ex.Message, "编曲工坊", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            cacheDirty = true;
            Invalidate();
        }

        /// <summary>把视图滚动到第一条有音符的轨道的音域中心。</summary>
        void FitViewToNotes()
        {
            for (int i = 0; i < project.Tracks.Count; i++)
            {
                Track t = project.Tracks[i];
                if (t.Notes.Count == 0) continue;
                double sum = 0;
                for (int k = 0; k < t.Notes.Count; k++) sum += t.Notes[k].Pitch;
                CenterPitch((int)Math.Round(sum / t.Notes.Count));
                return;
            }
        }

        void ExportWav()
        {
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.Filter = "WAV 音频 (*.wav)|*.wav";
            dlg.DefaultExt = "wav";
            string baseName = string.IsNullOrEmpty(project.FilePath) ? "未命名工程" : Path.GetFileNameWithoutExtension(project.FilePath);
            dlg.FileName = baseName + ".wav";
            dlg.InitialDirectory = folderDefault;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            Cursor prev = Cursor;
            Cursor = Cursors.WaitCursor;
            statusText = "正在渲染音频…";
            cacheDirty = true;
            Invalidate();
            Application.DoEvents();
            try
            {
                AudioEngine.ExportWav(project, dlg.FileName, btnMetro.Active, null);
                statusText = "已导出：" + Path.GetFileName(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导出失败：\n" + ex.Message, "编曲工坊", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusText = "导出失败";
            }
            Cursor = prev;
            cacheDirty = true;
            Invalidate();
        }

        void UpdateTitle()
        {
            string name = string.IsNullOrEmpty(project.FilePath) ? "未命名工程" : Path.GetFileNameWithoutExtension(project.FilePath);
            Text = name + (project.Dirty ? " *" : "") + "  —  编曲工坊";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ConfirmDiscard())
            {
                e.Cancel = true;
                return;
            }
            uiTimer.Stop();
            engine.Dispose();
            base.OnFormClosing(e);
        }

        void PreviewPitch(int pitch)
        {
            if (project.Tracks.Count == 0) return;
            if (pitch < 0) pitch = 0;
            if (pitch > 127) pitch = 127;
            Track t = project.Tracks[selectedTrack];
            engine.Preview(pitch, t.Instrument, 104);
        }

        double Snap(double beat)
        {
            if ((ModifierKeys & Keys.Alt) == Keys.Alt) return beat;
            double g = 0.25;
            return Math.Round(beat / g) * g;
        }

        void ToggleHelp()
        {
            showHelp = !showHelp;
            cacheDirty = true;
            Invalidate();
        }

        // 探针辅助（仅测试用）
        public void ProbePlay() { TogglePlay(); }
        public string ProbeAudio()
        {
            return "playing=" + engine.IsPlaying + " pos=" + engine.RawPosition.ToString("0.00")
                 + " underruns=" + engine.Underruns;
        }
        public void ProbeKeys() { selectedTrack = 0; cacheDirty = true; Invalidate(); }
        public void ProbeSelectTrack(int i) { if (i >= 0 && i < project.Tracks.Count) { selectedTrack = i; cacheDirty = true; Invalidate(); } }
        public void ProbeHelp() { showHelp = true; cacheDirty = true; Invalidate(); }
        public string ProbeStats()
        {
            Track t = project.Tracks[selectedTrack];
            return "track=" + selectedTrack + " notes=" + t.Notes.Count + " sel=" + (selectedNote != null ? selectedNote.Start + "/" + selectedNote.Pitch : "-");
        }
        public string ProbeLoad(string path)
        {
            try
            {
                Project p = ProjectJson.Read(File.ReadAllText(path));
                p.FilePath = path;
                p.Dirty = false;
                project = p;
                selectedTrack = 0;
                selectedNote = null;
                headBeat = 0;
                undoStack.Clear();
                engine.Stop();
                engine.Attach(project);
                engine.SetLoop(btnLoop.Active, project.LengthBeats);
                engine.SetLength(project.LengthBeats);
                FitViewToNotes();
                UpdateTitle();
                cacheDirty = true;
                Invalidate();
                int notes = 0;
                foreach (Track tr in project.Tracks) notes += tr.Notes.Count;
                return "ok tracks=" + project.Tracks.Count + " notes=" + notes + " bars=" + project.Bars + " bpm=" + project.Bpm;
            }
            catch (Exception ex) { return "fail " + ex.ToString().Replace("\n", " | ").Replace("\r", ""); }
        }
        public string ProbeInstruments()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < Synth.All.Length; i++) sb.Append(i + ":" + Synth.All[i].Name + " ");
            return sb.ToString();
        }
        public void ProbeMouse(int type, int x, int y)
        {
            MouseEventArgs e = new MouseEventArgs(MouseButtons.Left, 1, x, y, 0);
            if (type == 0) OnMouseDown(e);
            else if (type == 1) OnMouseMove(e);
            else OnMouseUp(e);
        }
        // 在卷帘上创建音符：beat/pitch 指定位置，lenBeats 指定长度
        public string ProbeCreateNote(double beat, int pitch, double lenBeats)
        {
            int x1 = (int)BeatToX(beat);
            int x2 = (int)BeatToX(beat + lenBeats);
            int y = (int)(PitchToY(pitch) + rowH * 0.5);
            int before = project.Tracks[selectedTrack].Notes.Count;
            ProbeMouse(0, x1, y);
            ProbeMouse(1, x2, y);
            ProbeMouse(2, x2, y);
            return "before=" + before + " after=" + project.Tracks[selectedTrack].Notes.Count + " drag=" + drag;
        }

        // ================= 音源管理面板 =================

        internal bool samplerOpen;
        RectangleF samplerCardRect;
        readonly System.Collections.Generic.List<RectangleF> samplerDefaultRects = new System.Collections.Generic.List<RectangleF>();
        readonly System.Collections.Generic.List<string> samplerDefaultPaths = new System.Collections.Generic.List<string>();
        readonly System.Collections.Generic.List<RectangleF> samplerRenameRects = new System.Collections.Generic.List<RectangleF>();
        readonly System.Collections.Generic.List<int> samplerRenameSlots = new System.Collections.Generic.List<int>();
        readonly System.Collections.Generic.List<RectangleF> samplerUnloadRects = new System.Collections.Generic.List<RectangleF>();
        readonly System.Collections.Generic.List<int> samplerUnloadSlots = new System.Collections.Generic.List<int>();
        readonly System.Collections.Generic.List<RectangleF> samplerLoadRects = new System.Collections.Generic.List<RectangleF>();
        readonly System.Collections.Generic.List<string> samplerLoadPaths = new System.Collections.Generic.List<string>();
        readonly System.Collections.Generic.List<RectangleF> samplerDelRects = new System.Collections.Generic.List<RectangleF>();
        readonly System.Collections.Generic.List<string> samplerDelPaths = new System.Collections.Generic.List<string>();
        RectangleF samplerImportRect;
        RectangleF samplerFolderRect, samplerCloseRect, samplerCopyRect;

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr ShellExecute(IntPtr hwnd, string op, string file, string param, string dir, int show);
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr ILCreateFromPath(string pszPath);
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        static extern void ILFree(IntPtr pidl);
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

        /// <summary>打开目录：优先用 shell 的「打开并定位」，它复用已运行的资源管理器，不新建 explorer 进程。</summary>
        static bool OpenFolder(string dir)
        {
            try
            {
                IntPtr pidl = ILCreateFromPath(dir);
                if (pidl != IntPtr.Zero)
                {
                    int hr = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
                    ILFree(pidl);
                    if (hr == 0) return true;
                }
            }
            catch { }
            try
            {
                IntPtr r = ShellExecute(IntPtr.Zero, "open", dir, null, null, 1);
                if ((long)r > 32) return true;
            }
            catch { }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
                return true;
            }
            catch { }
            try
            {
                Type t = Type.GetTypeFromProgID("Shell.Application");
                if (t != null)
                {
                    object shell = Activator.CreateInstance(t);
                    shell.GetType().InvokeMember("Explore", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { dir });
                    return true;
                }
            }
            catch { }
            return false;
        }

        public string SourceDir { get { return System.IO.Path.Combine(Application.StartupPath, "音源"); } }

        public void OpenSampler()
        {
            samplerOpen = true;
            cacheDirty = true;
            Invalidate();
        }

        void CloseSampler()
        {
            samplerOpen = false;
            cacheDirty = true;
            Invalidate();
        }

        void DrawSampler(Graphics g)
        {
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(170, 6, 8, 12)))
                g.FillRectangle(sh, ClientRectangle);

            float w = Math.Min(UI(880), ClientSize.Width - U(60));
            float h = UI(520);
            float x = (ClientSize.Width - w) * 0.5f;
            float y = (ClientSize.Height - h) * 0.5f;
            RectangleF card = new RectangleF(x, y, w, h);
            samplerCardRect = card;
            Theme.FillRound(g, Theme.Bg1, card, U(16));
            using (Pen pn = new Pen(Theme.Line))
                Theme.DrawRound(g, pn, card, U(16));

            Theme.Txt(g, "采样音源管理", Theme.FB(16f), Theme.Text, card.X + U(28), card.Y + U(22));
            Theme.Txt(g, "TUNEFORGE · SAMPLE MANAGER", Theme.F(7.5f), Theme.TextFaint, card.X + U(29), card.Y + U(48));

            long bytes = Synth.SampleBytes();
            string mem = "内存占用 " + (bytes / 1048576) + " MB";
            Theme.TextR(g, mem, Theme.FM(11f), Theme.Accent, new RectangleF(card.X + U(28), card.Y + U(24), card.Width - U(56), U(20)));

            // 内存条 + 系统内存
            float barY = card.Y + U(56);
            RectangleF barBg = new RectangleF(card.X + U(28), barY, card.Width - U(56), U(5));
            Theme.FillRound(g, Theme.Bg4, barBg, U(2.5f));
            float ratio = Math.Min(1f, bytes / (1200f * 1048576f));
            if (ratio > 0.01f)
                Theme.FillRound(g, ratio > 0.9f ? Theme.Danger : Theme.Accent,
                    new RectangleF(barBg.X, barBg.Y, barBg.Width * ratio, barBg.Height), U(2.5f));
            Theme.TextR(g, "已加载 " + Synth.SamplePresets().Count + " · 仓库 " + Synth.RepoFiles(SourceDir).Length + " 个文件",
                Theme.F(8.5f), Theme.TextFaint, new RectangleF(card.X + U(28), card.Y + U(40), card.Width - U(56), U(16)));
            Theme.Txt(g, "建议保持在 1200 MB 以内" + SysMemText(), Theme.F(8f), Theme.TextFaint, card.X + U(28), barY + U(8));

            samplerUnloadRects.Clear(); samplerUnloadSlots.Clear();
            samplerRenameRects.Clear(); samplerRenameSlots.Clear();
            samplerDefaultRects.Clear(); samplerDefaultPaths.Clear();
            samplerLoadRects.Clear(); samplerLoadPaths.Clear();
            samplerDelRects.Clear(); samplerDelPaths.Clear();

            float colW = (card.Width - U(84)) * 0.5f;
            float lx = card.X + U(28);
            float rx = lx + colW + U(28);
            float ly = card.Y + U(88);

            Theme.Txt(g, "已加载到内存", Theme.FB(9.5f), Theme.TextDim, lx, ly);
            Theme.Txt(g, "音源仓库（磁盘）", Theme.FB(9.5f), Theme.TextDim, rx, ly);
            ly += U(24);

            int n = 0;
            for (int i = 0; i < Synth.All.Length && n < 10; i++)
            {
                Preset p = Synth.All[i];
                if (p.Kind != 2 || p.Sf2 == null || p.Sf2.Pcm == null) continue;
                RectangleF row = new RectangleF(lx, ly, colW, U(38));
                Theme.FillRound(g, Theme.Bg2, row, U(8));
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillEllipse(b, row.X + U(12), row.Y + U(15), U(8), U(8));
                Theme.TextW(g, p.Name, Theme.FB(9.5f), Theme.Text, new RectangleF(row.X + U(30), row.Y + U(9), colW - U(196), U(20)));
                RectangleF rnBtn = new RectangleF(row.Right - U(152), row.Y + U(7), U(70), U(24));
                Theme.FillRound(g, Theme.Bg4, rnBtn, U(6));
                Theme.TextC(g, "重命名", Theme.F(8f), Theme.TextDim, rnBtn);
                samplerRenameRects.Add(rnBtn);
                samplerRenameSlots.Add(i);
                RectangleF un = new RectangleF(row.Right - U(76), row.Y + U(7), U(64), U(24));
                Theme.FillRound(g, Theme.Bg4, un, U(6));
                Theme.TextC(g, "卸载", Theme.F(8f), Theme.TextDim, un);
                samplerUnloadRects.Add(un);
                samplerUnloadSlots.Add(i);
                ly += U(44);
                n++;
            }
            if (n == 0)
                Theme.Txt(g, "还没有加载采样音源。", Theme.F(8.5f), Theme.TextFaint, lx, ly + U(2));

            string[] repo = Synth.RepoFiles(SourceDir);
            float ry = card.Y + U(88) + U(24);
            int rn = 0;
            for (int i = 0; i < repo.Length && rn < 10; i++)
            {
                string fp = repo[i];
                string fn = System.IO.Path.GetFileName(fp);
                bool loaded = false;
                for (int k = 0; k < Synth.All.Length; k++)
                {
                    if (Synth.All[k].Kind == 2 && Synth.All[k].Sf2 != null &&
                        string.Equals(Synth.All[k].Sf2.Name, System.IO.Path.GetFileNameWithoutExtension(fn), System.StringComparison.OrdinalIgnoreCase))
                    { loaded = true; break; }
                }
                RectangleF row = new RectangleF(rx, ry, colW, U(38));
                Theme.FillRound(g, Theme.Bg2, row, U(8));
                Theme.TextW(g, fn, Theme.FB(9f), loaded ? Theme.Accent : Theme.Text,
                    new RectangleF(row.X + U(12), row.Y + U(9), colW - U(216), U(20)));
                // 删除（最右）
                RectangleF del = new RectangleF(row.Right - U(72), row.Y + U(7), U(60), U(24));
                Theme.FillRound(g, Theme.Bg4, del, U(6));
                Theme.TextC(g, "删除", Theme.F(8f), Theme.Danger, del);
                samplerDelRects.Add(del);
                samplerDelPaths.Add(fp);
                // 默认开关（始终显示）
                bool isDefault = IsAutoLoaded(fn);
                RectangleF dflt = new RectangleF(row.Right - U(140), row.Y + U(7), U(62), U(24));
                Theme.FillRound(g, isDefault ? Theme.AccentDeep : Theme.Bg4, dflt, U(6));
                Theme.TextC(g, isDefault ? "默认 ✓" : "默认", Theme.F(7.5f),
                    isDefault ? Theme.Bg0 : Theme.TextDim, dflt);
                samplerDefaultRects.Add(dflt);
                samplerDefaultPaths.Add(fp);
                // 加载 / 已加载（始终显示）
                RectangleF ld = new RectangleF(row.Right - U(206), row.Y + U(7), U(60), U(24));
                if (loaded)
                {
                    Theme.TextC(g, "已加载", Theme.F(7.5f), Theme.TextFaint, ld);
                }
                else
                {
                    Theme.FillRound(g, Theme.Alpha(Theme.Accent, 60), ld, U(6));
                    Theme.TextC(g, "加载", Theme.F(8f), Theme.Accent, ld);
                    samplerLoadRects.Add(ld);
                    samplerLoadPaths.Add(fp);
                }
                ry += U(44);
                rn++;
            }
            if (rn == 0)
                Theme.Txt(g, "音源目录是空的。", Theme.F(8.5f), Theme.TextFaint, rx, ry + U(2));

            Theme.Txt(g, "卸载只释放内存并修正工程引用；删文件会从磁盘彻底删除 · 点击面板外空白处关闭",
                Theme.F(8f), Theme.TextFaint, card.X + U(28), card.Bottom - U(96));

            float by = card.Bottom - U(60);
            samplerImportRect = new RectangleF(card.X + U(28), by, U(150), U(36));
            Theme.FillRound(g, Theme.Accent, samplerImportRect, U(9));
            Theme.TextC(g, "导入新文件…", Theme.FB(9f), Color.FromArgb(10, 16, 22), samplerImportRect);

            samplerFolderRect = new RectangleF(card.X + U(190), by, U(170), U(36));
            Theme.FillRound(g, Theme.Bg4, samplerFolderRect, U(9));
            Theme.TextC(g, "打开音源文件夹", Theme.FB(9f), Theme.Text, samplerFolderRect);

            samplerCopyRect = new RectangleF(card.X + U(372), by, U(110), U(36));
            Theme.FillRound(g, Theme.Bg4, samplerCopyRect, U(9));
            Theme.TextC(g, "复制路径", Theme.FB(9f), Theme.Text, samplerCopyRect);

            samplerCloseRect = new RectangleF(card.Right - U(124), by, U(96), U(36));
            Theme.FillRound(g, Theme.Bg4, samplerCloseRect, U(9));
            Theme.TextC(g, "关闭", Theme.FB(9f), Theme.Text, samplerCloseRect);
        }
        static string SysMemText()
        {
            try
            {
                long tot = 0, avail = 0;
                SysMem(out tot, out avail);
                if (tot > 0) return " · 系统内存 " + ((tot + 536870912L) / 1073741824L) + " GB · 可用 " + (avail / 1073741824L) + " GB";
            }
            catch { }
            return "";
        }

        void HandleSamplerDown(PointF p)
        {
            for (int i = 0; i < samplerUnloadRects.Count; i++)
                if (samplerUnloadRects[i].Contains(p)) { UnloadSlot(samplerUnloadSlots[i]); return; }
            for (int i = 0; i < samplerLoadRects.Count; i++)
                if (samplerLoadRects[i].Contains(p)) { LoadSf2Async(samplerLoadPaths[i], false); return; }
            for (int i = 0; i < samplerDelRects.Count; i++)
                if (samplerDelRects[i].Contains(p)) { DeleteRepoFile(samplerDelPaths[i]); return; }
            for (int i = 0; i < samplerDefaultRects.Count; i++)
                if (samplerDefaultRects[i].Contains(p)) { ToggleAutoLoad(samplerDefaultPaths[i]); return; }
            for (int i = 0; i < samplerRenameRects.Count; i++)
            {
                if (!samplerRenameRects[i].Contains(p)) continue;
                int slot = samplerRenameSlots[i];
                if (slot < 0 || slot >= Synth.All.Length) return;
                string oldName = Synth.All[slot].Name;
                string nv = PromptForm.Ask(this, "重命名采样音色", "输入新的显示名称", oldName);
                if (nv != null && nv.Trim().Length > 0)
                {
                    Synth.All[slot].Name = nv.Trim();
                    cacheDirty = true; Invalidate();
                    statusText = "已重命名为 " + nv.Trim();
                }
                return;
            }

            if (samplerImportRect.Contains(p)) { ImportSample(); return; }
            if (samplerFolderRect.Contains(p))
            {
                string dir = SourceDir;
                try { if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir); } catch { }
                bool ok = OpenFolder(dir);
                statusText = ok ? "已打开音源文件夹" : ("无法打开文件夹：" + dir);
                cacheDirty = true;
                Invalidate();
                return;
            }
            if (samplerCopyRect.Contains(p))
            {
                try { Clipboard.SetText(SourceDir); statusText = "已复制音源目录路径"; }
                catch { statusText = "复制失败：" + SourceDir; }
                cacheDirty = true;
                Invalidate();
                return;
            }
            if (samplerCloseRect.Contains(p)) { CloseSampler(); return; }
            // 只有点击卡片外的遮罩才关闭
            if (samplerCardRect.Width > 0 && !samplerCardRect.Contains(p)) CloseSampler();
        }

        void UnloadSlot(int slot)
        {
            if (slot < 0 || slot >= Synth.All.Length) return;
            string nm = Synth.All[slot].Name;
            if (!Synth.RemoveSamplePreset(slot, project)) return;
            selectedNote = null;
            project.Dirty = true;
            engine.Sync();
            cacheDirty = true;
            Invalidate();
            statusText = "已卸载：" + nm;
        }

        void LoadSf2Async(string path, bool persist)
        {
            statusText = "正在解析：" + System.IO.Path.GetFileName(path);
            cacheDirty = true;
            Invalidate();
            System.Threading.Thread th = new System.Threading.Thread(delegate()
            {
                Sf2Preset set = null;
                try { set = Sf2Loader.Load(path); } catch { set = null; }
                string copied = path;
                if (persist && set != null)
                {
                    try
                    {
                        string dir = SourceDir;
                        if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                        string dst = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(path));
                        if (!string.Equals(dst, path, System.StringComparison.OrdinalIgnoreCase))
                            System.IO.File.Copy(path, dst, true);
                        copied = dst;
                    }
                    catch { }
                }
                string baseName = System.IO.Path.GetFileNameWithoutExtension(copied);
                if (set != null) baseName = set.Name;
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (set == null) { statusText = "解析失败：" + System.IO.Path.GetFileName(path); cacheDirty = true; Invalidate(); return; }
                        Synth.AddSamplePreset(set, baseName, copied);
                        engine.Sync();
                        cacheDirty = true;
                        Invalidate();
                        statusText = "已加载音色：" + baseName + "（总占用 " + (Synth.SampleBytes() / 1048576) + " MB）";
                    });
                }
                catch { }
            });
            th.IsBackground = true;
            th.Priority = System.Threading.ThreadPriority.BelowNormal;
            th.Start();
        }

        void DeleteRepoFile(string path)
        {
            string fn = System.IO.Path.GetFileName(path);
            if (MessageBox.Show(this, "确定从磁盘彻底删除「" + fn + "」吗？\n\n这个操作不可撤销。",
                "删除音源文件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            try
            {
                System.IO.File.Delete(path);
                statusText = "已删除文件：" + fn;
            }
            catch (Exception ex) { statusText = "删除失败：" + ex.Message; }
            cacheDirty = true;
            Invalidate();
        }

        void ImportSample()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "选择 SoundFont 文件";
                d.Filter = "SoundFont (*.sf2)|*.sf2|所有文件|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                LoadSf2Async(d.FileName, true);
            }
        }
        /// <summary>切换主题并保存到 设置.txt。</summary>
        public void SetTheme(int idx)
        {
            Theme.Apply(idx);
            BackColor = Theme.Bg0;
            TryDarkTitleBar();
            cacheDirty = true;
            Invalidate();
            SaveSettings();
            statusText = "主题：" + Theme.Names[Theme.Current];
        }

        static string SettingsPath
        {
            get { return System.IO.Path.Combine(Application.StartupPath, "设置.txt"); }
        }

        internal static int LoadSettings()
        {
            try
            {
                string p = SettingsPath;
                if (!System.IO.File.Exists(p)) return 0;
                string[] lines = System.IO.File.ReadAllLines(p);
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i].Trim();
                    if (ln.StartsWith("theme="))
                    {
                        int v;
                        if (int.TryParse(ln.Substring(6), out v) && v >= 0 && v < Theme.Names.Length) return v;
                    }
                }
            }
            catch { }
            return 0;
        }

        void SaveSettings()
        {
            try { System.IO.File.WriteAllText(SettingsPath, "theme=" + Theme.Current); } catch { }
        }

        public string ProbeSetTheme(int idx)
        {
            SetTheme(idx);
            return "theme=" + Theme.Current + " name=" + Theme.Names[Theme.Current] + " bg=" + Theme.Bg0.R + "," + Theme.Bg0.G + "," + Theme.Bg0.B;
        }

        public string ProbeThemes()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < Theme.Names.Length; i++) sb.Append(i + ":" + Theme.Names[i] + " ");
            return sb.ToString();
        }

        /// <summary>探针：直接调用打开文件夹。</summary>
        public string ProbeOpenFolder()
        {
            string dir = SourceDir;
            try { if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir); } catch { }
            bool ok = OpenFolder(dir);
            return "dir=" + dir + " ok=" + ok;
        }
    }

    /// <summary>单行文本输入的小对话框，用于重命名轨道、输入力度/长度。</summary>
    class PromptForm : Form
    {
        public string Value = "";
        TextBox box;

        /// <summary>弹一个输入框，返回新文本；取消返回 null。</summary>
        public static string Ask(IWin32Window owner, string title, string hint, string init)
        {
            using (PromptForm f = new PromptForm(title, init))
            {
                f.Text = title;
                if (f.ShowDialog(owner) != DialogResult.OK) return null;
                return f.Value;
            }
        }

        public PromptForm(string title, string init)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Bg2;
            ForeColor = Theme.Text;
            ClientSize = new Size((int)Theme.Px(320), (int)Theme.Px(112));

            box = new TextBox();
            box.Text = init;
            box.Bounds = new Rectangle((int)Theme.Px(16), (int)Theme.Px(16), (int)Theme.Px(288), (int)Theme.Px(26));
            box.BackColor = Theme.Bg3;
            box.ForeColor = Theme.Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = Theme.F(10f);
            Controls.Add(box);

            Button ok = new Button();
            ok.Text = "确定";
            ok.DialogResult = DialogResult.OK;
            ok.FlatStyle = FlatStyle.Flat;
            ok.Bounds = new Rectangle((int)Theme.Px(166), (int)Theme.Px(62), (int)Theme.Px(66), (int)Theme.Px(30));
            ok.Click += delegate { Value = box.Text; };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.Bounds = new Rectangle((int)Theme.Px(240), (int)Theme.Px(62), (int)Theme.Px(66), (int)Theme.Px(30));
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
    class DarkMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Theme.Bg2);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.Line))
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Rectangle r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            if (e.Item.Selected)
                Theme.FillRound(e.Graphics, Theme.Bg4, r, 5);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Theme.Text : Theme.TextDim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (Pen p = new Pen(Theme.Line))
                e.Graphics.DrawLine(p, 8, e.Item.Height / 2, e.Item.Width - 8, e.Item.Height / 2);
        }
    }
}
