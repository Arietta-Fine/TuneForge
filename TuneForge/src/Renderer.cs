using System;
using System.Collections.Generic;

namespace TuneForge
{
    public class TrackRender
    {
        public Preset Preset;
        public float Vol = 0.7f;
        public float PanL = 0.707f, PanR = 0.707f;
        public bool On = true;
        public Note[] Notes = new Note[0];
        public SynthVoice[] Voices;
        public DrumVoice[] Drums;
        public SampleVoice[] Samp;
        public int Cursor;
        readonly List<Sf2Zone> zoneBuf = new List<Sf2Zone>();

        /// <summary>该轨的一阶高通截止频率（Hz），用于清理低频堆积。</summary>
        public float HpFreq = 0f;
        public float HpCoef = 1f;
        float hxL, hyL, hxR, hyR;

        readonly float[] tmpL = new float[512];
        readonly float[] tmpR = new float[512];

        public TrackRender(Preset preset, int voices)
        {
            Preset = preset;
            Voices = new SynthVoice[voices];
            for (int i = 0; i < voices; i++) Voices[i] = new SynthVoice();
            Drums = new DrumVoice[voices];
            for (int i = 0; i < voices; i++) Drums[i] = new DrumVoice();
            Samp = new SampleVoice[voices];
            for (int i = 0; i < voices; i++) Samp[i] = new SampleVoice();
        }

        public void Reset()
        {
            for (int i = 0; i < Voices.Length; i++) Voices[i].Kill();
            for (int i = 0; i < Drums.Length; i++) Drums[i].Kill();
            for (int i = 0; i < Samp.Length; i++) Samp[i].Kill();
            Cursor = 0;
            hxL = hyL = hxR = hyR = 0f;
        }

        public void Trigger(Note n, double durationBeats, double bps, double sr)
        {
            double durSec = durationBeats / bps;
            Preset pr = (n.Inst >= 0 && n.Inst < Synth.All.Length) ? Synth.All[n.Inst] : Preset;
            if (pr.Kind == 2 && pr.Sf2 != null)
            {
                zoneBuf.Clear();
                pr.Sf2.Collect(n.Pitch, n.Vel, zoneBuf);
                for (int i = 0; i < zoneBuf.Count; i++)
                {
                    int vi = -1;
                    for (int k = 0; k < Samp.Length; k++) if (!Samp[k].Active) { vi = k; break; }
                    if (vi < 0) vi = Cursor % Samp.Length;
                    Cursor++;
                    Samp[vi].NoteOn(pr.Sf2, zoneBuf[i], n.Pitch, n.Vel, durSec, sr, PanL * Vol, PanR * Vol);
                }
                return;
            }
            if (pr.Kind == 1)
            {
                DrumVoice dv = Drums[Cursor % Drums.Length];
                Cursor++;
                dv.NoteOn(n.Pitch, n.Vel, pr, sr);
            }
            else
            {
                SynthVoice v = Voices[Cursor % Voices.Length];
                Cursor++;
                v.NoteOn(n.Pitch, n.Vel, pr, durSec, sr);
            }
        }

        void RenderVoices(float[] L, float[] R, int offset, int count)
        {
            float pl = PanL * Vol;
            float prr = PanR * Vol;
            // 三种声部池全部渲染：轨道音色与单音覆盖音色可能不同
            for (int i = 0; i < Voices.Length; i++)
                if (Voices[i].Active) Voices[i].Render(L, R, offset, count, pl, prr);
            for (int i = 0; i < Drums.Length; i++)
                if (Drums[i].Active) Drums[i].Render(L, R, offset, count, pl, prr);
            for (int i = 0; i < Samp.Length; i++)
                if (Samp[i].Active) Samp[i].Render(L, R, offset, count);
        }

