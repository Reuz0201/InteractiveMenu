using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using PixelFormatWpf = System.Windows.Media.PixelFormats;

namespace InteractiveMenu.Services
{
    /// <summary>
    /// Снимок экрана для фона меню. Окно меню непрозрачное (так WPF рисует его целиком на видеокарте
    /// и анимации идут плавно), а «прозрачность» изображаем снимком того, что было под ним.
    /// </summary>
    public static class ScreenCapture
    {
        public static (BitmapSource Sharp, BitmapSource Blurred) Capture(int pxWidth, int pxHeight, bool blur)
        {
            pxWidth = Math.Max(1, pxWidth);
            pxHeight = Math.Max(1, pxHeight);

            using var full = new Bitmap(pxWidth, pxHeight, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(0, 0, 0, 0, new Size(pxWidth, pxHeight), CopyPixelOperation.SourceCopy);

            var sharp = ToSource(full);
            if (!blur) return (sharp, null);

            // размытие «по-дешёвому»: уменьшаем в 12 раз, слегка размываем маленькую картинку,
            // а WPF растягивает её обратно на весь экран с билинейной фильтрацией
            int sw = Math.Max(1, pxWidth / 12), sh = Math.Max(1, pxHeight / 12);
            using var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(full, new Rectangle(0, 0, sw, sh));
            }

            return (sharp, BlurToSource(small, radius: 2, passes: 3));
        }

        private static BitmapSource ToSource(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormatWpf.Pbgra32, null,
                    data.Scan0, data.Stride * bmp.Height, data.Stride);
                src.Freeze();
                return src;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        /// <summary>Несколько проходов box-blur по маленькой картинке ≈ гауссово размытие.</summary>
        private static BitmapSource BlurToSource(Bitmap bmp, int radius, int passes)
        {
            int w = bmp.Width, h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            int stride = data.Stride;
            var px = new byte[stride * h];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);

            var tmp = new byte[px.Length];
            for (int p = 0; p < passes; p++)
            {
                BoxPass(px, tmp, w, h, stride, radius, horizontal: true);
                BoxPass(tmp, px, w, h, stride, radius, horizontal: false);
            }

            var src = BitmapSource.Create(w, h, 96, 96, PixelFormatWpf.Pbgra32, null, px, stride);
            src.Freeze();
            return src;
        }

        private static void BoxPass(byte[] src, byte[] dst, int w, int h, int stride, int r, bool horizontal)
        {
            int outer = horizontal ? h : w;
            int inner = horizontal ? w : h;
            for (int o = 0; o < outer; o++)
            {
                for (int i = 0; i < inner; i++)
                {
                    int b = 0, g = 0, rr = 0, a = 0, n = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int j = Math.Clamp(i + k, 0, inner - 1);
                        int idx = horizontal ? o * stride + j * 4 : j * stride + o * 4;
                        b += src[idx];
                        g += src[idx + 1];
                        rr += src[idx + 2];
                        a += src[idx + 3];
                        n++;
                    }
                    int di = horizontal ? o * stride + i * 4 : i * stride + o * 4;
                    dst[di] = (byte)(b / n);
                    dst[di + 1] = (byte)(g / n);
                    dst[di + 2] = (byte)(rr / n);
                    dst[di + 3] = (byte)(a / n);
                }
            }
        }
    }
}
