using System;
using System.Collections.Generic;

namespace TuneForge
{
    /// <summary>单声部合成器音源（带 PolyBLEP 抗混叠、ADSR 包络、状态变量低通滤波）。</summary>
    public class SynthVoice
    {
        public const double TwoPi = 6.28318530717958647692;
        public bool Active;

        Preset pr;
        double sr;
        double fA, fB, fC;
        double phA, phB, phC, incA, incB, incC;
        double env, envInc;
        int stage;          // 1 attack, 2 decay, 3 sustain, 4 release
        double age, dur, relCoef;
        double ic1, ic2, g, k, a1, a2, a3;
        int filtCnt;
        float amp;

        // 原声感状态
        double bright, brCoef;      // 亮度包络
        double noiseLp, noiseEnv, noiseCoef;
        double vibPh;
        double velNorm;
        double pitchMul;            // 当前音高乘子（起音偏移 + 颤音）
        double decEnv, decCoef;     // 指数衰减段
        float[] ksBuf; int ksLen, ksPos; float ksDamp;   // Karplus-Strong 拨弦

        static uint rngState = 0x9E3779B9u;

        public static float NextNoise()
        {
            rngState ^= rngState << 13;
            rngState ^= rngState >> 17;
            rngState ^= rngState << 5;
            return ((rngState & 0xFFFFFF) / 8388608f) - 1f;
        }

        public static double PolyBlep(double t, double dt)
        {
            if (dt <= 0) return 0;
            if (t < dt) { double x = t / dt; return x + x - x * x - 1.0; }
            if (t > 1.0 - dt) { double x = (t - 1.0) / dt; return x * x + x + x + 1.0; }
            return 0.0;
        }

        static double Frac(double x) { return x - Math.Floor(x); }

        static double Wave(int type, double ph, double inc)
        {
            switch (type)
            {
                case Synth.WR_SINE:
                    return Math.Sin(TwoPi * ph);
                case Synth.WR_SAW:
                    return 2.0 * ph - 1.0 - PolyBlep(ph, inc);
                case Synth.WR_SQUARE:
                    {
                        // ph=0 处上升沿：加 BLEP；ph=0.5 处下降沿：减 BLEP
                        double v = ph < 0.5 ? 1.0 : -1.0;
                        v += PolyBlep(ph, inc);
                        v -= PolyBlep(Frac(ph + 0.5), inc);
                        return v;
                    }
                case Synth.WR_TRI:
                    {
                        double t = Math.Abs(2.0 * ph - 1.0);
                        return 2.0 * t - 1.0;
                    }
                case Synth.WR_PULSE:
                    {
                        double pw = 0.32;
                        double v = ph < pw ? 1.0 : -1.0;
                        v += PolyBlep(ph, inc);
                        v -= PolyBlep(Frac(ph + 1.0 - pw), inc);
                        return v;
                    }
                default:
                    return NextNoise();
            }
        }