        public void Render(float[] L, float[] R, int offset, int count)
        {
            if (!On) return;

            if (HpFreq <= 0f)
            {
                RenderVoices(L, R, offset, count);
                return;
            }

            if (count > tmpL.Length) count = tmpL.Length;
            for (int i = 0; i < count; i++) { tmpL[i] = 0f; tmpR[i] = 0f; }
            RenderVoices(tmpL, tmpR, 0, count);

            float a = HpCoef;
            float xl = hxL, yl = hyL, xr = hxR, yr = hyR;
            for (int i = 0; i < count; i++)
            {
                float il = tmpL[i];
                float ol = a * (yl + il - xl);
                xl = il; yl = ol;
                float ir = tmpR[i];
                float orr = a * (yr + ir - xr);
                xr = ir; yr = orr;
                L[offset + i] += ol;
                R[offset + i] += orr;
            }
            hxL = xl; hyL = yl; hxR = xr; hyR = yr;
        }
    }

    /// <summary>轻量立体声混响（Freeverb 结构的简化版）：给干声加上一点房间空间感，
    /// 这是去掉「合成器电子味」最有效的一步。</summary>
    class Reverb
    {
        const int NCOMB = 4, NAP = 2;
        static readonly int[] CombTune = { 1116, 1188, 1277, 1356 };
        static readonly int[] ApTune = { 556, 441 };
        const float Fb = 0.84f, Damp = 0.22f, ApFb = 0.5f;

        readonly float[][] combL = new float[NCOMB][];
        readonly float[][] combR = new float[NCOMB][];
        readonly float[][] apL = new float[NAP][];
        readonly float[][] apR = new float[NAP][];
        readonly int[] cposL = new int[NCOMB], cposR = new int[NCOMB];
        readonly int[] aposL = new int[NAP], aposR = new int[NAP];
        readonly float[] storeL = new float[NCOMB], storeR = new float[NCOMB];
        float hpL, hpR;

        public Reverb()
        {
            for (int i = 0; i < NCOMB; i++)
            {
                combL[i] = new float[CombTune[i]];
                combR[i] = new float[(int)(CombTune[i] * 1.02)];
            }
            for (int i = 0; i < NAP; i++)
            {
                apL[i] = new float[ApTune[i]];
                apR[i] = new float[(int)(ApTune[i] * 1.02)];
            }
        }

        public void Reset()
        {
            for (int i = 0; i < NCOMB; i++)
            {
                Array.Clear(combL[i], 0, combL[i].Length);
                Array.Clear(combR[i], 0, combR[i].Length);
                storeL[i] = 0f; storeR[i] = 0f;
                cposL[i] = 0; cposR[i] = 0;
            }
            for (int i = 0; i < NAP; i++)
            {
                Array.Clear(apL[i], 0, apL[i].Length);
                Array.Clear(apR[i], 0, apR[i].Length);
                aposL[i] = 0; aposR[i] = 0;
            }
            hpL = 0f; hpR = 0f;
        }

        static float Comb(float[] buf, ref int pos, float x, ref float store)
        {
            float y = buf[pos];
            store = y * (1f - Damp) + store * Damp;
            buf[pos] = x + store * Fb;
            pos++;
            if (pos >= buf.Length) pos = 0;
            return y;
        }

        static float Allpass(float[] buf, ref int pos, float x)
        {
            float y = buf[pos];
            float o = y - x;
            buf[pos] = x + y * ApFb;
            pos++;
            if (pos >= buf.Length) pos = 0;
            return o;
        }

        public void Process(float[] L, float[] R, int frames, float wet)
        {
            for (int i = 0; i < frames; i++)
            {
                float xl = L[i], xr = R[i];
                // 混响只吃 200Hz 以上，避免低频被拖成一团
                hpL += 0.03f * (xl - hpL);
                hpR += 0.03f * (xr - hpR);
                float inL = (xl - hpL) * 0.15f;
                float inR = (xr - hpR) * 0.15f;

                float accL = 0f, accR = 0f;
                for (int c = 0; c < NCOMB; c++)
                {
                    accL += Comb(combL[c], ref cposL[c], inL, ref storeL[c]);
                    accR += Comb(combR[c], ref cposR[c], inR, ref storeR[c]);
                }
                for (int a = 0; a < NAP; a++)
                {
                    accL = Allpass(apL[a], ref aposL[a], accL);
                    accR = Allpass(apR[a], ref aposR[a], accR);
                }
                L[i] = xl + accL * wet;
                R[i] = xr + accR * wet;
            }
        }
    }

