using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TuneForge
{
    /// <summary>集中管理配色、字体与 GDI+ 绘图辅助。</summary>
    static class Theme
    {
        // 逻辑像素 -> 物理像素（DPI 缩放）
        public static float S = 1f;
        public static int Px(float v) { return (int)Math.Round(v * S); }
        public static float Pxf(float v) { return v * S; }

        // ── 主题系统 ──
        public static readonly string[] Names = new string[]
        {
            "暗夜青", "深海蓝", "暮光紫", "暖琥珀", "森林绿", "纸白", "晨雾蓝", "米黄"
        };
        public static int Current = 0;
        public static bool Light = false;

        // 基础色（随主题变化）
        public static Color Bg0, Bg1, Bg2, Bg3, Bg4;
        public static Color Line, LineSoft;
        public static Color Text, TextDim, TextFaint;

        // 派生色（由 Rebuild 计算）
        public static Color PanelBg, RulerBg, KeyBg, GridBg, RowDark, RowLight, GridLine;

        /// <summary>钢琴卷帘键位配色（浅色主题下自动反转）。</summary>
        public static Color KeyWhite { get { return Light ? Mix(Bg4, Color.White, 0.55f) : Color.FromArgb(198, 205, 218); } }
        public static Color KeyWhite2 { get { return Light ? Mix(Bg4, Color.White, 0.42f) : Color.FromArgb(178, 186, 202); } }
        public static Color KeyBlack { get { return Light ? Color.FromArgb(52, 56, 64) : Color.FromArgb(7, 9, 13); } }
        public static Color KeySep { get { return Light ? Mix(Bg4, Text, 0.35f) : Color.FromArgb(60, 67, 82); } }
        public static Color CardBg, CardSel, CardHov, TopA, TopB, BottomA, BottomB;

        // 强调色与轨道色不随主题变化
        public static readonly Color Accent = Color.FromArgb(64, 210, 190);
        public static readonly Color AccentDeep = Color.FromArgb(30, 150, 140);
        public static readonly Color Accent2 = Color.FromArgb(255, 138, 101);
        public static readonly Color Amber = Color.FromArgb(255, 197, 92);
        public static readonly Color Playhead = Color.FromArgb(255, 92, 112);
        public static readonly Color Danger = Color.FromArgb(255, 99, 99);
        public static readonly Color Ok = Color.FromArgb(104, 214, 143);

        public static readonly string[] ColorNames = new string[] { "青", "靛", "橙", "粉", "绿", "蓝", "紫", "黄" };
        public static readonly Color[] TrackColors = new Color[]
        {
            Color.FromArgb(72, 214, 196),
            Color.FromArgb(126, 143, 255),
            Color.FromArgb(255, 176, 100),
            Color.FromArgb(255, 122, 160),
            Color.FromArgb(146, 216, 120),
            Color.FromArgb(120, 200, 255),
            Color.FromArgb(210, 150, 255),
            Color.FromArgb(240, 210, 110),
        };

        static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        /// <summary>切换主题：只改背景色系，音符与强调色保持统一。</summary>
        public static void Apply(int idx)
        {
            if (idx < 0) idx = 0;
            if (idx >= Names.Length) idx = Names.Length - 1;
            Current = idx;
            Light = idx >= 5;

            // 文字色必须先重置，否则从浅色切回深色会残留深灰
            Text = C(233, 237, 245);
            TextDim = C(139, 150, 170);
            TextFaint = C(96, 106, 125);

            switch (idx)
            {
                case 1: // 深海蓝
                    Bg0 = C(16, 26, 50); Bg1 = C(22, 34, 62); Bg2 = C(28, 42, 76);
                    Bg3 = C(38, 56, 98); Bg4 = C(52, 78, 142);
                    break;
                case 2: // 暮光紫
                    Bg0 = C(26, 16, 44); Bg1 = C(34, 22, 56); Bg2 = C(44, 28, 70);
                    Bg3 = C(58, 40, 92); Bg4 = C(78, 60, 122);
                    break;
                case 3: // 暖琥珀
                    Bg0 = C(36, 24, 14); Bg1 = C(46, 32, 18); Bg2 = C(58, 40, 22);
                    Bg3 = C(74, 54, 30); Bg4 = C(96, 76, 55);
                    break;
                case 4: // 森林绿
                    Bg0 = C(14, 30, 20); Bg1 = C(20, 38, 26); Bg2 = C(26, 48, 32);
                    Bg3 = C(36, 64, 44); Bg4 = C(50, 90, 68);
                    break;
                case 5: // 纸白
                    Bg0 = C(250, 250, 252); Bg1 = C(244, 245, 248); Bg2 = C(236, 238, 243);
                    Bg3 = C(226, 229, 236); Bg4 = C(198, 206, 220);
                    Text = C(26, 31, 42); TextDim = C(88, 98, 116); TextFaint = C(140, 150, 168);
                    break;
                case 6: // 晨雾蓝
                    Bg0 = C(236, 243, 252); Bg1 = C(228, 238, 250); Bg2 = C(216, 230, 246);
                    Bg3 = C(200, 218, 240); Bg4 = C(168, 196, 226);
                    Text = C(22, 34, 52); TextDim = C(78, 96, 122); TextFaint = C(128, 146, 172);
                    break;
                case 7: // 米黄
                    Bg0 = C(252, 249, 240); Bg1 = C(248, 243, 230); Bg2 = C(242, 234, 216);
                    Bg3 = C(234, 222, 198); Bg4 = C(210, 196, 168);
                    Text = C(52, 42, 26); TextDim = C(108, 92, 66); TextFaint = C(156, 138, 106);
                    break;
                default: // 暗夜青
                    Bg0 = C(9, 11, 15); Bg1 = C(15, 18, 24); Bg2 = C(21, 25, 33);
                    Bg3 = C(28, 33, 43); Bg4 = C(36, 42, 55);
                    break;
            }

            Line = Mix(Bg4, Text, 0.22f);
            LineSoft = Mix(Bg1, Bg3, 0.50f);
            Rebuild();
        }

        /// <summary>按当前 Bg0..Bg4 重算派生色。所有主题共用同一套强调色与轨道色，只换背景。</summary>
        public static void Rebuild()
        {
            PanelBg = Bg1; RulerBg = Bg2; KeyBg = Bg1;
            GridBg = Mix(Bg0, Bg2, 0.60f);
            RowDark = Mix(GridBg, Color.Black, Light ? 0.05f : 0.15f);
            RowLight = Mix(GridBg, Bg3, 0.30f);
            GridLine = Mix(Bg3, Bg4, 0.50f);
            CardBg = Mix(Bg2, Bg1, 0.45f);
            CardSel = Light ? Mix(Bg3, Accent, 0.16f) : Mix(Bg3, Accent, 0.10f);
            CardHov = Mix(Bg2, Bg3, 0.55f);
            TopA = Bg3; TopB = Bg1; BottomA = Bg2; BottomB = Bg1;
        }

        static Theme() { Apply(0); }

        static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();
        public const string Family = "Microsoft YaHei UI";
        public const string Mono = "Consolas";

        public static Font F(float sizePt) { return F(Family, sizePt, FontStyle.Regular); }
        public static Font FB(float sizePt) { return F(Family, sizePt, FontStyle.Bold); }
        public static Font FM(float sizePt) { return F(Mono, sizePt, FontStyle.Regular); }

        public static Font F(string family, float sizePt, FontStyle style)
        {
            string key = family + "|" + sizePt.ToString("0.##") + "|" + (int)style;
            Font f;
            if (FontCache.TryGetValue(key, out f)) return f;
            f = new Font(family, sizePt, style, GraphicsUnit.Point);
            FontCache[key] = f;
            return f;
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Brush b, RectangleF r, float radius)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = RoundRect(r, radius)) g.FillPath(b, p);
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (SolidBrush b = new SolidBrush(c)) FillRound(g, b, r, radius);
        }

        public static void DrawRound(Graphics g, Pen pen, RectangleF r, float radius)
        {
            using (GraphicsPath p = RoundRect(r, radius)) g.DrawPath(pen, p);
        }

        public static void Txt(Graphics g, string s, Font f, Color c, float x, float y)
        {
            using (SolidBrush b = new SolidBrush(c))
                g.DrawString(s, f, b, x, y, StringFormat.GenericTypographic);
        }

        public static void TextC(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            try
            {
                using (SolidBrush b = new SolidBrush(c))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString(s, f, b, r, sf);
                }
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tf-paint.log"), "TextC r=" + r.ToString() + " s=[" + s + "] fs=" + f.Size + " " + ex.Message + "\n"); } catch { }
            }
        }

        public static void TextR(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (SolidBrush b = new SolidBrush(c))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(s, f, b, r, sf);
            }
        }

        public static void TextL(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            try
            {
                using (SolidBrush b = new SolidBrush(c))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Near;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString(s, f, b, r, sf);
                }
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tf-paint.log"), "TextL r=" + r.ToString() + " s=[" + s + "] fs=" + f.Size + " " + ex.Message + "\n"); } catch { }
            }
        }

        public static void TextW(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (SolidBrush b = new SolidBrush(c))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(s, f, b, r, sf);
            }
        }

        public static Color Mix(Color a, Color b, float t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        public static Color Hsv(float h, float s, float v)
        {
            h = h - (float)Math.Floor(h);
            float r = 0, g = 0, b = 0;
            float i = (float)Math.Floor(h * 6f);
            float f = h * 6f - i;
            float p = v * (1f - s);
            float q = v * (1f - f * s);
            float t = v * (1f - (1f - f) * s);
            switch (((int)i) % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
        }
    }
}
