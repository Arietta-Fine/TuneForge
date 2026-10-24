using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TuneForge
{
    public class Note
    {
        public double Start;    // 起始拍
        public double Length;   // 长度（拍）
        public int Pitch;       // MIDI 音高 0-127
        public int Vel = 100;   // 力度 1-127
        public int Inst = -1;   // >=0 时覆盖轨道音色（单个音换音色）

        public Note() { }
        public Note(double start, double length, int pitch, int vel)
        {
            Start = start; Length = length; Pitch = pitch; Vel = vel;
        }

        public Note(double start, double length, int pitch, int vel, int inst)
        {
            Start = start; Length = length; Pitch = pitch; Vel = vel; Inst = inst;
        }
        public Note Clone() { Note c = new Note(Start, Length, Pitch, Vel, Inst); return c; }
        public double End { get { return Start + Length; } }
    }

    public class Track
    {
        public string Name = "新轨道";
        public int Instrument = 0;
        public double Volume = 0.78;
        public double Pan = 0;
        public bool Mute = false;
        public bool Solo = false;
        public int ColorIndex = 0;
        public List<Note> Notes = new List<Note>();

        public void SortNotes() { Notes.Sort(delegate(Note a, Note b) { return a.Start.CompareTo(b.Start); }); }
        public Track Clone()
        {
            Track t = new Track();
            t.Name = Name; t.Instrument = Instrument; t.Volume = Volume; t.Pan = Pan;
            t.Mute = Mute; t.Solo = Solo; t.ColorIndex = ColorIndex;
            for (int i = 0; i < Notes.Count; i++) t.Notes.Add(Notes[i].Clone());
            return t;
        }
    }

    public class Project
    {
        public double Bpm = 100;
        public int BeatsPerBar = 4;
        public int BeatUnit = 4;
        public double Bars = 8;
        public List<Track> Tracks = new List<Track>();
        public string FilePath = null;
        public bool Dirty = false;

        public double LengthBeats { get { return Bars * BeatsPerBar; } }
        public double BeatSeconds { get { return 60.0 / Bpm; } }

        public bool AnySolo()
        {
            for (int i = 0; i < Tracks.Count; i++) if (Tracks[i].Solo) return true;
            return false;
        }

        public bool Audible(Track t)
        {
            if (t.Mute) return false;
            if (AnySolo() && !t.Solo) return false;
            return true;
        }

        public Project Clone()
        {
            Project p = new Project();
            p.Bpm = Bpm; p.BeatsPerBar = BeatsPerBar; p.BeatUnit = BeatUnit; p.Bars = Bars;
            for (int i = 0; i < Tracks.Count; i++) p.Tracks.Add(Tracks[i].Clone());
            return p;
        }

        public double MaxNoteEnd()
        {
            double m = 0;
            for (int i = 0; i < Tracks.Count; i++)
            {
                Track t = Tracks[i];
                for (int j = 0; j < t.Notes.Count; j++)
                    if (t.Notes[j].End > m) m = t.Notes[j].End;
            }
            return m;
        }
    }

    public class Preset
    {
        public string Name;
        public string Tag;
        public int WaveA;
        public int WaveB;
        public float MixB;
        public float Detune;    // B 声部相对音分
        public float OctB;      // B 声部八度偏移
        public float A, D, S, R;
        public float Cutoff, Reso, FiltEnv;
        public float Gain;
        public float FmRatio, FmDepth;
        public int Kind;        // 0 = 合成器, 1 = 鼓组, 2 = SF2 采样
        public bool Ks;         // true = Karplus-Strong 拨弦物理建模
        public Sf2Preset Sf2;
        public bool Temp;   // 仅为当前工程导入的临时音源
        public string SrcFile;   // 来源文件名（默认加载配置用）   // Kind == 2 时使用的采样集

        // ── 原声感参数 ──
        public float Bright;    // 亮度衰减：起音瞬间高频更亮，随后迅速回落
        public float NoiseAmt;  // 起音噪声：击弦 / 拨弦 / 吹气的那一下「沙」
        public float Spread;    // 第二根「弦」的失谐量（音分），制造弦组的合唱感
        public float PitchEnv;  // 起音音高偏移（音分），钢琴弦被敲击瞬间会略偏高
        public float Vib;       // 颤音深度（音分）
    }

    static class Synth
    {
        public const int WR_SINE = 0;
        public const int WR_SAW = 1;
        public const int WR_SQUARE = 2;
        public const int WR_TRI = 3;
        public const int WR_PULSE = 4;
        public const int WR_NOISE = 5;

        static Preset P(string name, string tag, int wa, int wb, float mixb, float det, float octb,
                        float a, float d, float s, float r, float cut, float res, float fe, float gain)
        {
            Preset p = new Preset();
            p.Name = name; p.Tag = tag;
            p.WaveA = wa; p.WaveB = wb; p.MixB = mixb; p.Detune = det; p.OctB = octb;
            p.A = a; p.D = d; p.S = s; p.R = r;
            p.Cutoff = cut; p.Reso = res; p.FiltEnv = fe; p.Gain = gain;
            p.FmRatio = 0; p.FmDepth = 0; p.Kind = 0;
            return p;
        }

        /// <summary>给预设补充「原声感」参数：亮度衰减、起音噪声、弦组失谐、起音音高、颤音。</summary>
        static Preset Tone(Preset p, float bright, float noise, float spread, float pEnv, float vib)
        {
            p.Bright = bright; p.NoiseAmt = noise; p.Spread = spread; p.PitchEnv = pEnv; p.Vib = vib;
            return p;
        }

        public static Preset[] All = BuildPresets();

        static Preset[] BuildPresets()
        {
            List<Preset> list = new List<Preset>();
            list.Add(Tone(P("模拟主音", "LEAD", WR_SAW, WR_SAW, 0.55f, 12f, 0f, 0.006f, 0.10f, 0.62f, 0.20f, 3600f, 0.22f, 0.35f, 0.34f), 0.15f, 0.02f, 8f, 0f, 5f));
            list.Add(Tone(P("温暖铺底", "PAD", WR_TRI, WR_SAW, 0.28f, -9f, 0f, 0.38f, 0.50f, 0.82f, 1.10f, 1350f, 0.12f, 0.18f, 0.30f), 0.10f, 0.02f, 12f, 0f, 3f));
            list.Add(Tone(P("拨弦", "PLUCK", WR_TRI, WR_SAW, 0.30f, 7f, 0f, 0.002f, 0.22f, 0.02f, 0.16f, 5200f, 0.20f, 0.25f, 0.36f), 0.35f, 0.10f, 4f, 6f, 0f));
            list.Add(Tone(P("低音", "BASS", WR_SQUARE, WR_SINE, 0.45f, 0f, -1f, 0.004f, 0.14f, 0.48f, 0.10f, 950f, 0.30f, 0.20f, 0.40f), 0.10f, 0.03f, 3f, 0f, 0f));
            list.Add(Tone(P("电钢琴", "KEYS", WR_SINE, WR_TRI, 0.28f, 0f, 1f, 0.004f, 0.95f, 0.22f, 0.40f, 5200f, 0.10f, 0.10f, 0.34f), 0.30f, 0.06f, 5f, 4f, 3f));
            list.Add(Tone(P("铃音", "BELL", WR_SINE, WR_SINE, 0.18f, 0f, 2f, 0.002f, 1.30f, 0.00f, 0.90f, 9000f, 0.05f, 0.00f, 0.30f), 0f, 0f, 0f, 0f, 0f));
            list.Add(Tone(P("管风琴", "ORGAN", WR_SQUARE, WR_SINE, 0.35f, 0f, 1f, 0.012f, 0.10f, 1.00f, 0.16f, 4800f, 0.10f, 0.05f, 0.26f), 0.02f, 0.01f, 3f, 0f, 0f));
            list.Add(Tone(P("弦乐", "STRINGS", WR_SAW, WR_SAW, 0.50f, -14f, 0f, 0.20f, 0.35f, 0.85f, 0.55f, 2400f, 0.14f, 0.15f, 0.24f), 0.12f, 0.03f, 14f, 0f, 11f));
            list.Add(Tone(P("明亮方波", "LEAD", WR_SQUARE, WR_PULSE, 0.40f, 5f, 0f, 0.004f, 0.16f, 0.55f, 0.14f, 4200f, 0.18f, 0.30f, 0.28f), 0.20f, 0.02f, 6f, 0f, 4f));
            list.Add(Tone(P("柔和钟琴", "KEYS", WR_SINE, WR_SINE, 0.35f, 0f, 1f, 0.003f, 0.80f, 0.05f, 0.55f, 7000f, 0.05f, 0.10f, 0.30f), 0.10f, 0.03f, 3f, 0f, 0f));

            // ── 原声乐器音色 ──
            list.Add(Tone(P("普通钢琴", "PIANO", WR_SAW, WR_TRI, 0.42f, 3f, 0f, 0.002f, 1.60f, 0.10f, 0.85f, 8200f, 0.06f, 0.75f, 0.36f), 0.85f, 0.40f, 7f, 10f, 0f));
            list.Add(Tone(P("尼龙吉他", "GUITAR", WR_TRI, WR_SAW, 0.42f, 6f, 0f, 0.001f, 0.85f, 0.04f, 0.55f, 4200f, 0.10f, 0.40f, 0.36f), 0.55f, 0.32f, 5f, 8f, 0f));
            list.Add(Tone(P("小提琴", "VIOLIN", WR_SAW, WR_SAW, 0.50f, -7f, 0f, 0.14f, 0.30f, 0.88f, 0.38f, 2800f, 0.18f, 0.12f, 0.26f), 0.18f, 0.05f, 16f, 0f, 14f));
            list.Add(Tone(P("竖琴", "HARP", WR_TRI, WR_SINE, 0.35f, 0f, 1f, 0.001f, 1.15f, 0.00f, 0.70f, 6500f, 0.06f, 0.30f, 0.35f), 0.60f, 0.30f, 5f, 6f, 0f));
            list.Add(Tone(P("电贝斯", "E.BASS", WR_SQUARE, WR_SINE, 0.40f, 0f, -1f, 0.003f, 0.35f, 0.55f, 0.12f, 820f, 0.26f, 0.30f, 0.40f), 0.15f, 0.05f, 4f, 3f, 0f));
            list.Add(Tone(P("长笛", "FLUTE", WR_SINE, WR_TRI, 0.22f, 0f, 1f, 0.075f, 0.25f, 0.85f, 0.22f, 4200f, 0.06f, 0.08f, 0.30f), 0.06f, 0.12f, 4f, 0f, 9f));

            Preset drums = new Preset();
            drums.Name = "鼓组";
            drums.Tag = "DRUMS";
            drums.Kind = 1;
            drums.Gain = 0.95f;
            drums.A = 0.001f; drums.D = 0.2f; drums.S = 0f; drums.R = 0.05f;
            drums.Cutoff = 8000f; drums.Reso = 0.1f; drums.FiltEnv = 0f;
            drums.WaveA = WR_NOISE; drums.WaveB = WR_SINE; drums.MixB = 0f; drums.Detune = 0; drums.OctB = 0;
            list.Add(drums);

            // FM 铃音特别处理
            Preset bell = list[5];
            bell.FmRatio = 3.01f; bell.FmDepth = 1.6f;

            // 拨弦 / 弹拨类用 Karplus-Strong：真实的"弦"振动，而不是振荡器叠包络
            if (list.Count > 14)
            {
                list[2].Ks = true;    // 拨弦
                list[11].Ks = true;   // 尼龙吉他
                list[13].Ks = true;   // 竖琴
                list[14].Ks = true;   // 电贝斯
            }

            return list.ToArray();
        }

        public static int DrumPreset { get { return All.Length - 1; } }

        // 鼓组音高映射
        public static readonly int[] DrumPitches = new int[] { 36, 38, 42, 46, 45, 39, 41, 43, 49, 50, 48, 44 };
        public static readonly string[] DrumNames = new string[] { "底鼓", "军鼓", "闭镲", "开镲", "中鼓", "拍手", "低鼓", "高鼓", "吊镲", "高音", "低通鼓", "踏板" };

        public static string DrumLabel(int pitch)
        {
            for (int i = 0; i < DrumPitches.Length; i++) if (DrumPitches[i] == pitch) return DrumNames[i];
            return "打击 " + pitch.ToString();
        }

        public static bool IsDrumPitch(int pitch)
        {
            for (int i = 0; i < DrumPitches.Length; i++) if (DrumPitches[i] == pitch) return true;
            return false;
        }

        public static string NoteName(int pitch)
        {
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            int oct = pitch / 12 - 1;
            return names[pitch % 12] + oct.ToString();
        }

        public static double Freq(int pitch) { return 440.0 * Math.Pow(2.0, (pitch - 69) / 12.0); }

        public static bool IsBlackKey(int pitch)
        {
            int n = pitch % 12;
            return n == 1 || n == 3 || n == 6 || n == 8 || n == 10;
        }

        // ── SF2 采样音源 ──
        public static Sf2Preset PianoSample, ViolinSample, AcousticSample;
        public static string SampleInfo = "";
        static bool samplesLoaded;

        /// <summary>扫描音源目录并接入采样音色（YAMAHA C5 钢琴 / Violins Legato / Acoustic Sustain）。</summary>
        public static void LoadSamples(string dir)
        {
            if (samplesLoaded) return;
            samplesLoaded = true;
            if (!System.IO.Directory.Exists(dir)) return;
            try
            {
                string piano = System.IO.Path.Combine(dir, "YAMAHA C5 Standard.sf2");
                string violin = System.IO.Path.Combine(dir, "Violins Legato.sf2");
                string acoust = System.IO.Path.Combine(dir, "Acoustic Sustain.sf2");
                PianoSample = Sf2Loader.Load(piano);
                ViolinSample = Sf2Loader.Load(violin);
                AcousticSample = Sf2Loader.Load(acoust);
                if (PianoSample != null) PianoSample.Gain = 1.00f;
                if (ViolinSample != null) ViolinSample.Gain = 0.42f;
                if (AcousticSample != null) AcousticSample.Gain = 0.72f;

                for (int i = 0; i < All.Length; i++)
                {
                    Preset p = All[i];
                    if (p.Name == "普通钢琴" && PianoSample != null) { p.Kind = 2; p.Sf2 = PianoSample; p.Name = "钢琴 · 采样"; }
                    else if (p.Name == "小提琴" && ViolinSample != null) { p.Kind = 2; p.Sf2 = ViolinSample; p.Name = "小提琴 · 采样"; }
                }
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                if (PianoSample != null) sb.Append("钢琴 " + (PianoSample.Pcm.Length * 2 / 1048576) + "MB");
                if (ViolinSample != null) sb.Append(" / 小提琴 " + (ViolinSample.Pcm.Length * 2 / 1048576) + "MB");
                if (AcousticSample != null) sb.Append(" / 原声 " + (AcousticSample.Pcm.Length * 2 / 1048576) + "MB");
                // 扫描目录里其余 .sf2：放进去就自动成为可用音色（数量不设上限）
                string[] extra = System.IO.Directory.GetFiles(dir, "*.sf2");
                System.Collections.Generic.List<string> allow = new System.Collections.Generic.List<string>();
                try
                {
                    string cfg = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "设置.txt");
                    if (System.IO.File.Exists(cfg))
                    {
                        string[] lines = System.IO.File.ReadAllLines(cfg);
                        for (int li = 0; li < lines.Length; li++)
                        {
                            string ln = lines[li].Trim();
                            if (!ln.StartsWith("autoload=")) continue;
                            string vv = ln.Substring(9);
                            if (vv.Length == 0) break;
                            string[] parts = vv.Split(new char[] { '|' });
                            for (int pi = 0; pi < parts.Length; pi++)
                                if (parts[pi].Trim().Length > 0) allow.Add(parts[pi].Trim());
                        }
                    }
                }
                catch { }
                System.Array.Sort(extra, System.StringComparer.OrdinalIgnoreCase);
                System.Collections.Generic.List<Preset> add = new System.Collections.Generic.List<Preset>();
                for (int i = 0; i < extra.Length; i++)
                {
                    string fn = System.IO.Path.GetFileName(extra[i]);
                    if (allow.Count > 0 && !allow.Contains(fn)) continue;
                    if (string.Equals(fn, "YAMAHA C5 Standard.sf2", System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(fn, "Violins Legato.sf2", System.StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        Sf2Preset ex = Sf2Loader.Load(extra[i]);
                        if (ex == null) continue;
                        Preset p = new Preset();
                        p.Name = ex.Name + " · 采样";
                        p.SrcFile = fn;
                        p.Tag = "SF2";
                        p.Kind = 2;
                        p.Sf2 = ex;
                        ex.Gain = 0.80f;
                        add.Add(p);
                        sb.Append(" / " + ex.Name + " " + (ex.Pcm.Length * 2 / 1048576) + "MB");
                    }
                    catch { }
                }
                if (add.Count > 0)
                {
                    Preset[] merged = new Preset[All.Length + add.Count];
                    System.Array.Copy(All, merged, All.Length);
                    for (int i = 0; i < add.Count; i++) merged[All.Length + i] = add[i];
                    All = merged;
                }

                SampleInfo = sb.ToString();
            }
            catch { PianoSample = null; ViolinSample = null; AcousticSample = null; }
        }

        /// <summary>移除「仅为当前工程导入」的临时音色，并修正受影响的轨道。</summary>
        public static void RemoveTempPresets(Project p)
        {
            for (int i = All.Length - 1; i >= 0; i--)
            {
                if (!All[i].Temp) continue;
                if (p != null)
                {
                    for (int ti = 0; ti < p.Tracks.Count; ti++)
                    {
                        Track t = p.Tracks[ti];
                        if (t.Instrument == i) t.Instrument = 0;
                        else if (t.Instrument > i) t.Instrument--;
                        for (int ni = 0; ni < t.Notes.Count; ni++)
                        {
                            Note n = t.Notes[ni];
                            if (n.Inst == i) n.Inst = -1;
                            else if (n.Inst > i) n.Inst--;
                        }
                    }
                }
                System.Collections.Generic.List<Preset> lst = new System.Collections.Generic.List<Preset>(All);
                lst.RemoveAt(i);
                All = lst.ToArray();
            }
        }
        /// <summary>音源目录里的所有 .sf2（磁盘仓库）。</summary>
        public static string[] RepoFiles(string dir)
        {
            if (!System.IO.Directory.Exists(dir)) return new string[0];
            string[] f = System.IO.Directory.GetFiles(dir, "*.sf2");
            System.Array.Sort(f, System.StringComparer.OrdinalIgnoreCase);
            return f;
        }

        /// <summary>把音色从内存里移除，并修正轨道引用索引。</summary>
        public static bool RemoveSamplePreset(int idx, Project project)
        {
            if (idx < 0 || idx >= All.Length) return false;
            if (All[idx].Kind != 2) return false;
            Preset[] n = new Preset[All.Length - 1];
            int j = 0;
            for (int i = 0; i < All.Length; i++) if (i != idx) n[j++] = All[i];
            All = n;
            if (project != null)
            {
                for (int i = 0; i < project.Tracks.Count; i++)
                {
                    Track t = project.Tracks[i];
                    if (t.Instrument > idx) t.Instrument--;
                    else if (t.Instrument == idx) t.Instrument = 0;
                    for (int ni = 0; ni < t.Notes.Count; ni++)
                    {
                        Note nt = t.Notes[ni];
                        if (nt.Inst > idx) nt.Inst--;
                        else if (nt.Inst == idx) nt.Inst = -1;
                    }
                }
            }
            return true;
        }

        /// <summary>把已解析的采样集登记成一个音色。</summary>
        /// <summary>单独加载一个 sf2 文件（导入用），失败返回 null。</summary>
        public static Sf2Preset LoadSf2File(string path)
        {
            try { return Sf2Loader.Load(path); }
            catch { return null; }
        }
        public static Preset AddSamplePreset(Sf2Preset set, string baseName, string path)
        {
            if (set == null) return null;
            set.Gain = 0.80f;
            Preset p = new Preset();
            p.Name = baseName + " · 采样";
            p.Tag = "SF2";
            p.Kind = 2;
            p.Sf2 = set;
            int old = All.Length;
            Preset[] n = new Preset[old + 1];
            System.Array.Copy(All, n, old);
            n[old] = p;
            All = n;
            return p;
        }
        /// <summary>已加载采样音源占用的字节数（按音源去重）。</summary>
        public static long SampleBytes()
        {
            long sum = 0;
            System.Collections.Generic.HashSet<Sf2Preset> seen = new System.Collections.Generic.HashSet<Sf2Preset>();
            for (int i = 0; i < All.Length; i++)
            {
                Sf2Preset s = All[i].Sf2;
                if (s == null || s.Pcm == null || !seen.Add(s)) continue;
                sum += (long)s.Pcm.Length * 2;
            }
            return sum;
        }

        /// <summary>列为采样音色的条目（面板里逐行展示用）。</summary>
        public static System.Collections.Generic.List<Preset> SamplePresets()
        {
            System.Collections.Generic.List<Preset> list = new System.Collections.Generic.List<Preset>();
            for (int i = 0; i < All.Length; i++)
                if (All[i].Kind == 2 && All[i].Sf2 != null) list.Add(All[i]);
            return list;
        }
    }

    static class Demo
    {
        // A 小调：Am - F - C - G，两遍
        public static Project Build()
        {
            Project p = new Project();
            p.Bpm = 100;
            p.Bars = 8;
            p.BeatsPerBar = 4;

            Track pad = NewTrack("铺底和弦", 1, 0, 0.50, 0.0);
            Track bass = NewTrack("低音线条", 3, 1, 0.58, -0.18);
            Track lead = NewTrack("主旋律", 0, 2, 0.80, 0.24);
            Track drums = NewTrack("鼓组律动", Synth.DrumPreset, 3, 0.94, 0.0);

            int[][] chords = new int[][]
            {
                new int[] { 57, 60, 64 }, // Am
                new int[] { 53, 57, 60 }, // F
                new int[] { 60, 64, 67 }, // C
                new int[] { 55, 59, 62 }, // G
            };
            int[] roots = new int[] { 45, 41, 48, 43 };

            for (int bar = 0; bar < 8; bar++)
            {
                int ci = bar % 4;
                double b0 = bar * 4;
                // Pad：每小节整和弦
                int[] ch = chords[ci];
                for (int k = 0; k < ch.Length; k++)
                    pad.Notes.Add(new Note(b0, 3.92, ch[k] + 12, 78));

                // Bass：根音 + 八度跳
                bass.Notes.Add(new Note(b0, 1.4, roots[ci], 108));
                bass.Notes.Add(new Note(b0 + 1.5, 0.4, roots[ci], 80));
                bass.Notes.Add(new Note(b0 + 2.0, 1.4, roots[ci] + 12, 96));
                if (bar % 2 == 1) bass.Notes.Add(new Note(b0 + 3.5, 0.4, roots[ci], 78));

                // 鼓
                drums.Notes.Add(new Note(b0, 0.25, 36, 116));
                drums.Notes.Add(new Note(b0 + 2, 0.25, 36, 108));
                if (bar % 2 == 1) drums.Notes.Add(new Note(b0 + 2.75, 0.25, 36, 92));
                drums.Notes.Add(new Note(b0 + 1, 0.25, 38, 116));
                drums.Notes.Add(new Note(b0 + 3, 0.25, 38, 116));
                for (int s = 0; s < 8; s++)
                    drums.Notes.Add(new Note(b0 + s * 0.5, 0.2, 42, (s % 2 == 0) ? 102 : 74));
                if (bar % 4 == 3) drums.Notes.Add(new Note(b0 + 3.5, 0.5, 46, 80));
            }

            // 主旋律：A 小调五声
            int[] mel = { 76, 74, 72, 71, 69, 72, 74, 76, 74, 72, 69, 67, 69, 71, 72, 74 };
            double[] starts = { 0, 0.75, 1.5, 2.0, 2.5, 3.0, 0, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5 };
            int[] melBars = { 4, 5, 6, 7, 4, 5, 6, 7, 4, 5, 6, 7, 4, 5, 6, 7 };
            for (int i = 0; i < mel.Length; i++)
            {
                double st = melBars[i] * 4 + starts[i];
                double len = (i % 4 == 3) ? 0.75 : 0.42;
                lead.Notes.Add(new Note(st, len, mel[i], 92));
            }

            pad.SortNotes(); bass.SortNotes(); lead.SortNotes(); drums.SortNotes();
            p.Tracks.Add(pad); p.Tracks.Add(bass); p.Tracks.Add(lead); p.Tracks.Add(drums);
            p.Dirty = false;
            return p;
        }

        static Track NewTrack(string name, int inst, int color, double vol, double pan)
        {
            Track t = new Track();
            t.Name = name; t.Instrument = inst; t.ColorIndex = color; t.Volume = vol; t.Pan = pan;
            return t;
        }

        public static Project Empty()
        {
            Project p = new Project();
            p.Bpm = 110; p.Bars = 8;
            Track a = NewTrack("主音", 0, 0, 0.75, 0);
            Track b = NewTrack("铺底", 1, 1, 0.65, 0);
            Track c = NewTrack("低音", 3, 2, 0.78, 0);
            Track d = NewTrack("鼓组", Synth.DrumPreset, 3, 0.70, 0);
            p.Tracks.Add(a); p.Tracks.Add(b); p.Tracks.Add(c); p.Tracks.Add(d);
            p.Dirty = false;
            return p;
        }
    }
}
