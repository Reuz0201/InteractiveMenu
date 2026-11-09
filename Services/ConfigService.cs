using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using InteractiveMenu.Models;

namespace InteractiveMenu.Services
{
    public static class ConfigService
    {
        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InteractiveMenu");

        public static string FilePath => Path.Combine(Dir, "config.json");

        /// <summary>true, если при последней загрузке конфига ещё не было и создан пример.</summary>
        public static bool LastLoadWasNew { get; private set; }

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };

        public static AppConfig Load()
        {
            LastLoadWasNew = false;
            try
            {
                if (File.Exists(FilePath))
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Options);
                    if (cfg?.Root != null)
                    {
                        cfg.Root.Kind = NodeKind.Category;
                        return cfg;
                    }
                }
            }
            catch (Exception)
            {
                // битый конфиг — откладываем в сторону, чтобы не потерять
                try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
            }

            LastLoadWasNew = true;
            var def = CreateDefault();
            Save(def);
            return def;
        }

        public static void Save(AppConfig cfg)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, Options));
        }

        public static void Export(AppConfig cfg, string path) =>
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, Options));

        /// <summary>Читает экспортированный конфиг (или просто дерево меню).</summary>
        public static AppConfig Import(string path)
        {
            string json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (cfg?.Root == null)
            {
                var root = JsonSerializer.Deserialize<MenuNode>(json, Options);
                if (root == null || root.Children.Count == 0)
                    throw new InvalidDataException("в файле нет меню InteractiveMenu");
                cfg = new AppConfig { Root = root };
            }
            cfg.Root.Kind = NodeKind.Category;
            return cfg;
        }

        public static AppConfig Clone(AppConfig cfg) =>
            JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg, Options), Options);

        // ---------- пример меню ----------

        private static MenuNode Cat(string icon, string title, string color, params MenuNode[] children)
        {
            var n = new MenuNode { Icon = icon, Title = title, Color = color, Kind = NodeKind.Category };
            foreach (var c in children) n.Children.Add(c);
            return n;
        }

        private static MenuNode Btn(string icon, string title, string color, params ActionStep[] steps)
        {
            var n = new MenuNode { Icon = icon, Title = title, Color = color, Kind = NodeKind.Button };
            foreach (var s in steps) n.Steps.Add(s);
            return n;
        }

        private static MenuNode Ctx(MenuNode node, string apps)
        {
            node.Apps = apps;
            return node;
        }

        private static ActionStep S(ActionType type, string arg = "", string extra = "") =>
            new ActionStep { Type = type, Argument = arg, ExtraArgs = extra };

        public static AppConfig CreateDefault()
        {
            const string teal = "#1D9E75", purple = "#7F77DD", coral = "#D85A30",
                blue = "#378ADD", amber = "#BA7517", pink = "#D4537E", gray = "#888780", red = "#E24B4A";

            var root = Cat("✦", "Меню", purple,
                Cat("🔊", "Звук", teal,
                    Btn("🔇", "Без звука", teal, S(ActionType.VolumeMute)),
                    Btn("🔊", "Громче", teal, S(ActionType.VolumeUp, "10")),
                    Btn("🔉", "Тише", teal, S(ActionType.VolumeDown, "10")),
                    Btn("🎧", "Устройство", teal, S(ActionType.SwitchAudioDevice)),
                    Cat("🎵", "Музыка", teal,
                        Btn("⏮", "Назад", teal, S(ActionType.MediaPrev)),
                        Btn("⏯", "Пауза", teal, S(ActionType.MediaPlayPause)),
                        Btn("⏭", "Дальше", teal, S(ActionType.MediaNext)))),
                Cat("📁", "Папки", amber,
                    Btn("💾", "Programms", amber, S(ActionType.OpenPath, @"Z:\Programms")),
                    Btn("💻", "MyCode", amber, S(ActionType.OpenPath, @"Z:\MyCode")),
                    Btn("📥", "Загрузки", amber, S(ActionType.OpenPath, @"%USERPROFILE%\Downloads"))),
                Cat("🚀", "Приложения", blue,
                    Btn("🗂", "Проводник", blue, S(ActionType.OpenPath, "explorer.exe")),
                    Btn("📝", "Блокнот", blue, S(ActionType.OpenPath, "notepad.exe")),
                    Btn("📊", "Диспетчер", blue, S(ActionType.SendHotkey, "Ctrl+Shift+Esc")),
                    Btn("🧮", "Калькулятор", blue, S(ActionType.OpenPath, "calc.exe"))),
                Cat("🎬", "Сценарии", pink,
                    Btn("💼", "Рабочий режим", pink,
                        S(ActionType.OpenPath, @"Z:\MyCode"),
                        S(ActionType.Delay, "400"),
                        S(ActionType.OpenUrl, "https://github.com"),
                        S(ActionType.VolumeDown, "20")),
                    Btn("🖥", "Свернуть всё", pink, S(ActionType.SendHotkey, "Win+D"))),
                Cat("⚡", "Система", coral,
                    Btn("🔒", "Блокировка", coral, S(ActionType.LockPC)),
                    Btn("💤", "Сон", coral, S(ActionType.Sleep)),
                    Btn("🔆", "Ярче", coral, S(ActionType.Brightness, "+15")),
                    Btn("🔅", "Темнее", coral, S(ActionType.Brightness, "-15")),
                    Btn("✂", "Скриншот", coral, S(ActionType.ScreenshotArea)),
                    Cat("🔌", "Питание", red,
                        Btn("🔁", "Перезагрузка", red, S(ActionType.Restart)),
                        Btn("⛔", "Выключить", red, S(ActionType.Shutdown)))),
                Ctx(Cat("🌐", "Браузер", "#00B8D9",
                    Btn("➕", "Новая вкладка", "#00B8D9", S(ActionType.SendHotkey, "Ctrl+T")),
                    Btn("↩", "Вернуть вкладку", "#00B8D9", S(ActionType.SendHotkey, "Ctrl+Shift+T")),
                    Btn("✖", "Закрыть вкладку", "#00B8D9", S(ActionType.SendHotkey, "Ctrl+W"))),
                    "chrome, msedge, firefox, opera, browser, yandex"),
                Btn("⚙", "Настройки", gray, S(ActionType.OpenSettings)));

            return new AppConfig { Root = root };
        }
    }
}