        public void NoteOn(int pitch, int vel, Preset p, double durationSec, double sampleRate)
        {
            pr = p; sr = sampleRate;
            Active = true; stage = 1; env = 0; age = 0;
            dur = durationSec; if (dur < 0.02) dur = 0.02;
            // 每个音符带 ±3 音分的随机微失谐：真实乐器从来不是"绝对准"的，
            // 这一点点偏差能让重复音符不再像机器复读机。
            fA = Synth.Freq(pitch) * Math.Pow(2.0, (NextNoise() * 3.0) / 1200.0);
            fB = fA * Math.Pow(2.0, p.OctB + p.Detune / 1200.0);
            fC = fA * Math.Pow(2.0, p.Spread / 1200.0);
            incA = fA / sr; incB = fB / sr; incC = fC / sr;
            // 随机初相位：让同一个音符每次触发的波形都不重合，避免叠加出「电子味」
            phA = (NextNoise() + 1f) * 0.5;
            phB = (NextNoise() + 1f) * 0.5;
            phC = (NextNoise() + 1f) * 0.5;
            if (phA < 0.02 || phA > 0.98) phA = 0.37;
            if (phB < 0.02 || phB > 0.98) phB = 0.63;
            ic1 = 0; ic2 = 0; filtCnt = 0;
            a1 = 0; a2 = 0; a3 = 0; g = 0; k = 0;
            envInc = 1.0 / (Math.Max(p.A, 0.001) * sr);
            relCoef = Math.Exp(-1.0 / (Math.Max(p.R, 0.03) * sr));

            bright = 1.0;
            brCoef = Math.Exp(-1.0 / (0.13 * sr));
            noiseLp = 0; noiseEnv = 1.0;
            noiseCoef = Math.Exp(-1.0 / (0.020 * sr));
            vibPh = 0;
            pitchMul = 1.0;
            velNorm = vel / 127.0;
            decEnv = 1.0;
            decCoef = Math.Exp(-2.2 / (Math.Max(p.D, 0.005) * sr));

            if (p.Ks)
            {
                int len = (int)Math.Round(sr / Math.Max(fA, 20.0));
                if (len < 8) len = 8; if (len > 8192) len = 8192;
                ksBuf = new float[len]; ksLen = len; ksPos = 0;
                float sum = 0;
                for (int i = 0; i < len; i++) { ksBuf[i] = NextNoise(); sum += Math.Abs(ksBuf[i]); }
                if (sum < 1e-6f) sum = 1f;
                float kscale = 0.55f * len / sum;
                for (int i = 0; i < len; i++) ksBuf[i] *= kscale;
                float kd = 0.9998f - 0.0006f * (float)(fA / 500.0);
                if (kd < 0.990f) kd = 0.990f;
                ksDamp = kd;
            }
            else { ksBuf = null; ksLen = 0; ksPos = 0; ksDamp = 1f; }

            float v = vel / 127f;
            v = 0.25f * v + 0.75f * v * v;
            amp = p.Gain * v;
        }

        public void Release() { if (stage < 4) stage = 4; }

