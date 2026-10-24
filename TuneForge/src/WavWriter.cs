using System;
using System.IO;

namespace TuneForge
{
    /// <summary>16-bit 立体声 PCM WAV 写出。</summary>
    static class WavWriter
    {
        public static void Write(string path, float[] L, float[] R, int sampleRate)
        {
            int n = Math.Min(L.Length, R.Length);
            int dataBytes = n * 4; // 2ch * 2byte
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write(new byte[] { 82, 73, 70, 70 });
                bw.Write(36 + dataBytes);
                bw.Write(new byte[] { 87, 65, 86, 69 });
                bw.Write(new byte[] { 102, 109, 116, 32 });
                bw.Write(16);
                bw.Write((short)1);
                bw.Write((short)2);
                bw.Write(sampleRate);
                bw.Write(sampleRate * 4);
                bw.Write((short)4);
                bw.Write((short)16);
                bw.Write(new byte[] { 100, 97, 116, 97 });
                bw.Write(dataBytes);

                byte[] buf = new byte[16384];
                int bi = 0;
                for (int i = 0; i < n; i++)
                {
                    short l = ToShort(L[i]);
                    short r = ToShort(R[i]);
                    buf[bi++] = (byte)l;
                    buf[bi++] = (byte)(l >> 8);
                    buf[bi++] = (byte)r;
                    buf[bi++] = (byte)(r >> 8);
                    if (bi >= buf.Length - 4)
                    {
                        bw.Write(buf, 0, bi);
                        bi = 0;
                    }
                }
                if (bi > 0) bw.Write(buf, 0, bi);
            }
        }

        static short ToShort(float v)
        {
            if (v > 1f) v = 1f;
            if (v < -1f) v = -1f;
            if (float.IsNaN(v)) v = 0;
            return (short)Math.Round(v * 32767f);
        }
    }
}