    /// <summary>把工程数据渲染成立体声采样。音频线程与离线导出共用。</summary>
    public class Renderer
    {
        public const int SUB = 64;
        public const int VOICES = 24;
        public readonly object syncLock = new object();

        public TrackRender[] Tracks = new TrackRender[0];
        public double SampleRate = 44100;
        public bool MetroOn = false;

        double clickEnv, clickPh, clickInc;
        int metroLastBeat = -1;

        // 母带处理状态
        float dcxL, dcyL, dcxR, dcyR;
        float airLpL, airLpR;
        readonly Reverb verb = new Reverb();
        public float Width = 1.32f;      // 中侧宽度
        public float OutputGain = 1.20f; // 主输出增益
        public float Air = 0.38f;        // 高频搁架增益（提升清晰度）
        public float ReverbMix = 0.75f;   // 混响湿声量

        public Renderer(double sampleRate)
        {
            SampleRate = sampleRate;
            clickInc = 0; clickEnv = 0;
        }

        public void ResetMetro() { metroLastBeat = -1; clickEnv = 0; }

        public void Sync(Project p)
        {
            lock (syncLock)
            {
                if (Tracks.Length != p.Tracks.Count)
                {
                    TrackRender[] arr = new TrackRender[p.Tracks.Count];
                    for (int i = 0; i < arr.Length; i++)
                        arr[i] = new TrackRender(Synth.All[0], VOICES);
                    Tracks = arr;
                }

                for (int i = 0; i < p.Tracks.Count; i++)
                {
                    Track t = p.Tracks[i];
                    TrackRender tr = Tracks[i];
                    tr.Preset = Synth.All[t.Instrument];
                    tr.Vol = (float)(t.Volume * 0.85);
                    double pn = (t.Pan + 1.0) * 0.5;
                    if (pn < 0) pn = 0; if (pn > 1) pn = 1;
                    tr.PanL = (float)Math.Cos(pn * Math.PI * 0.5);
                    tr.PanR = (float)Math.Sin(pn * Math.PI * 0.5);
                    tr.On = p.Audible(t);

                    tr.HpFreq = tr.Preset.Kind == 2 ? 0f : HpForInstrument(t.Instrument);
                    tr.HpCoef = tr.HpFreq <= 0f ? 1f
                        : (float)Math.Exp(-2.0 * Math.PI * tr.HpFreq / SampleRate);

                    int count = t.Notes.Count;
                    Note[] notes = new Note[count];
                    for (int k = 0; k < count; k++)
                    {
                        Note src = t.Notes[k];
                        notes[k] = new Note(src.Start, src.Length, src.Pitch, src.Vel);
                    }
                    Array.Sort(notes, delegate(Note a, Note b) { return a.Start.CompareTo(b.Start); });
                    tr.Notes = notes;
                }
            }
        }

        public void Reset()
        {
            lock (syncLock)
            {
                for (int i = 0; i < Tracks.Length; i++) Tracks[i].Reset();
                clickEnv = 0;
                metroLastBeat = -1;
                dcxL = dcyL = dcxR = dcyR = 0;
                airLpL = airLpR = 0;
                verb.Reset();
            }
        }

        /// <summary>播放从中间开始时，补发已经跨过播放头长音。</summary>
        public void PlayFrom(double beat, double bps)
        {
            lock (syncLock)
            {
                for (int i = 0; i < Tracks.Length; i++)
                {
                    TrackRender tr = Tracks[i];
                    if (!tr.On) continue;
                    for (int k = 0; k < tr.Notes.Length; k++)
                    {
                        Note n = tr.Notes[k];
                        if (n.Start < beat - 0.0001 && n.End > beat + 0.02)
                            tr.Trigger(n, n.End - beat, bps, SampleRate);
                    }
                }
            }
        }

