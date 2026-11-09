using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InteractiveMenu.Services
{
    /// <summary>
    /// Картинки для шариков: иконка программы/ярлыка (крупная, через Shell) или своя картинка.
    /// Всё кешируется, чтобы меню открывалось мгновенно. Вызывать из UI-потока (Shell — STA COM).
    /// </summary>
    public static class IconCache
    {
        private static readonly ConcurrentDictionary<string, ImageSource> Cache =
            new ConcurrentDictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

        /// <summary>Картинка для файла/ярлыка/изображения или null, если не получилось.</summary>
        public static ImageSource Get(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (Cache.TryGetValue(path, out var cached)) return cached;

            ImageSource result = null;
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    string ext = Path.GetExtension(path);
                    result = Array.Exists(ImageExtensions, e => e.Equals(ext, StringComparison.OrdinalIgnoreCase))
                        ? LoadImage(path)
                        : LoadShellIcon(path, 128) ?? LoadAssociatedIcon(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Иконка {path}: {ex.Message}");
            }

            Cache[path] = result; // null тоже кешируем, чтобы не дёргать диск каждый раз
            return result;
        }

        /// <summary>Сбросить кеш для пути (например, пользователь заменил картинку).</summary>
        public static void Forget(string path)
        {
            if (!string.IsNullOrWhiteSpace(path)) Cache.TryRemove(path, out _);
        }

        /// <summary>
        /// Иконка игры Steam по appid из локального кеша Steam (если игра есть в библиотеке).
        /// Старый формат: librarycache\570_icon.jpg, новый: librarycache\570\&lt;sha1&gt;.jpg.
        /// </summary>
        private static readonly ConcurrentDictionary<string, string> SteamIcons = new ConcurrentDictionary<string, string>();

        public static string FindSteamIcon(string appId) => SteamIcons.GetOrAdd(appId, FindSteamIconUncached);

        private static string FindSteamIconUncached(string appId)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                string steam = key?.GetValue("SteamPath") as string;
                if (string.IsNullOrEmpty(steam)) return null;
                string cache = Path.Combine(steam.Replace('/', '\\'), "appcache", "librarycache");

                string old = Path.Combine(cache, appId + "_icon.jpg");
                if (File.Exists(old)) return old;

                string dir = Path.Combine(cache, appId);
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.jpg"))
                    {
                        string name = Path.GetFileNameWithoutExtension(f);
                        if (name.Length == 40 && System.Text.RegularExpressions.Regex.IsMatch(name, "^[0-9a-fA-F]+$")) return f;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Иконка Steam: " + ex.Message);
            }
            return null;
        }

        private static ImageSource LoadImage(string path)
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.DecodePixelWidth = 160;
            img.UriSource = new Uri(path, UriKind.Absolute);
            img.EndInit();
            img.Freeze();
            return img;
        }

        private static ImageSource LoadAssociatedIcon(string path)
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }

        // ------------------------------------------------------------ Shell: крупная иконка с прозрачностью

        private static ImageSource LoadShellIcon(string path, int size)
        {
            IShellItemImageFactory factory = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero)
                    return null;
                return FromHBitmap(hbm);
            }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        /// <summary>
        /// HBITMAP от Shell — 32-битная DIB-секция с альфой. Стандартный CreateBitmapSourceFromHBitmap
        /// теряет прозрачность (чёрный фон), поэтому копируем пиксели сами.
        /// </summary>
        private static BitmapSource FromHBitmap(IntPtr hbm)
        {
            if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out DIBSECTION ds) != 0 &&
                ds.dsBm.bmBitsPixel == 32 && ds.dsBm.bmBits != IntPtr.Zero)
            {
                int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
                BitmapSource src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, ds.dsBm.bmBits, stride * h, stride);
                if (ds.dsBmih.biHeight > 0)
                    src = new TransformedBitmap(src, new ScaleTransform(1, -1)); // DIB «снизу вверх»
                src.Freeze();
                return src;
            }

            var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }

        private const int SIIGBF_BIGGERSIZEOK = 0x1;
        private const int SIIGBF_ICONONLY = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DIBSECTION
        {
            public BITMAP dsBm;
            public BITMAPINFOHEADER dsBmih;
            public uint dsBitfields0;
            public uint dsBitfields1;
            public uint dsBitfields2;
            public IntPtr dshSection;
            public uint dsOffset;
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
        private static extern int GetObject(IntPtr h, int size, out DIBSECTION ds);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr h);
    }
}
