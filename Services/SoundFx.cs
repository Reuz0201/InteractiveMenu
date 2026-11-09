using System;
using System.IO;
using System.Media;

namespace InteractiveMenu.Services
{
    /// <summary>
    /// Тихий «тап» при срабатывании кнопки. Звук синтезируется в памяти (два коротких тона
    /// с мягким затуханием), никаких файлов рядом с exe.
    /// </summary>
    public static class SoundFx
    {
        private static SoundPlayer _player;

        public static void PlayClick()
        {
            try
            {
                _player ??= CreatePlayer();
                _player.Play(); // асинхронно, не блокирует
            }
            catch (Exception ex)
            {
                Log.Warn("Звук: " + ex.Message);
            }
        }

        private static SoundPlayer CreatePlayer()
        {
            var player = new SoundPlayer(new MemoryStream(BuildWav()));
            player.Load();
            return player;
        }

        private static byte[] BuildWav()
        {
            const int rate = 44100;
            const double seconds = 0.11;
            int samples = (int)(rate * seconds);
            var pcm = new short[samples];

            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / rate;
                // лёгкий «поп»: тон скользит 740 → 1180 Гц, второй тон на октаву выше тише
                double f = 740 + 440 * Math.Min(1, t / 0.035);
                double env = Math.Min(1, t / 0.004) * Math.Exp(-t * 38);
                double s = Math.Sin(2 * Math.PI * f * t) * 0.8 + Math.Sin(4 * Math.PI * f * t) * 0.12;
                pcm[i] = (short)(s * env * 0.22 * short.MaxValue);
            }

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            int dataBytes = samples * 2;
            w.Write("RIFF"u8.ToArray());
            w.Write(36 + dataBytes);
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16);            // размер fmt-чанка
            w.Write((short)1);      // PCM
            w.Write((short)1);      // моно
            w.Write(rate);
            w.Write(rate * 2);      // байт в секунду
            w.Write((short)2);      // выравнивание блока
            w.Write((short)16);     // бит на сэмпл
            w.Write("data"u8.ToArray());
            w.Write(dataBytes);
            foreach (var p in pcm) w.Write(p);
            w.Flush();
            return ms.ToArray();
        }
    }
}