        public void Render(float[] L, float[] R, int frames, double startBeat, double bps)
        {
            lock (syncLock)
            {
                double dtBeat = bps / SampleRate;
                int done = 0;
                double beat = startBeat;
                while (done < frames)
                {
                    int n = frames - done;
                    if (n > SUB) n = SUB;
                    double b1 = beat + dtBeat * n;

                    for (int i = 0; i < Tracks.Length; i++)
                    {
                        TrackRender tr = Tracks[i];
                        if (!tr.On) continue;
                        Note[] ns = tr.Notes;
                        for (int k = 0; k < ns.Length; k++)
                        {
                            Note nt = ns[k];
                            if (nt.Start >= beat && nt.Start < b1)
                                tr.Trigger(nt, nt.Length, bps, SampleRate);
                        }
                    }

                    for (int i = 0; i < Tracks.Length; i++) Tracks[i].Render(L, R, done, n);
                    if (MetroOn) RenderClick(L, R, done, n, beat, b1);
                    beat = b1;
                    done += n;
                }

                for (int i = 0; i < frames; i++)
                {
                    // 一阶 DC blocker（去直流，~4Hz 高通）
                    float xl = L[i], xr = R[i];
                    float yl = xl - dcxL + 0.9995f * dcyL;
                    float yr = xr - dcxR + 0.9995f * dcyR;
                    dcxL = xl; dcyL = yl;
                    dcxR = xr; dcyR = yr;
                    L[i] = yl;
                    R[i] = yr;
                }

                // 混响：一点点房间空间感（湿声在软限幅之前，不会被压爆）
                verb.Process(L, R, frames, ReverbMix);

                for (int i = 0; i < frames; i++)
                {
                    // 母带：中侧加宽 → 高频搁架（提升清晰度）→ 主增益 → 软限幅
                    float m = (L[i] + R[i]) * 0.5f;
                    float s = (L[i] - R[i]) * 0.5f * Width;
                    float l0 = m + s, r0 = m - s;
                    airLpL += 0.09f * (l0 - airLpL);
                    airLpR += 0.09f * (r0 - airLpR);
                    float hl = l0 + Air * (l0 - airLpL);
                    float hr = r0 + Air * (r0 - airLpR);
                    L[i] = SoftClip(hl * OutputGain);
                    R[i] = SoftClip(hr * OutputGain);
                }
            }
        }

        /// <summary>按拍触发节拍器咔哒声。</summary>
        void RenderClick(float[] L, float[] R, int offset, int count, double b0, double b1)
        {
            int i0 = (int)Math.Floor(b0 + 1e-9);
            int i1 = (int)Math.Floor(b1 + 1e-9);
            if (i1 != metroLastBeat && (i1 > 0 || b0 <= 0.0001))
            {
                metroLastBeat = i1;
                clickEnv = 0.42;
                clickPh = 0;
                clickInc = ((i1 % 4) == 0 ? 1760.0 : 1245.0) / SampleRate;
            }

            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                if (clickEnv > 0.0001)
                {
                    float v = (float)(Math.Sin(SynthVoice.TwoPi * clickPh) * clickEnv);
                    L[i] += v;
                    R[i] += v;
                    clickPh += clickInc;
                    if (clickPh >= 1.0) clickPh -= 1.0;
                    clickEnv *= 0.99972;
                }
            }
        }

        static float SoftClip(float x)
        {
            float a = x < 0 ? -x : x;
            if (a <= 0.7f) return x;
            float y = 0.7f + 0.3f * (float)Math.Tanh((a - 0.7f) / 0.3f);
            return x < 0 ? -y : y;
        }

        /// <summary>按音色给轨道分配高通截止频率，避免低频声部互相糊在一起。</summary>
        static float HpForInstrument(int inst)
        {
            switch (inst)
            {
                case 3: return 42f;    // 低音：保留 sub，只去掉次声
                case 14: return 45f;   // 电贝斯：保留低频厚度
                case 16: return 32f;   // 鼓组：保留底鼓下潜
                case 1: return 210f;   // 温暖铺底：和弦不需要低音区
                case 2: return 175f;   // 拨弦
                case 6: return 140f;   // 管风琴
                case 7: return 165f;   // 弦乐
                case 5: return 190f;   // 铃音
                case 9: return 160f;   // 柔和钟琴
                case 10: return 120f;  // 普通钢琴
                case 11: return 140f;  // 尼龙吉他
                case 12: return 180f;  // 小提琴
                case 13: return 150f;  // 竖琴
                case 15: return 220f;  // 长笛
                default: return 145f;  // 主音类
            }
        }
    }
}