        public void Render(float[] L, float[] R, int offset, int count, float panL, float panR)
        {
            if (!Active) return;
            bool useSpread = pr.Spread > 0.5f;
            bool useNoise = pr.NoiseAmt > 0.001f;
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                if (stage == 4 && env < 0.0006) { Active = false; return; }
                if (age >= dur && stage < 4) stage = 4;

                // 包络
                switch (stage)
                {
                    case 1:
                        env += envInc;
                        if (env >= 1.0) { env = 1.0; stage = 2; }
                        break;
                    case 2:
                        {
                            double target = pr.S;
                            if (decEnv > 0.0015)
                            {
                                decEnv *= decCoef;
                                env = target + (1.0 - target) * decEnv;
                            }
                            else { env = target; stage = 3; }
                            break;
                        }
                    case 3:
                        env = pr.S;
                        break;
                    case 4:
                        env *= relCoef;
                        break;
                }

                // 滤波器 / 音高包络（每 24 采样更新一次）
                if (--filtCnt <= 0)
                {
                    filtCnt = 24;
                    vibPh += 5.5 * 24.0 / sr;
                    if (vibPh > 1.0) vibPh -= 1.0;
                    double cents = pr.PitchEnv * bright;
                    if (pr.Vib > 0f) cents += pr.Vib * Math.Sin(TwoPi * vibPh);
                    pitchMul = 1.0 + cents * 0.0005776;

                    double cut = pr.Cutoff * (1.0 + pr.FiltEnv * env) * (1.0 + 1.5 * pr.Bright * bright);
                    cut *= (0.55 + 0.45 * velNorm);
                    if (cut < 40) cut = 40;
                    if (cut > 16000) cut = 16000;
                    double w = Math.Tan(Math.PI * cut / sr);
                    double res = pr.Reso; if (res > 0.94) res = 0.94;
                    k = 2.0 - 1.92 * res;
                    a1 = 1.0 / (1.0 + w * (w + k));
                    a2 = w * a1;
                    a3 = w * a2;
                }

                // 振荡器（拨弦类改用 Karplus-Strong 弦模型）
                double s;
                double ia = incA * pitchMul;
                if (ksBuf != null)
                {
                    int nx = ksPos + 1; if (nx >= ksLen) nx = 0;
                    float a0v = ksBuf[ksPos];
                    float a1v = ksBuf[nx];
                    ksBuf[ksPos] = (a0v + a1v) * 0.5f * ksDamp;
                    ksPos = nx;
                    s = a0v * 3.6;
                }
                else
                {
                if (pr.FmDepth > 0.0)
                {
                    double mod = Math.Sin(TwoPi * pr.FmRatio * phA) * pr.FmDepth * 0.25;
                    s = Math.Sin(TwoPi * (phA + mod));
                }
                else
                {
                    double sa = Wave(pr.WaveA, phA, ia);
                    if (pr.WaveA == Synth.WR_NOISE) s = sa;
                    else
                    {
                        double sb = Wave(pr.WaveB, phB, incB * pitchMul);
                        if (useSpread)
                        {
                            // 第二根「弦」：同波形微失谐，产生弦组的合唱与拍频
                            double sc = Wave(pr.WaveA, phC, incC * pitchMul);
                            sa = (sa + sc) * 0.5;
                        }
                        s = sa * (1.0 - pr.MixB) + sb * pr.MixB;
                    }
                }
                }

                // 状态变量低通
                double v3 = s - ic2;
                double v1 = a1 * ic1 + a2 * v3;
                double v2 = ic2 + a2 * ic1 + a3 * v3;
                ic1 = 2.0 * v1 - ic1;
                ic2 = 2.0 * v2 - ic2;
                double outv = v2 * env;

                // 起音噪声：击弦 / 拨弦 / 吹气的那一下「沙」
                if (useNoise)
                {
                    noiseEnv *= noiseCoef;
                    double nz = NextNoise();
                    noiseLp += (nz - noiseLp) * 0.45;
                    outv += (nz - noiseLp) * pr.NoiseAmt * noiseEnv * 1.5;
                }

                outv *= amp;

                phA += ia; if (phA >= 1.0) phA -= 1.0;
                phB += incB * pitchMul; if (phB >= 1.0) phB -= 1.0;
                if (useSpread) { phC += incC * pitchMul; if (phC >= 1.0) phC -= 1.0; }
                bright *= brCoef;
                age += 1.0 / sr;

                float f = (float)outv;
                L[i] += f * panL;
                R[i] += f * panR;
            }
        }

