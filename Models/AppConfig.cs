using System.Collections.Generic;
using System.Windows.Input;

namespace InteractiveMenu.Models
{
    public class AppConfig
    {
        // --- управление
        public ModifierKeys HotkeyModifiers { get; set; } = ModifierKeys.Alt;
        public Key HotkeyKey { get; set; } = Key.Q;

        // --- вид
        /// <summary>Размытый снимок экрана под меню.</summary>
        public bool BlurBackground { get; set; } = true;

        /// <summary>Насколько затемнять фон, 0..0.9.</summary>
        public double DimOpacity { get; set; } = 0.45;

        /// <summary>Множитель размера шариков и веток.</summary>
        public double BallScale { get; set; } = 1.0;

        // --- обновления
        public bool AutoUpdate { get; set; } = true;

        public MenuNode Root { get; set; }

        public static string HotkeyText(ModifierKeys mods, Key key)
        {
            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(KeyName(key));
            return string.Join(" + ", parts);
        }

        private static string KeyName(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Num" + (int)(key - Key.NumPad0);
            return key switch
            {
                Key.Oem3 => "~",
                Key.Space => "Space",
                Key.Capital => "CapsLock",
                _ => key.ToString()
            };
        }
    }
}
