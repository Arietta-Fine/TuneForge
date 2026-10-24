using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace TuneForge
{
    /// <summary>
    /// 基于 winmm waveOut 的立体声播放引擎。
    /// 采用「渲染线程 + 大环形缓冲 + 轻量回调」结构：
    /// 渲染线程独立负责合成，waveOut 回调只做一次极轻的拷贝，
    /// 彻底避免回调超时导致的缓冲欠载（爆音 / 电流声）。
    /// </summary>
    public class AudioEngine : IDisposable
    {
        // 44.1 kHz 标准音乐采样率；渲染链的滤波系数按此采样率整定
        public static int SR = 44100;
        const int BLOCK = 512;          // 每个 waveOut 缓冲的采样数（约 11.6 ms）
        const int NBUF = 6;             // 6 个缓冲 ≈ 70 ms 设备队列
        const int RING = 16384;         // 环形缓冲 ≈ 371 ms
        const int RMASK = RING - 1;

        const int WAVE_MAPPER = -1;
        const int CALLBACK_FUNCTION = 0x00030000;
        const int WOM_DONE = 0x3BD;
        const int WHDR_DONE = 0x00000001;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate void WaveOutProc(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll")]
        static extern int waveOutOpen(out IntPtr hwo, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);
        [DllImport("winmm.dll")]
        static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pHdr, int cbHdr);
        [DllImport("winmm.dll")]
        static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pHdr, int cbHdr);
        [DllImport("winmm.dll")]
        static extern int waveOutWrite(IntPtr hwo, IntPtr pHdr, int cbHdr);
        [DllImport("winmm.dll")]
        static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")]
        static extern int waveOutClose(IntPtr hwo);

        public Renderer Renderer;
        Project project;

        // ---- 环形缓冲（单生产者 = 渲染线程，单消费者 = waveOut 回调）----
        readonly float[] ringL = new float[RING];
        readonly float[] ringR = new float[RING];
        volatile int prodCount;
        volatile int consCount;

        Thread renderThread;
        volatile bool threadRun;

        // ---- 非托管资源 ----
        IntPtr hwo = IntPtr.Zero;
        IntPtr[] hdrPtrs;
        IntPtr[] dataPtrs;
        byte[][] managedBuf;
        WaveOutProc proc;
        IntPtr procPtr;
        bool allocated = false;
        volatile bool deviceOpen = false;
        volatile bool queuePaused = false;

        volatile int callbackDepth;
        int underruns;
        int peakUnderrun;

        readonly object renderLock = new object();
        readonly object previewLock = new object();
        readonly object deviceLock = new object();
        SynthVoice[] previewVoices = new SynthVoice[8];
        DrumVoice[] previewDrums = new DrumVoice[8];
        SampleVoice[] previewSamp = new SampleVoice[8];
        readonly List<Sf2Zone> previewZones = new List<Sf2Zone>();
        int previewSampCursor;
        int previewCursor;

        volatile bool playing = false;
        volatile float position = 0;
        volatile float curBps = 100f / 60f;
        volatile bool looping = true;
        volatile float loopEnd = 32f;
        volatile float lengthBeats = 32f;
        volatile bool metro = false;

        public event Action PlaybackEnded;

        public AudioEngine()
        {
            Renderer = new Renderer(SR);
            for (int i = 0; i < previewVoices.Length; i++) previewVoices[i] = new SynthVoice();
            for (int i = 0; i < previewDrums.Length; i++) previewDrums[i] = new DrumVoice();
            for (int i = 0; i < previewSamp.Length; i++) previewSamp[i] = new SampleVoice();
            threadRun = true;
            renderThread = new Thread(RenderLoop);
            renderThread.IsBackground = true;
            renderThread.Name = "TuneForge-Render";
            try { renderThread.Priority = ThreadPriority.AboveNormal; } catch { }
            renderThread.Start();
        }

        public bool IsPlaying { get { return playing; } }
        public bool Metronome { get { return metro; } set { metro = value; Renderer.MetroOn = value; } }
        public int Underruns { get { return underruns; } }

        /// <summary>UI 显示用的位置（补偿设备队列延迟）。</summary>
        public double UiPosition
        {
            get
            {
                double lat = NBUF * BLOCK / (double)SR * curBps;
                double p = position - lat;
                if (p < 0) p = 0;
                return p;
            }
        }

        public double RawPosition { get { return position; } }

        public void Attach(Project p)
        {
            project = p;
            SetTempo(p.Bpm);
            SetLoop(looping, p.LengthBeats);
            SetLength(p.LengthBeats);
            Renderer.Sync(p);
        }

        public void Sync()
        {
            if (project != null) Renderer.Sync(project);
        }

        public void SetTempo(double bpm)
        {
            curBps = (float)(bpm / 60.0);
        }

        public void SetLoop(bool on, double endBeat)
        {
            looping = on;
            loopEnd = (float)endBeat;
            if (loopEnd < 1f) loopEnd = 1f;
        }

        public void SetLength(double beats)
        {
            lengthBeats = (float)beats;
        }

        public void Seek(double beat)
        {
            lock (renderLock) { position = (float)beat; }
            Flush();
        }

        public void Play(double fromBeat)
        {
            EnsureDevice();
            lock (renderLock)
            {
                position = (float)fromBeat;
                Renderer.Reset();
                if (fromBeat > 0.0001) Renderer.PlayFrom(fromBeat, curBps);
                playing = true;
            }
            Flush();
            RestartQueue();
            WaitRing(BLOCK * NBUF, 160);
            Prefill();
        }

        /// <summary>等待渲染线程写入足够数据（避免刚开播时的空队列）。</summary>
        void WaitRing(int samples, int maxMs)
        {
            int waited = 0;
            while (prodCount - consCount < samples && waited < maxMs)
            {
                Thread.Sleep(1);
                waited++;
            }
        }

        public void Pause()
        {
            if (!playing) return;
            playing = false;
            Flush();
            RestartQueue();
            WaitRing(BLOCK * NBUF, 120);
            Prefill();
        }

        public void Stop()
        {
            playing = false;
            lock (renderLock)
            {
                Renderer.Reset();
                position = 0;
            }
            Flush();
            RestartQueue();
            WaitRing(BLOCK * NBUF, 120);
            Prefill();
        }

        /// <summary>试听一个音符（走同一个渲染线程，与主混音混合）。</summary>
        public void Preview(int pitch, int instrument, int velocity)
        {
            Preset pr = Synth.All[instrument];
            lock (previewLock)
            {
                if (pr.Kind == 2 && pr.Sf2 != null)
                {
                    previewZones.Clear();
                    pr.Sf2.Collect(pitch, velocity, previewZones);
                    for (int i = 0; i < previewZones.Count; i++)
                    {
                        SampleVoice sv = previewSamp[previewSampCursor % previewSamp.Length];
                        previewSampCursor++;
                        sv.NoteOn(pr.Sf2, previewZones[i], pitch, velocity, 0.9, SR, 0.5f, 0.5f);
                    }
                }
                else if (pr.Kind == 1)
                {
                    DrumVoice dv = previewDrums[previewCursor % previewDrums.Length];
                    previewCursor++;
                    dv.NoteOn(pitch, velocity, pr, SR);
                }
                else
                {
                    SynthVoice v = previewVoices[previewCursor % previewVoices.Length];
                    previewCursor++;
                    v.NoteOn(pitch, velocity, pr, 0.32, SR);
                }
            }
            EnsureDevice();
            if (!deviceOpen) return;
            if (!QueueRunning()) { RestartQueue(); WaitRing(BLOCK * NBUF, 80); Prefill(); }
        }

        // ================= 渲染线程 =================

        void RenderLoop()
        {
            float[] l = new float[BLOCK];
            float[] r = new float[BLOCK];
            while (threadRun)
            {
                int avail = RING - (prodCount - consCount);
                if (avail < BLOCK) { Thread.Sleep(1); continue; }

                bool wantPlay = playing;
                bool ended = false;

                lock (renderLock)
                {
                    for (int i = 0; i < BLOCK; i++) { l[i] = 0f; r[i] = 0f; }
                    double bps = curBps;
                    if (wantPlay)
                    {
                        float start = position;
                        Renderer.Render(l, r, BLOCK, start, bps);
                        double adv = BLOCK / (double)SR * bps;
                        double np = start + adv;
                        if (looping && np >= loopEnd)
                        {
                            position = 0f;
                            Renderer.Reset();
                            Renderer.PlayFrom(0, bps);
                        }
                        else if (!looping && np >= lengthBeats)
                        {
                            position = lengthBeats;
                            ended = true;
                        }
                        else position = (float)np;
                    }
                }

                lock (previewLock)
                {
                    for (int i = 0; i < previewVoices.Length; i++)
                        if (previewVoices[i].Active) previewVoices[i].Render(l, r, 0, BLOCK, 0.5f, 0.5f);
                    for (int i = 0; i < previewDrums.Length; i++)
                        if (previewDrums[i].Active) previewDrums[i].Render(l, r, 0, BLOCK, 0.5f, 0.5f);
                    for (int i = 0; i < previewSamp.Length; i++)
                        if (previewSamp[i].Active) previewSamp[i].Render(l, r, 0, BLOCK);
                }

                int w = prodCount;
                for (int i = 0; i < BLOCK; i++)
                {
                    ringL[(w + i) & RMASK] = l[i];
                    ringR[(w + i) & RMASK] = r[i];
                }
                prodCount = w + BLOCK;

                if (ended)
                {
                    playing = false;
                    Action ev = PlaybackEnded;
                    if (ev != null) { try { ev(); } catch { } }
                }
            }
        }

        void Flush()
        {
            // 丢弃尚未播放的数据（生产者仍在写，最多差一两个块，听感上是一次极短静音）
            int p = prodCount;
            if (consCount != p) consCount = p;
        }

        // ================= 设备 =================

        void Allocate()
        {
            hdrPtrs = new IntPtr[NBUF];
            dataPtrs = new IntPtr[NBUF];
            managedBuf = new byte[NBUF][];
            int byteCount = BLOCK * 2 * 2;
            for (int i = 0; i < NBUF; i++)
            {
                dataPtrs[i] = Marshal.AllocHGlobal(byteCount);
                hdrPtrs[i] = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEHDR)));
                managedBuf[i] = new byte[byteCount];
                WAVEHDR h = new WAVEHDR();
                h.lpData = dataPtrs[i];
                h.dwBufferLength = (uint)byteCount;
                h.dwFlags = 0;
                Marshal.StructureToPtr(h, hdrPtrs[i], false);
            }
            proc = new WaveOutProc(OnWaveDone);
            procPtr = Marshal.GetFunctionPointerForDelegate(proc);
            allocated = true;
        }

        void EnsureDevice()
        {
            if (deviceOpen) return;
            lock (deviceLock)
            {
                if (deviceOpen) return;
                if (!allocated) Allocate();

                WAVEFORMATEX fmt = new WAVEFORMATEX();
                fmt.wFormatTag = 1;
                fmt.nChannels = 2;
                fmt.nSamplesPerSec = (uint)SR;
                fmt.wBitsPerSample = 16;
                fmt.nBlockAlign = (ushort)(fmt.nChannels * fmt.wBitsPerSample / 8);
                fmt.nAvgBytesPerSec = (uint)(fmt.nSamplesPerSec * fmt.nBlockAlign);
                fmt.cbSize = 0;

                int rc = waveOutOpen(out hwo, WAVE_MAPPER, ref fmt, procPtr, IntPtr.Zero, CALLBACK_FUNCTION);
                if (rc != 0)
                {
                    hwo = IntPtr.Zero;
                    throw new Exception("无法打开音频输出设备（错误码 " + rc + "）");
                }
                for (int i = 0; i < NBUF; i++)
                {
                    WAVEHDR h = (WAVEHDR)Marshal.PtrToStructure(hdrPtrs[i], typeof(WAVEHDR));
                    h.dwFlags = 0;
                    Marshal.StructureToPtr(h, hdrPtrs[i], false);
                    waveOutPrepareHeader(hwo, hdrPtrs[i], Marshal.SizeOf(typeof(WAVEHDR)));
                }
                deviceOpen = true;
            }
        }

        /// <summary>清空设备队列（用于定位 / 重新播放），之后需要 Prefill 才能重新出声。</summary>
        void RestartQueue()
        {
            if (!deviceOpen) return;
            lock (deviceLock)
            {
                if (!deviceOpen) return;
                queuePaused = true;
                waveOutReset(hwo);
                WaitCallbackIdle(80);
                for (int i = 0; i < NBUF; i++)
                {
                    WAVEHDR h = (WAVEHDR)Marshal.PtrToStructure(hdrPtrs[i], typeof(WAVEHDR));
                    h.dwFlags = h.dwFlags & ~((uint)WHDR_DONE);
                    h.dwBufferLength = (uint)managedBuf[i].Length;
                    Marshal.StructureToPtr(h, hdrPtrs[i], false);
                }
                queuePaused = false;
            }
        }

        bool QueueRunning()
        {
            if (!deviceOpen) return false;
            for (int i = 0; i < NBUF; i++)
            {
                WAVEHDR h = (WAVEHDR)Marshal.PtrToStructure(hdrPtrs[i], typeof(WAVEHDR));
                if ((h.dwFlags & WHDR_DONE) == 0) return true;
            }
            return false;
        }

        void WaitCallbackIdle(int ms)
        {
            int waited = 0;
            while (Volatile.Read(ref callbackDepth) > 0 && waited < ms)
            {
                Thread.Sleep(1);
                waited++;
            }
        }

        void Prefill()
        {
            if (!deviceOpen) return;
            for (int i = 0; i < NBUF; i++)
                if (!WriteBlock(i)) break;
        }

        bool WriteBlock(int idx)
        {
            if (!deviceOpen) return false;
            int avail = prodCount - consCount;
            if (avail < BLOCK) return false;
            byte[] buf = managedBuf[idx];
            int rd = consCount;
            int o = 0;
            for (int i = 0; i < BLOCK; i++)
            {
                float l = ringL[(rd + i) & RMASK];
                float r = ringR[(rd + i) & RMASK];
                if (l > 1f) l = 1f; if (l < -1f) l = -1f;
                if (r > 1f) r = 1f; if (r < -1f) r = -1f;
                int sl = (int)(l * 32000f);
                int sr = (int)(r * 32000f);
                buf[o++] = (byte)(sl & 0xFF);
                buf[o++] = (byte)((sl >> 8) & 0xFF);
                buf[o++] = (byte)(sr & 0xFF);
                buf[o++] = (byte)((sr >> 8) & 0xFF);
            }
            consCount = rd + BLOCK;

            WAVEHDR h = (WAVEHDR)Marshal.PtrToStructure(hdrPtrs[idx], typeof(WAVEHDR));
            h.dwFlags = h.dwFlags & ~((uint)WHDR_DONE);
            h.dwBufferLength = (uint)buf.Length;
            Marshal.StructureToPtr(h, hdrPtrs[idx], false);
            Marshal.Copy(buf, 0, dataPtrs[idx], buf.Length);
            waveOutWrite(hwo, hdrPtrs[idx], Marshal.SizeOf(typeof(WAVEHDR)));
            return true;
        }

        void OnWaveDone(IntPtr h, uint msg, IntPtr inst, IntPtr p1, IntPtr p2)
        {
            if (msg != WOM_DONE) return;
            if (queuePaused) return;
            Interlocked.Increment(ref callbackDepth);
            try
            {
                if (!deviceOpen || hwo == IntPtr.Zero) return;
                int idx = -1;
                for (int i = 0; i < NBUF; i++) if (hdrPtrs[i] == p1) { idx = i; break; }
                if (idx < 0) return;

                if (WriteBlock(idx)) return;

                // 数据不足：补一块静音，继续驱动设备，避免整体停摆
                underruns++;
                if (underruns > peakUnderrun) peakUnderrun = underruns;
                byte[] buf = managedBuf[idx];
                Array.Clear(buf, 0, buf.Length);
                consCount = prodCount;
                WAVEHDR hh = (WAVEHDR)Marshal.PtrToStructure(hdrPtrs[idx], typeof(WAVEHDR));
                hh.dwFlags = hh.dwFlags & ~((uint)WHDR_DONE);
                hh.dwBufferLength = (uint)buf.Length;
                Marshal.StructureToPtr(hh, hdrPtrs[idx], false);
                Marshal.Copy(buf, 0, dataPtrs[idx], buf.Length);
                waveOutWrite(hwo, hdrPtrs[idx], Marshal.SizeOf(typeof(WAVEHDR)));
            }
            finally { Interlocked.Decrement(ref callbackDepth); }
        }

        void CloseDevice()
        {
            lock (deviceLock)
            {
                if (!deviceOpen || hwo == IntPtr.Zero) { deviceOpen = false; return; }
                queuePaused = true;
                deviceOpen = false;
                waveOutReset(hwo);
                WaitCallbackIdle(120);
                for (int i = 0; i < NBUF; i++)
                    waveOutUnprepareHeader(hwo, hdrPtrs[i], Marshal.SizeOf(typeof(WAVEHDR)));
                waveOutClose(hwo);
                hwo = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            playing = false;
            threadRun = false;
            try { if (renderThread != null) renderThread.Join(300); } catch { }
            CloseDevice();
            if (allocated)
            {
                for (int i = 0; i < NBUF; i++)
                {
                    if (dataPtrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(dataPtrs[i]);
                    if (hdrPtrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(hdrPtrs[i]);
                }
                allocated = false;
            }
        }

        // ---------------- 离线渲染 / 导出 ----------------

        public static void RenderToFloats(Project p, float[] outL, float[] outR, int frames, bool metro, Action<double> progress)
        {
            Renderer r = new Renderer(SR);
            r.Sync(p);
            r.MetroOn = metro;
            double bps = p.Bpm / 60.0;
            int done = 0;
            float[] bl = new float[1024];
            float[] br = new float[1024];
            while (done < frames)
            {
                int n = frames - done;
                if (n > 1024) n = 1024;
                for (int i = 0; i < n; i++) { bl[i] = 0; br[i] = 0; }
                double beat = done / (double)SR * bps;
                r.Render(bl, br, n, beat, bps);
                for (int i = 0; i < n; i++) { outL[done + i] = bl[i]; outR[done + i] = br[i]; }
                done += n;
                if (progress != null && frames > 0) progress(done / (double)frames);
            }
        }

        /// <summary>把当前工程渲染为 16bit 44.1kHz 立体声 WAV。</summary>
        public static void ExportWav(Project p, string path, bool withMetro, Action<double> progress)
        {
            double bps = p.Bpm / 60.0;
            double seconds = p.LengthBeats / bps + 2.5;
            int frames = (int)(seconds * SR);

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                int dataBytes = frames * 4;
                byte[] header = new byte[44];
                WriteAscii(header, 0, "RIFF");
                WriteInt(header, 4, 36 + dataBytes);
                WriteAscii(header, 8, "WAVE");
                WriteAscii(header, 12, "fmt ");
                WriteInt(header, 16, 16);
                WriteShort(header, 20, 1);
                WriteShort(header, 22, 2);
                WriteInt(header, 24, SR);
                WriteInt(header, 28, SR * 4);
                WriteShort(header, 32, 4);
                WriteShort(header, 34, 16);
                WriteAscii(header, 36, "data");
                WriteInt(header, 40, dataBytes);
                fs.Write(header, 0, 44);

                Renderer r = new Renderer(SR);
                r.Sync(p);
                r.MetroOn = withMetro;

                float[] bl = new float[1024];
                float[] br = new float[1024];
                byte[] outBuf = new byte[1024 * 4];
                int done = 0;
                while (done < frames)
                {
                    int n = frames - done;
                    if (n > 1024) n = 1024;
                    for (int i = 0; i < n; i++) { bl[i] = 0; br[i] = 0; }
                    double beat = done / (double)SR * bps;
                    r.Render(bl, br, n, beat, bps);
                    int o = 0;
                    for (int i = 0; i < n; i++)
                    {
                        float l = bl[i], rr = br[i];
                        if (l > 1f) l = 1f; if (l < -1f) l = -1f;
                        if (rr > 1f) rr = 1f; if (rr < -1f) rr = -1f;
                        short sl = (short)(l * 32000f);
                        short sr2 = (short)(rr * 32000f);
                        outBuf[o++] = (byte)(sl & 0xFF);
                        outBuf[o++] = (byte)((sl >> 8) & 0xFF);
                        outBuf[o++] = (byte)(sr2 & 0xFF);
                        outBuf[o++] = (byte)((sr2 >> 8) & 0xFF);
                    }
                    fs.Write(outBuf, 0, n * 4);
                    done += n;
                    if (progress != null) progress(done / (double)frames);
                }
            }
        }

        static void WriteAscii(byte[] b, int off, string s)
        {
            for (int i = 0; i < s.Length; i++) b[off + i] = (byte)s[i];
        }
        static void WriteInt(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF); b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF); b[off + 3] = (byte)((v >> 24) & 0xFF);
        }
        static void WriteShort(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF); b[off + 1] = (byte)((v >> 8) & 0xFF);
        }
    }
}
