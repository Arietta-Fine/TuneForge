using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TuneForge
{
    /// <summary>SF2 采样：PCM 数据与整个 smpl 块共享，start/end/loop 为全局 sample 索引。</summary>
    public class Sf2Sample
    {
        public string Name = "";
        public int Start, End, LoopStart, LoopEnd;
        public int Rate = 44100;
        public int RootKey = 60;
        public int FineTune, CoarseTune;
        public int Type;          // 1 mono / 2 right / 4 left
        public bool HasLoop { get { return LoopEnd > LoopStart + 16; } }
        public int Length { get { return End - Start; } }
    }

    public class Sf2Zone
    {
        public int LoKey = 0, HiKey = 127, LoVel = 0, HiVel = 127;
        public int SampleId = -1;
        public int RootKey = -1;
        public int CoarseTune, FineTune;
        public float Atten;      // 分贝（负值）
    }

    public class Sf2Preset
    {
        public string Name = "";
        public int Bank, Program;
        public List<Sf2Zone> Zones = new List<Sf2Zone>();
        public List<Sf2Sample> Samples = new List<Sf2Sample>();
        public short[] Pcm;
        /// <summary>整组采样的输出增益（不同音源录音电平差异大）。</summary>
        public float Gain = 1.0f;

        /// <summary>收集匹配该音高/力度的 zone（通常左右声道各一个）。</summary>
        public List<Sf2Zone> Collect(int key, int vel)
        {
            List<Sf2Zone> tmp = new List<Sf2Zone>();
            Collect(key, vel, tmp);
            return tmp;
        }

        /// <summary>收集匹配该音高/力度的 zone（通常左右声道各一个）。</summary>
        public void Collect(int key, int vel, List<Sf2Zone> into)
        {
            into.Clear();
            for (int i = 0; i < Zones.Count; i++)
            {
                Sf2Zone z = Zones[i];
                if (key < z.LoKey || key > z.HiKey) continue;
                if (vel < z.LoVel || vel > z.HiVel) continue;
                if (z.SampleId < 0 || z.SampleId >= Samples.Count) continue;
                into.Add(z);
            }
            // 力度层重叠时保留区间最窄的一层（选层最精确）
            if (into.Count > 2)
            {
                int best = 1000;
                for (int i = 0; i < into.Count; i++)
                {
                    int w = into[i].HiVel - into[i].LoVel;
                    if (w < best) best = w;
                }
                for (int i = into.Count - 1; i >= 0; i--)
                    if (into[i].HiVel - into[i].LoVel > best) into.RemoveAt(i);
            }
        }
    }

    /// <summary>极简 SF2 读取器：只取第一个 preset 及其 instrument 的 zone/sample。</summary>
    public static class Sf2Loader
    {
        static int U16(byte[] b, int p) { return b[p] | (b[p + 1] << 8); }
        static int I32(byte[] b, int p) { return b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24); }
        static uint U32(byte[] b, int p) { return (uint)I32(b, p); }

        class Chunk { public long Start; public int Size; }

        static void Walk(FileStream fs, long start, long end, Dictionary<string, Chunk> chunks)
        {
            long pos = start;
            byte[] h = new byte[8];
            while (pos + 8 <= end)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                if (fs.Read(h, 0, 8) < 8) break;
                string id = Encoding.ASCII.GetString(h, 0, 4);
                int size = I32(h, 4);
                if (size < 0) break;
                long body = pos + 8;
                if (id == "LIST")
                {
                    if (size >= 4) Walk(fs, body + 4, body + size, chunks);
                }
                else if (!chunks.ContainsKey(id))
                {
                    Chunk c = new Chunk(); c.Start = body; c.Size = size;
                    chunks[id] = c;
                }
                pos = body + size + (size & 1);
            }
        }

        public static Sf2Preset Load(string path)
        {
            Dictionary<string, Chunk> chunks = new Dictionary<string, Chunk>();
            long fileLen;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                fileLen = fs.Length;
                Walk(fs, 12, fileLen, chunks);
            }
            Chunk sh, ph, ins, pbag, pgen, ibag, igen, smp;
            if (!chunks.TryGetValue("shdr", out sh)) return null;
            if (!chunks.TryGetValue("phdr", out ph)) return null;
            if (!chunks.TryGetValue("inst", out ins)) return null;
            if (!chunks.TryGetValue("pbag", out pbag)) return null;
            if (!chunks.TryGetValue("pgen", out pgen)) return null;
            if (!chunks.TryGetValue("smpl", out smp)) return null;
            bool hasIbag = chunks.TryGetValue("ibag", out ibag);
            bool hasIgen = chunks.TryGetValue("igen", out igen);
            if (!hasIbag || !hasIgen) return null;

            // pdta 区一次性读入内存（通常 < 2MB）
            long lo = long.MaxValue, hi = 0;
            foreach (KeyValuePair<string, Chunk> kv in chunks)
            {
                if (kv.Key == "smpl") continue;
                if (kv.Value.Start < lo) lo = kv.Value.Start;
                long e = kv.Value.Start + kv.Value.Size;
                if (e > hi) hi = e;
            }
            if (lo == long.MaxValue) return null;
            byte[] buf = new byte[hi - lo];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                fs.Seek(lo, SeekOrigin.Begin);
                int got = 0;
                while (got < buf.Length)
                {
                    int n = fs.Read(buf, got, buf.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
            }
            int shO = (int)(sh.Start - lo), phO = (int)(ph.Start - lo);
            int insO = (int)(ins.Start - lo), pbagO = (int)(pbag.Start - lo);
            int pgenO = (int)(pgen.Start - lo), ibagO = (int)(ibag.Start - lo);
            int igenO = (int)(igen.Start - lo);

            Sf2Preset p = new Sf2Preset();
            Chunk inam;
            if (chunks.TryGetValue("INAM", out inam))
                p.Name = Encoding.ASCII.GetString(buf, (int)(inam.Start - lo),
                    Math.Max(0, Math.Min(inam.Size - 1, buf.Length - (int)(inam.Start - lo)))).Trim('\0', ' ');

            int shCount = (sh.Size / 46) - 1;
            for (int i = 0; i < shCount; i++)
            {
                int o = shO + i * 46;
                Sf2Sample s = new Sf2Sample();
                s.Name = Encoding.ASCII.GetString(buf, o, 20).Trim('\0', ' ');
                s.Start = (int)U32(buf, o + 20);
                s.End = (int)U32(buf, o + 24);
                s.LoopStart = (int)U32(buf, o + 28);
                s.LoopEnd = (int)U32(buf, o + 32);
                s.Rate = (int)U32(buf, o + 36);
                s.RootKey = buf[o + 40];
                s.FineTune = (sbyte)buf[o + 41];
                s.Type = U16(buf, o + 44);
                if (s.Rate <= 0) s.Rate = 44100;
                if (s.End > s.Start) p.Samples.Add(s);
            }
            if (p.Samples.Count == 0) return null;

            int phCount = (ph.Size / 38) - 1;
            if (phCount < 1) return null;
            p.Program = U16(buf, phO + 20);
            p.Bank = U16(buf, phO + 22);
            int bagStart = U16(buf, phO + 24);
            int bagEnd = U16(buf, phO + phCount * 38 + 24);

            int instCount = ins.Size / 22;
            int pbagCount = pbag.Size / 4, pgenCount = pgen.Size / 4;
            int ibagCount = ibag.Size / 4, igenCount = igen.Size / 4;

            for (int z = bagStart; z < bagEnd && z + 1 < pbagCount; z++)
            {
                int g0 = U16(buf, pbagO + z * 4);
                int g1 = U16(buf, pbagO + (z + 1) * 4);
                int instIdx = -1;
                for (int gi = g0; gi < g1 && gi < pgenCount; gi++)
                    if (U16(buf, pgenO + gi * 4) == 41) instIdx = U16(buf, pgenO + gi * 4 + 2);
                if (instIdx < 0 || instIdx + 1 >= instCount) continue;

                int iz0 = U16(buf, insO + instIdx * 22 + 20);
                int iz1 = U16(buf, insO + (instIdx + 1) * 22 + 20);
                for (int iz = iz0; iz < iz1 && iz + 1 < ibagCount; iz++)
                {
                    int ig0 = U16(buf, ibagO + iz * 4);
                    int ig1 = U16(buf, ibagO + (iz + 1) * 4);
                    Sf2Zone zone = new Sf2Zone();
                    bool ok = false;
                    for (int gi = ig0; gi < ig1 && gi < igenCount; gi++)
                    {
                        int op = U16(buf, igenO + gi * 4);
                        int val = U16(buf, igenO + gi * 4 + 2);
                        switch (op)
                        {
                            case 43: zone.LoKey = val & 0xFF; zone.HiKey = val >> 8; break;
                            case 44: zone.LoVel = val & 0xFF; zone.HiVel = val >> 8; break;
                            case 53: zone.SampleId = val; ok = true; break;
                            case 58: zone.RootKey = val; break;
                            case 51: zone.CoarseTune = (sbyte)val; break;
                            case 52: zone.FineTune = (sbyte)val; break;
                            case 48: zone.Atten = -(val / 10f); break;
                        }
                    }
                    if (ok && zone.SampleId < p.Samples.Count) p.Zones.Add(zone);
                }
            }
            if (p.Zones.Count == 0) return null;

            // 读取 PCM
            int maxEnd = 0;
            for (int i = 0; i < p.Samples.Count; i++) if (p.Samples[i].End > maxEnd) maxEnd = p.Samples[i].End;
            int total = Math.Min(maxEnd, smp.Size / 2);
            p.Pcm = new short[total];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                fs.Seek(smp.Start, SeekOrigin.Begin);
                byte[] tmp = new byte[1 << 20];
                int done = 0;
                while (done < total)
                {
                    int want = Math.Min(tmp.Length, (total - done) * 2);
                    int got = fs.Read(tmp, 0, want);
                    if (got <= 0) break;
                    Buffer.BlockCopy(tmp, 0, p.Pcm, done * 2, got - (got & 1));
                    done += got / 2;
                }
            }
            for (int i = 0; i < p.Samples.Count; i++)
            {
                Sf2Sample s = p.Samples[i];
                if (s.End > p.Pcm.Length) s.End = p.Pcm.Length;
                if (s.LoopEnd > p.Pcm.Length) s.LoopEnd = p.Pcm.Length;
                if (s.LoopStart > s.End) s.LoopStart = s.End;
            }
            return p;
        }
    }
}
