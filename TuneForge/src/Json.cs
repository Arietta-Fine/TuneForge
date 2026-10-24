using System;
using System.IO;
using System.Text;

namespace TuneForge
{
    /// <summary>极简 JSON 读写，用于工程文件存储。</summary>
    static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            object v = ParseValue(text, ref i);
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON 意外结束");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' && i + 3 < s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (c == 'f' && i + 4 < s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (c == 'n' && i + 3 < s.Length && s.Substring(i, 4) == "null") { i += 4; return null; }
            return ParseNumber(s, ref i);
        }

        static object ParseObject(string s, ref int i)
        {
            System.Collections.Generic.Dictionary<string, object> d = new System.Collections.Generic.Dictionary<string, object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (s[i] != ':') throw new FormatException("JSON 缺少冒号");
                i++;
                object v = ParseValue(s, ref i);
                d[key] = v;
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; break; }
                throw new FormatException("JSON 对象格式错误");
            }
            return d;
        }

        static object ParseArray(string s, ref int i)
        {
            System.Collections.Generic.List<object> list = new System.Collections.Generic.List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (i < s.Length)
            {
                object v = ParseValue(s, ref i);
                list.Add(v);
                SkipWs(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; break; }
                throw new FormatException("JSON 数组格式错误");
            }
            return list;
        }

        static string ParseString(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            if (s[i] != '"') throw new FormatException("JSON 期望字符串");
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\')
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            string hex = s.Substring(i, 4);
                            i += 4;
                            sb.Append((char)Convert.ToInt32(hex, 16));
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
            string t = s.Substring(start, i - start);
            double d;
            if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
                return d;
            throw new FormatException("JSON 数字格式错误: " + t);
        }

        // ---- 辅助读取 ----
        public static System.Collections.Generic.Dictionary<string, object> Obj(object o)
        {
            System.Collections.Generic.Dictionary<string, object> d = o as System.Collections.Generic.Dictionary<string, object>;
            if (d == null) throw new FormatException("JSON 结构错误");
            return d;
        }

        public static System.Collections.Generic.List<object> Arr(object o)
        {
            System.Collections.Generic.List<object> l = o as System.Collections.Generic.List<object>;
            if (l == null) throw new FormatException("JSON 结构错误");
            return l;
        }

        public static double Num(System.Collections.Generic.Dictionary<string, object> d, string key, double def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v is double) return (double)v;
            return def;
        }

        public static int Int(System.Collections.Generic.Dictionary<string, object> d, string key, int def)
        {
            return (int)Math.Round(Num(d, key, def));
        }

        public static string Str(System.Collections.Generic.Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v is string) return (string)v;
            return def;
        }

        public static bool Bool(System.Collections.Generic.Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v is bool) return (bool)v;
            return def;
        }

        public static string Esc(string s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    static class ProjectJson
    {
        public static string Write(Project p)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"format\": \"tuneforge-project\",\n");
            sb.Append("  \"version\": 1,\n");
            sb.Append("  \"bpm\": ").Append(p.Bpm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"beatsPerBar\": ").Append(p.BeatsPerBar).Append(",\n");
            sb.Append("  \"beatUnit\": ").Append(p.BeatUnit).Append(",\n");
            sb.Append("  \"bars\": ").Append(p.Bars).Append(",\n");
            sb.Append("  \"tracks\": [\n");
            for (int i = 0; i < p.Tracks.Count; i++)
            {
                Track t = p.Tracks[i];
                sb.Append("    {\n");
                sb.Append("      \"name\": \"").Append(Json.Esc(t.Name)).Append("\",\n");
                sb.Append("      \"instrument\": ").Append(t.Instrument).Append(",\n");
                sb.Append("      \"volume\": ").Append(t.Volume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"pan\": ").Append(t.Pan.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("      \"mute\": ").Append(t.Mute ? "true" : "false").Append(",\n");
                sb.Append("      \"solo\": ").Append(t.Solo ? "true" : "false").Append(",\n");
                sb.Append("      \"color\": ").Append(t.ColorIndex).Append(",\n");
                sb.Append("      \"notes\": [");
                for (int k = 0; k < t.Notes.Count; k++)
                {
                    Note n = t.Notes[k];
                    if (k > 0) sb.Append(",");
                    sb.Append("\n        {\"s\":").Append(n.Start.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"l\":").Append(n.Length.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"p\":").Append(n.Pitch)
                      .Append(",\"v\":").Append(n.Vel);
                    if (n.Inst >= 0) sb.Append(",\"i\":").Append(n.Inst);
                    sb.Append("}");
                }
                if (t.Notes.Count > 0) sb.Append("\n      ");
                sb.Append("]\n");
                sb.Append("    }");
                if (i < p.Tracks.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        public static Project Read(string text)
        {
            System.Collections.Generic.Dictionary<string, object> root = Json.Obj(Json.Parse(text));
            Project p = new Project();
            p.Bpm = Math.Max(40, Math.Min(240, Json.Num(root, "bpm", 110)));
            p.BeatsPerBar = Math.Max(1, Math.Min(16, Json.Int(root, "beatsPerBar", 4)));
            p.BeatUnit = Math.Max(1, Math.Min(16, Json.Int(root, "beatUnit", 4)));
            p.Bars = Math.Max(1, Math.Min(512, Json.Int(root, "bars", 8)));
            p.Tracks.Clear();
            object otracks;
            if (root.TryGetValue("tracks", out otracks) && otracks is System.Collections.Generic.List<object>)
            {
                foreach (object o in Json.Arr(otracks))
                {
                    System.Collections.Generic.Dictionary<string, object> td = Json.Obj(o);
                    Track t = new Track();
                    t.Name = Json.Str(td, "name", "轨道");
                    t.Instrument = Math.Max(0, Math.Min(Synth.All.Length - 1, Json.Int(td, "instrument", 0)));
                    t.Volume = Math.Max(0, Math.Min(1, Json.Num(td, "volume", 0.8)));
                    t.Pan = Math.Max(-1, Math.Min(1, Json.Num(td, "pan", 0)));
                    t.Mute = Json.Bool(td, "mute", false);
                    t.Solo = Json.Bool(td, "solo", false);
                    t.ColorIndex = Math.Max(0, Json.Int(td, "color", 0));
                    object onotes;
                    if (td.TryGetValue("notes", out onotes) && onotes is System.Collections.Generic.List<object>)
                    {
                        foreach (object no in Json.Arr(onotes))
                        {
                            System.Collections.Generic.Dictionary<string, object> nd = Json.Obj(no);
                            double s = Math.Max(0, Json.Num(nd, "s", 0));
                            double l = Math.Max(0.05, Json.Num(nd, "l", 0.5));
                            int pi = Math.Max(0, Math.Min(127, Json.Int(nd, "p", 60)));
                            int v = Math.Max(1, Math.Min(127, Json.Int(nd, "v", 100)));
                            int ni = Json.Int(nd, "i", -1);
                            if (ni >= Synth.All.Length) ni = -1;
                            t.Notes.Add(new Note(s, l, pi, v, ni));
                        }
                    }
                    t.SortNotes();
                    p.Tracks.Add(t);
                }
            }
            if (p.Tracks.Count == 0) p.Tracks.Add(new Track());
            return p;
        }
    }
}
