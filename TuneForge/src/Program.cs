using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace TuneForge
{
    static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static string shotPath;
        static string shotAction;
        static string exportPath;
        static string audioTestDir;
        static string loadPath;
        static string starPath;
        static bool selfTest;

        static void Trace(string s)
        {
            if (shotPath == null) return;
            try { File.AppendAllText(shotPath + ".trace", DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + Environment.NewLine); }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--shot" && i + 1 < args.Length) shotPath = args[++i];
                if (args[i] == "--action" && i + 1 < args.Length) shotAction = args[++i];
                if (args[i] == "--theme" && i + 1 < args.Length)
                {
                    int tv;
                    if (int.TryParse(args[++i], out tv)) MainForm.DesignTheme = tv;
                }
                if (args[i] == "--export" && i + 1 < args.Length) exportPath = args[++i];
                if (args[i] == "--file" && i + 1 < args.Length) loadPath = args[++i];
                if (args[i] == "--make-star" && i + 1 < args.Length) starPath = args[++i];
                if (args[i] == "--audiotest" && i + 1 < args.Length) audioTestDir = args[++i];
                if (args[i] == "--selftest") selfTest = true;
            }
            if (selfTest) { RunSelfTest(); return; }
            if (starPath != null) { MakeStar(starPath); return; }
            if (audioTestDir != null) { RunAudioTest(audioTestDir); return; }
            if (exportPath != null)
            {
                try
                {
                    Project p = loadPath != null
                        ? ProjectJson.Read(File.ReadAllText(loadPath))
                        : Demo.Build();
                    AudioEngine.ExportWav(p, exportPath, false, null);
                    File.WriteAllText(exportPath + ".ok", "exported");
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(exportPath + ".err", ex.ToString()); } catch { }
                }
                return;
            }
            Trace("main start");
            try
            {
                try { SetProcessDPIAware(); } catch { }
                Trace("dpi done");
                Theme.S = 1f;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += OnThreadException;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;

                MainForm form = new MainForm();
                Trace("form created");
                if (shotPath != null) HookShot(form);
                Trace("run");
                Application.Run(form);
            }
            catch (Exception ex)
            {
                Trace("catch " + ex);
                try
                {
                    string msg = "编曲工坊启动失败：\n\n" + ex;
                    if (shotPath != null) File.WriteAllText(shotPath + ".err", ex.ToString());
                    MessageBox.Show(msg, "编曲工坊", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            }
        }

        /// <summary>生成「小星星」示例工程并保存为 .tforge。</summary>
        static void MakeStar(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                Project p = new Project();
                p.Bpm = 120;
                p.BeatsPerBar = 4;
                p.BeatUnit = 4;
                p.Bars = 12;

                Track mel = new Track();
                mel.Name = "小星星 · 钢琴";
                mel.Instrument = 10;      // 普通钢琴
                mel.ColorIndex = 0;
                mel.Volume = 1.00;
                mel.Pan = 0.08;

                int[] melody = new int[]
                {
                    72,72,79,79,81,81,79,
                    77,77,76,76,74,74,72,
                    79,79,77,77,76,76,74,
                    79,79,77,77,76,76,74,
                    72,72,79,79,81,81,79,
                    77,77,76,76,74,74,72
                };
                double t = 0;
                for (int i = 0; i < melody.Length; i++)
                {
                    bool lastOfPhrase = (i % 7) == 6;
                    double len = lastOfPhrase ? 2.0 : 1.0;
                    mel.Notes.Add(new Note(t, len, melody[i], lastOfPhrase ? 88 : 100));
                    t += len;
                }

                Track bass = new Track();
                bass.Name = "低音 · 贝斯";
                bass.Instrument = 3;      // 低音
                bass.ColorIndex = 1;
                bass.Volume = 0.88;
                bass.Pan = -0.10;
                int[] roots = new int[] { 36, 36, 41, 36, 36, 43, 43, 36, 36, 36, 41, 36 };
                for (int m = 0; m < roots.Length; m++)
                    bass.Notes.Add(new Note(m * 4.0, 4.0, roots[m], 84));

                // 中声部：每小节一个 C 大调三和弦，把 120-500Hz 的空洞填上
                Track harp = new Track();
                harp.Name = "和声 · 钢琴";
                harp.Instrument = 10;
                harp.ColorIndex = 4;
                harp.Volume = 0.58;
                harp.Pan = 0.16;
                int[][] chords = new int[][]
                {
                    new int[]{60,64,67}, new int[]{60,64,67}, new int[]{65,69,72}, new int[]{60,64,67},
                    new int[]{60,64,67}, new int[]{67,71,74}, new int[]{67,71,74}, new int[]{60,64,67},
                    new int[]{60,64,67}, new int[]{60,64,67}, new int[]{65,69,72}, new int[]{60,64,67}
                };
                for (int m = 0; m < chords.Length; m++)
                    for (int k = 0; k < chords[m].Length; k++)
                        harp.Notes.Add(new Note(m * 4.0, 3.5, chords[m][k], 62));

                p.Tracks.Add(mel);
                p.Tracks.Add(bass);
                p.Tracks.Add(harp);

                File.WriteAllText(path, ProjectJson.Write(p), new System.Text.UTF8Encoding(false));
                File.WriteAllText(path + ".ok",
                    "tracks=" + p.Tracks.Count + " notes=" + (mel.Notes.Count + bass.Notes.Count + harp.Notes.Count) +
                    " beats=" + p.LengthBeats);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".err", ex.ToString()); } catch { }
            }
        }

        /// <summary>为每个音色渲染一个测试片段，供后续频谱分析。</summary>
        static void RunAudioTest(string dir)
        {
            Directory.CreateDirectory(dir);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < Synth.All.Length; i++)
            {
                Preset pr = Synth.All[i];
                Project p = Demo.Empty();
                p.Bpm = 120;
                p.Bars = 2;
                p.Tracks.Clear();
                Track t = new Track();
                t.Name = pr.Name;
                t.Instrument = i;
                t.Volume = 0.8;
                t.Pan = 0;
                t.ColorIndex = 0;
                t.Notes = new System.Collections.Generic.List<Note>();
                p.Tracks.Add(t);
                if (pr.Kind == 1)
                {
                    for (int k = 0; k < Synth.DrumPitches.Length; k++)
                        t.Notes.Add(new Note(k * 0.5, 0.4, Synth.DrumPitches[k], 110));
                }
                else
                {
                    t.Notes.Add(new Note(0, 2.0, 60, 110));
                    t.Notes.Add(new Note(2.0, 2.0, 69, 110));
                }
                string f = Path.Combine(dir, i.ToString("00") + "_" + pr.Tag + ".wav");
                AudioEngine.ExportWav(p, f, false, null);
                sb.AppendLine(f + "  " + pr.Name);
            }
            File.WriteAllText(Path.Combine(dir, "index.txt"), sb.ToString());
        }

        static void RunSelfTest()
        {
            string log = Path.Combine(Path.GetTempPath(), "tf-selftest.txt");
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                Project p = Demo.Build();
                sb.AppendLine("demo tracks=" + p.Tracks.Count + " bpm=" + p.Bpm + " bars=" + p.Bars);
                int notes = 0;
                foreach (Track t in p.Tracks) notes += t.Notes.Count;
                sb.AppendLine("demo notes=" + notes + " length=" + p.LengthBeats);
                sb.AppendLine("audible=" + p.Tracks.FindAll(delegate (Track t) { return !t.Mute; }).Count);

                string json = ProjectJson.Write(p);
                sb.AppendLine("json bytes=" + json.Length);
                Project q = ProjectJson.Read(json);
                int notes2 = 0;
                foreach (Track t in q.Tracks) notes2 += t.Notes.Count;
                sb.AppendLine("roundtrip tracks=" + q.Tracks.Count + " notes=" + notes2 + " bpm=" + q.Bpm + " bars=" + q.Bars);

                bool ok = q.Tracks.Count == p.Tracks.Count && notes2 == notes &&
                          Math.Abs(q.Bpm - p.Bpm) < 0.01 && q.Bars == p.Bars;
                sb.AppendLine("roundtrip ok=" + ok);

                string wav = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tf-selftest.wav");
                DateTime t0 = DateTime.Now;
                AudioEngine.ExportWav(p, wav, false, null);
                sb.AppendLine("wav ms=" + (int)(DateTime.Now - t0).TotalMilliseconds +
                              " bytes=" + new FileInfo(wav).Length + " expectBytes=" + (44 + (long)(p.LengthBeats * p.BeatSeconds * 44100) * 4));

                string wav2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tf-selftest-metro.wav");
                AudioEngine.ExportWav(p, wav2, true, null);
                sb.AppendLine("wavMetro bytes=" + new FileInfo(wav2).Length);

                // 空工程
                Project e = Demo.Empty();
                string ej = ProjectJson.Write(e);
                Project e2 = ProjectJson.Read(ej);
                sb.AppendLine("empty roundtrip tracks=" + e2.Tracks.Count + " notes=" + (e2.Tracks[0].Notes.Count) + " bpm=" + e2.Bpm);

                // 音符合法性
                int bad = 0;
                foreach (Track t in p.Tracks)
                    foreach (Note n in t.Notes)
                        if (n.Pitch < 0 || n.Pitch > 127 || n.Length <= 0 || n.Start < 0) bad++;
                sb.AppendLine("bad notes=" + bad);

                sb.AppendLine("RESULT=PASS");
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION " + ex);
                sb.AppendLine("RESULT=FAIL");
            }
            try { File.WriteAllText(log, sb.ToString()); } catch { }
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "selftest.txt"), sb.ToString()); } catch { }
        }

        static void HookShot(MainForm form)
        {
            form.Shown += delegate
            {
                Trace("shown");
                Timer t = new Timer();
                t.Interval = 900;
                t.Tick += delegate
                {
                    t.Stop();
                    Trace("tick");
                    try
                    {
                        if (shotAction == "play")
                        {
                            form.ProbePlay();
                            System.Threading.Thread.Sleep(3000);
                            Trace("audio " + form.ProbeAudio());
                        }
                        else if (shotAction == "keys")
                        {
                            form.ProbeKeys();
                        }
                        else if (shotAction == "drums")
                        {
                            form.ProbeSelectTrack(3);
                        }
                        else if (shotAction == "help")
                        {
                            form.ProbeHelp();
                        }
                        else if (shotAction == "create")
                        {
                            Trace("create " + form.ProbeCreateNote(1.0, 64, 1.0));
                            Trace("stats " + form.ProbeStats());
                        }
                        else if (shotAction == "menu")
                        {
                            Trace("menu " + form.ProbeMenu());
                        }
                        else if (shotAction == "new")
                        {
                            form.ProbeNew();
                            Trace("stats " + form.ProbeStats());
                        }
                        else if (shotAction == "load" && loadPath != null)
                        {
                            Trace("load " + form.ProbeLoad(loadPath));
                        }
                        else if (shotAction == "instmenu")
                        {
                            Trace("instruments " + form.ProbeInstruments());
                        }
                        else if (shotAction == "sampler")
                        {
                            form.OpenSampler();
                        }
                        else if (shotAction == "openfolder")
                        {
                            Trace("openfolder " + form.ProbeOpenFolder());
                        }
                    }
                    catch { }
                    try
                    {
                        using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                            bmp.Save(shotPath, ImageFormat.Png);
                        }
                        Trace("saved");
                    }
                    catch (Exception ex)
                    {
                        try { File.WriteAllText(shotPath + ".err", ex.ToString()); } catch { }
                    }
                    Environment.Exit(0);
                };
                t.Start();
            };
        }

        static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            try
            {
                MessageBox.Show("发生了一个错误：\n\n" + e.Exception.Message, "编曲工坊", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }

        static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                Exception ex = e.ExceptionObject as Exception;
                string msg = ex == null ? "未知错误" : ex.Message;
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "tuneforge-error.log"),
                    DateTime.Now + "  " + (ex == null ? "" : ex.ToString()) + Environment.NewLine);
                MessageBox.Show("发生了一个未处理的错误：\n\n" + msg, "编曲工坊", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
