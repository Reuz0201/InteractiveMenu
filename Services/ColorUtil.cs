using System;
using System.Windows;
using System.Windows.Media;

namespace InteractiveMenu.Services
{
    public static class ColorUtil
    {
        public static Color Parse(string hex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex.Trim()) is Color c)
                    return c;
            }
            catch (FormatException) { }
            return Color.FromRgb(0x7F, 0x77, 0xDD);
        }

        public static Color Lighten(Color c, double k) => Color.FromArgb(c.A,
            (byte)(c.R + (255 - c.R) * k),
            (byte)(c.G + (255 - c.G) * k),
            (byte)(c.B + (255 - c.B) * k));

        public static Color Darken(Color c, double k) => Color.FromArgb(c.A,
            (byte)(c.R * (1 - k)),
            (byte)(c.G * (1 - k)),
            (byte)(c.B * (1 - k)));

        public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        /// <summary>Объёмная заливка шарика: светлый блик сверху-слева, тёмный край.</summary>
        public static Brush BallBrush(Color color)
        {
            var fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.35, 0.28),
                Center = new Point(0.42, 0.38),
                RadiusX = 0.75,
                RadiusY = 0.75
            };
            fill.GradientStops.Add(new GradientStop(Lighten(color, 0.45), 0));
            fill.GradientStops.Add(new GradientStop(color, 0.55));
            fill.GradientStops.Add(new GradientStop(Darken(color, 0.4), 1));
            fill.Freeze();
            return fill;
        }

        /// <summary>Мягкое свечение вокруг шарика (вместо дорогого DropShadowEffect).</summary>
        public static Brush GlowBrush(Color color)
        {
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(WithAlpha(color, 150), 0.0));
            glow.GradientStops.Add(new GradientStop(WithAlpha(color, 70), 0.45));
            glow.GradientStops.Add(new GradientStop(WithAlpha(color, 0), 1.0));
            glow.Freeze();
            return glow;
        }
    }
}
