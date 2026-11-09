using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using InteractiveMenu.Models;

namespace InteractiveMenu.Services
{
    public static class ActionRunner
    {
        public static Action OpenSettingsRequested;
        public static event Action<string> Error;

        /// <summary>Короткое уведомление о результате (например, на какое устройство переключили звук).</summary>
        public static event Action<string, string> Notice;

        public static async Task RunAsync(IEnumerable<ActionStep> steps)
        {
            foreach (var step in steps.ToList())
            {
                try
                {
                    await RunStep(step);
                }
                catch (Exception ex)
                {
                    Error?.Invoke($"{ActionStep.DisplayName(step.Type)}: {ex.Message}");
                }
            }
        }

        private static async Task RunStep(ActionStep s)
        {
            string arg = (s.Argument ?? "").Trim();
            switch (s.Type)
            {
                case ActionType.OpenPath:
                {
                    string path = NormalizePath(arg);
                    var psi = new ProcessStartInfo(path) { UseShellExecute = true };
                    if (!string.IsNullOrWhiteSpace(s.ExtraArgs)) psi.Arguments = s.ExtraArgs;
                    if (File.Exists(path)) psi.WorkingDirectory = Path.GetDirectoryName(path) ?? "";
                    Process.Start(psi);
                    break;
                }
                case ActionType.OpenUrl:
                {
                    string url = arg;
                    if (!url.Contains("://")) url = "https://" + url;
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    break;
                }
                case ActionType.RunCommand:
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c " + arg)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    break;
                case ActionType.RunCommandWindow:
                    Process.Start(new ProcessStartInfo("cmd.exe", "/k " + arg) { UseShellExecute = true });
                    break;
                case ActionType.SendHotkey:
                    HotkeySender.Send(arg);
                    break;
                case ActionType.VolumeMute:
                    NativeMethods.Tap(0xAD, true);
                    break;
                case ActionType.VolumeUp:
                    for (int i = 0; i < Math.Max(1, ParseInt(arg, 10) / 2); i++) NativeMethods.Tap(0xAF, true);
                    break;
                case ActionType.VolumeDown:
                    for (int i = 0; i < Math.Max(1, ParseInt(arg, 10) / 2); i++) NativeMethods.Tap(0xAE, true);
                    break;
                case ActionType.SwitchAudioDevice:
                {
                    string name = await Task.Run(() => AudioDevices.Switch(arg));
                    Notice?.Invoke("🎧 Звук переключён", name);
                    break;
                }
                case ActionType.Brightness:
                {
                    int level = await Task.Run(() => Brightness.Apply(arg));
                    Notice?.Invoke("🔆 Яркость", $"{level}%");
                    break;
                }
                case ActionType.ScreenshotArea:
                    Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
                    break;
                case ActionType.ScreenshotFull:
                {
                    string file = TakeScreenshot();
                    Notice?.Invoke("📷 Скриншот сохранён", file);
                    break;
                }
                case ActionType.MediaPlayPause:
                    NativeMethods.Tap(0xB3, true);
                    break;
                case ActionType.MediaNext:
                    NativeMethods.Tap(0xB0, true);
                    break;
                case ActionType.MediaPrev:
                    NativeMethods.Tap(0xB1, true);
                    break;
                case ActionType.Delay:
                    await Task.Delay(Math.Clamp(ParseInt(arg, 500), 0, 600000));
                    break;
                case ActionType.LockPC:
                    NativeMethods.LockWorkStation();
                    break;
                case ActionType.Sleep:
                    NativeMethods.SetSuspendState(false, false, false);
                    break;
                case ActionType.Shutdown:
                    Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
                    break;
                case ActionType.Restart:
                    Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true });
                    break;
                case ActionType.OpenSettings:
                    OpenSettingsRequested?.Invoke();
                    break;
            }
        }

        /// <summary>Снимок монитора, на котором курсор: PNG в Изображения\\Screenshots + в буфер обмена.</summary>
        private static string TakeScreenshot()
        {
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            var b = screen.Bounds;
            using var bmp = new System.Drawing.Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, System.Drawing.CopyPixelOperation.SourceCopy);

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"InteractiveMenu_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);

            try { System.Windows.Forms.Clipboard.SetImage(bmp); }
            catch (Exception ex) { Log.Warn("Буфер обмена: " + ex.Message); }
            return file;
        }

        private static string NormalizePath(string p)
        {
            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
            // Z:/Programms -> Z:\Programms (shell не любит прямые слэши в локальных путях)
            if (p.Length >= 2 && p[1] == ':') p = p.Replace('/', '\\');
            return p;
        }

        private static int ParseInt(string s, int fallback) =>
            int.TryParse(s, out int v) && v >= 0 ? v : fallback;
    }

    /// <summary>Отправка комбинаций вида "Ctrl+Shift+Esc".</summary>
    public static class HotkeySender
    {
        public static void Send(string combo)
        {
            var vks = new List<byte>();
            foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                byte vk = ToVk(raw);
                if (vk == 0) throw new ArgumentException($"не знаю клавишу «{raw}»");
                vks.Add(vk);
            }
            if (vks.Count == 0) return;

            foreach (var vk in vks) NativeMethods.KeyDown(vk, IsExtended(vk));
            for (int i = vks.Count - 1; i >= 0; i--) NativeMethods.KeyUp(vks[i], IsExtended(vks[i]));
        }

        private static bool IsExtended(byte vk) =>
            vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C;

        private static byte ToVk(string name)
        {
            string n = name.ToLowerInvariant();
            switch (n)
            {
                case "ctrl": case "control": case "ctl": return 0x11;
                case "alt": return 0x12;
                case "shift": return 0x10;
                case "win": case "windows": return 0x5B;
                case "esc": case "escape": return 0x1B;
                case "enter": case "return": return 0x0D;
                case "space": case "пробел": return 0x20;
                case "tab": return 0x09;
                case "del": case "delete": return 0x2E;
                case "ins": case "insert": return 0x2D;
                case "backspace": case "back": return 0x08;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pgup": case "pageup": return 0x21;
                case "pgdn": case "pagedown": return 0x22;
                case "up": return 0x26;
                case "down": return 0x28;
                case "left": return 0x25;
                case "right": return 0x27;
                case "prtsc": case "printscreen": case "print": return 0x2C;
            }

            if (n.Length == 1)
            {
                char c = char.ToUpperInvariant(n[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return (byte)c;
            }

            if (n.Length >= 2 && n[0] == 'f' && int.TryParse(n.Substring(1), out int f) && f >= 1 && f <= 24)
                return (byte)(0x70 + f - 1);

            if (Enum.TryParse(name, true, out Key key))
                return (byte)KeyInterop.VirtualKeyFromKey(key);

            return 0;
        }
    }
}