        public void Kill() { Active = false; }
    }

    /// <summary>鼓组音源（底鼓 / 军鼓 / 镲片 / 鼓桶 / 拍手）。</summary>
    public class DrumVoice
    {
        public bool Active;
        int kind;
        double t, sr, amp, decay, ph, baseFreq;
        double lpState;
        double lp2;
        double snap;
        static uint rng = 0x2545F491u;

        static float Noise()
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            return ((rng & 0xFFFFFF) / 8388608f) - 1f;
        }

        public static int KindOf(int pitch)
        {
            switch (pitch)
            {
                case 36: return 0;                    // 底鼓
                case 38: case 40: return 1;           // 军鼓
                case 42: case 44: return 2;           // 闭镲
                case 46: return 3;                    // 开镲
                case 45: case 41: case 43: case 47: case 48: case 50: return 4; // 鼓桶
                case 39: return 5;                    // 拍手
                case 49: case 51: case 57: return 6;  // 吊镲
                default: return 1;
            }
        }

        public void NoteOn(int pitch, int vel, Preset p, double sampleRate)
        {
            sr = sampleRate;
            kind = KindOf(pitch);
            t = 0; ph = 0; lpState = 0; lp2 = 0; snap = 0;
            double v = vel / 127.0; v = 0.3 * v + 0.7 * v * v;
            amp = (float)(p.Gain * v * 0.9);

            switch (kind)
            {
                case 0: baseFreq = 150; decay = 9.5; break;
                case 1: baseFreq = 200; decay = 21; break;
                case 2: decay = 62; break;
                case 3: decay = 13; break;
                case 4:
                    switch (pitch)
                    {
                        case 41: baseFreq = 150; break;
                        case 43: baseFreq = 230; break;
                        case 45: baseFreq = 190; break;
                        case 47: baseFreq = 260; break;
                        case 48: baseFreq = 300; break;
                        default: baseFreq = 340; break;
                    }
                    decay = 15; break;
                case 5: decay = 30; break;
                default: decay = 3.2; break;
            }
            Active = true;
        }

        public void Render(float[] L, float[] R, int offset, int count, float panL, float panR)
        {
            if (!Active) return;
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                double n = Noise();
                double outv = 0;
                switch (kind)
                {
                    case 0:
                        {
                            double f = 46.0 + 150.0 * Math.Exp(-t * 38.0);
                            ph += f / sr;
                            double body = Math.Sin(SynthVoice.TwoPi * ph) * Math.Exp(-t * decay);
                            // click 先低通，避免白噪尖刺（听感像电流声）
                            lp2 += (n - lp2) * 0.35;
                            double click = lp2 * Math.Exp(-t * 420.0) * 0.30;
                            outv = body * 0.95 + click;
                            break;
                        }
                    case 1:
                        {
                            lpState += (n - lpState) * 0.22;
                            double hp = n - lpState;
                            lp2 += (hp - lp2) * 0.68;   // 8 kHz 低通，能量集中在 1.5-8k
                            double body = Math.Sin(SynthVoice.TwoPi * baseFreq * t) * Math.Exp(-t * 26.0) * 0.45;
                            outv = lp2 * Math.Exp(-t * decay) * 1.05 + body;
                            break;
                        }
                    case 2:
                    case 3:
                        {
                            lpState += (n - lpState) * 0.55;
                            double hp = n - lpState;
                            lp2 += (hp - lp2) * 0.82;   // 12 kHz 低通，去掉刺耳的空气噪声
                            outv = lp2 * Math.Exp(-t * decay) * 1.30;
                            break;
                        }
                    case 4:
                        {
                            double f = baseFreq * (1.0 + 0.6 * Math.Exp(-t * 26.0));
                            ph += f / sr;
                            outv = Math.Sin(SynthVoice.TwoPi * ph) * Math.Exp(-t * decay) * 0.9;
                            break;
                        }
                    case 5:
                        {
                            lpState += (n - lpState) * 0.35;
                            double hp = n - lpState;
                            lp2 += (hp - lp2) * 0.575;  // 6 kHz 低通
                            double burst = 0;
                            for (int c = 0; c < 4; c++)
                            {
                                double dtc = t - c * 0.011;
                                if (dtc >= 0) burst += Math.Exp(-dtc * 150.0);
                            }
                            if (burst > 1) burst = 1;
                            outv = lp2 * burst * Math.Exp(-t * 6.0) * 1.15;
                            break;
                        }
                    default:
                        {
                            lpState += (n - lpState) * 0.3;
                            double hp = n - lpState;
                            lp2 += (hp - lp2) * 0.82;   // 12 kHz 低通
                            outv = lp2 * Math.Exp(-t * decay) * 0.95;
                            break;
                        }
                }

                t += 1.0 / sr;
                // 0.8ms 起音淡入，避免噪声成分在第一个采样处突跳产生咔嗒
                double atk = t / 0.0008;
                if (atk < 1.0) outv *= atk;
                float fv = (float)(outv * amp);
                L[i] += fv * panL;
                R[i] += fv * panR;

                if (t > 3.0) { Active = false; return; }
                if (outv < 0.00002 && outv > -0.00002 && t > 0.25) { Active = false; return; }
            }
        }

        public void Kill() { Active = false; }
    }

}
