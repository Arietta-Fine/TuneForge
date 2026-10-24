using System;
using System.Collections.Generic;

namespace TuneForge
{
    /// <summary>SF2 采样声部：线性插值重采样 + 循环 + release 淡出。</summary>
    public class SampleVoice
    {
        public bool Active;
        public Sf2Preset Set;
        public Sf2Sample Sample;
        double pos;
        double step;
        int loopA, loopB;
        bool loop;
        float ampL, ampR;
        int state;              // 0 空闲 / 1 播放 / 2 释放
        long played, releaseAt;
        double env, relCoef;

        public void Kill() { state = 0; Active = false; }

        public void NoteOn(Sf2Preset set, Sf2Zone z, int pitch, int vel,
                           double durationSec, double sr, float panL, float panR)
        {
            if (set == null || set.Pcm == null || z.SampleId < 0 || z.SampleId >= set.Samples.Count) return;
            Set = set;
            Sample = set.Samples[z.SampleId];
            int root = z.RootKey >= 0 ? z.RootKey : Sample.RootKey;
            double cents = (pitch - root) * 100.0 + z.CoarseTune * 100.0 + z.FineTune
                           + Sample.CoarseTune * 100.0 + Sample.FineTune;
            step = Math.Pow(2.0, cents / 1200.0) * (Sample.Rate / sr);
            pos = Sample.Start;
            loopA = Sample.LoopStart;
            loopB = Sample.LoopEnd;
            loop = Sample.HasLoop && loopB > loopA + 64;

            float velGain = 0.62f + 0.38f * (vel / 127f);
            float att = (float)Math.Pow(10.0, z.Atten / 20.0);
            float g = velGain * att * Set.Gain;
            // 立体声采样：type 4 只进左，type 2 只进右；单声道两侧相同
            if (Sample.Type == 4) { ampL = g * panL; ampR = 0f; }
            else if (Sample.Type == 2) { ampL = 0f; ampR = g * panR; }
            else { ampL = g * panL; ampR = g * panR; }

            state = 1;
            played = 0;
            releaseAt = (long)(Math.Max(0.05, durationSec) * sr);
            env = 1.0;
            relCoef = Math.Exp(-1.0 / (0.11 * sr));
            Active = true;
        }

        public void NoteOff()
        {
            if (state == 1) state = 2;
        }

        public void Render(float[] L, float[] R, int offset, int count)
        {
            if (state == 0) return;
            short[] pcm = Set.Pcm;
            int len = pcm.Length;
            double lp = pos, st = step;
            int la = loopA, lb = loopB;
            bool lp2 = loop;
            float al = ampL, ar = ampR;
            double e = env, rc = relCoef;
            long pl = played, ra = releaseAt;
            int end = Sample.End;

            for (int i = 0; i < count; i++)
            {
                int i0 = (int)lp;
                if (i0 < 0) { i0 = 0; lp = 0; }
                if (i0 >= len - 1) { state = 0; break; }
                float fr = (float)(lp - i0);
                // int16 → [-1,1]
                float s = (pcm[i0] * (1f - fr) + pcm[i0 + 1] * fr) * 3.0517578e-5f;

                pl++;
                if (state == 1 && pl >= ra) state = 2;
                if (state == 2)
                {
                    e *= rc;
                    if (e < 0.0006) { state = 0; break; }
                }

                L[offset + i] += s * al * (float)e;
                R[offset + i] += s * ar * (float)e;

                lp += st;
                if (lp2 && lp >= lb - 1)
                {
                    double span = lb - la;
                    if (span > 8) lp -= span;
                    else { state = 0; break; }
                }
                else if (!lp2 && lp >= end - 1)
                {
                    state = 0; break;
                }
            }
            pos = lp; env = e; played = pl;
            if (state == 0) Active = false;
        }
    }
}
