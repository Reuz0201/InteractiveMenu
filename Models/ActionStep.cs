using System;
using System.Text.Json.Serialization;

namespace InteractiveMenu.Models
{
    public enum ActionType
    {
        OpenPath,
        OpenUrl,
        RunCommand,
        RunCommandWindow,
        SendHotkey,
        VolumeMute,
        VolumeUp,
        VolumeDown,
        MediaPlayPause,
        MediaNext,
        MediaPrev,
        Delay,
        LockPC,
        Sleep,
        Shutdown,
        Restart,
        OpenSettings
    }

    /// <summary>Один шаг сценария.</summary>
    public class ActionStep : ObservableObject
    {
        private ActionType _type = ActionType.OpenPath;
        private string _argument = "";
        private string _extraArgs = "";

        public ActionType Type
        {
            get => _type;
            set
            {
                if (Set(ref _type, value))
                {
                    Raise(nameof(NeedsArgument));
                    Raise(nameof(CanBrowse));
                    Raise(nameof(Hint));
                    Raise(nameof(Placeholder));
                    Raise(nameof(Title));
                }
            }
        }

        /// <summary>Путь / ссылка / команда / комбинация / число — зависит от типа.</summary>
        public string Argument
        {
            get => _argument;
            set => Set(ref _argument, value ?? "");
        }

        /// <summary>Аргументы запуска программы (только для OpenPath).</summary>
        public string ExtraArgs
        {
            get => _extraArgs;
            set => Set(ref _extraArgs, value ?? "");
        }

        public static ActionType[] AllTypes => Enum.GetValues<ActionType>();

        [JsonIgnore]
        public bool NeedsArgument => Type is ActionType.OpenPath or ActionType.OpenUrl or ActionType.RunCommand
            or ActionType.RunCommandWindow or ActionType.SendHotkey or ActionType.VolumeUp
            or ActionType.VolumeDown or ActionType.Delay;

        [JsonIgnore]
        public bool CanBrowse => Type == ActionType.OpenPath;

        [JsonIgnore]
        public string Hint => Type switch
        {
            ActionType.OpenPath => "Файл, папка или программа. Например: Z:\\Programms или C:\\Windows\\notepad.exe. Можно %USERPROFILE%.",
            ActionType.OpenUrl => "Ссылка, например https://github.com",
            ActionType.RunCommand => "Команда cmd без окна, например: taskkill /im chrome.exe /f",
            ActionType.RunCommandWindow => "Команда cmd в открытом окне, например: ipconfig",
            ActionType.SendHotkey => "Комбинация клавиш, например: Ctrl+Shift+Esc, Win+D, Alt+F4",
            ActionType.VolumeUp => "На сколько процентов поднять (по умолчанию 10)",
            ActionType.VolumeDown => "На сколько процентов опустить (по умолчанию 10)",
            ActionType.Delay => "Пауза в миллисекундах (1000 = 1 сек)",
            ActionType.VolumeMute => "Включает / выключает звук",
            ActionType.Shutdown => "Выключает ПК сразу, без вопросов",
            ActionType.Restart => "Перезагружает ПК сразу, без вопросов",
            ActionType.OpenSettings => "Открывает окно настроек InteractiveMenu",
            _ => ""
        };

        [JsonIgnore]
        public string Title => DisplayName(Type);

        /// <summary>Серый пример в пустом поле.</summary>
        [JsonIgnore]
        public string Placeholder => Type switch
        {
            ActionType.OpenPath => @"Z:\Programms   или   C:\Games\game.exe",
            ActionType.OpenUrl => "https://youtube.com",
            ActionType.RunCommand => "taskkill /im chrome.exe /f",
            ActionType.RunCommandWindow => "ipconfig",
            ActionType.SendHotkey => "Ctrl+Shift+Esc",
            ActionType.VolumeUp => "10",
            ActionType.VolumeDown => "10",
            ActionType.Delay => "500",
            _ => ""
        };

        public static string DisplayName(ActionType t) => t switch
        {
            ActionType.OpenPath => "📂 Открыть файл / папку / программу",
            ActionType.OpenUrl => "🌐 Открыть ссылку",
            ActionType.RunCommand => "⌨ Команда (скрыто)",
            ActionType.RunCommandWindow => "🖥 Команда (в окне)",
            ActionType.SendHotkey => "🎹 Нажать комбинацию клавиш",
            ActionType.VolumeMute => "🔇 Вкл/выкл звук",
            ActionType.VolumeUp => "🔊 Громче",
            ActionType.VolumeDown => "🔉 Тише",
            ActionType.MediaPlayPause => "⏯ Плей / пауза",
            ActionType.MediaNext => "⏭ Следующий трек",
            ActionType.MediaPrev => "⏮ Предыдущий трек",
            ActionType.Delay => "⏱ Пауза",
            ActionType.LockPC => "🔒 Заблокировать ПК",
            ActionType.Sleep => "💤 Спящий режим",
            ActionType.Shutdown => "🔌 Выключить ПК",
            ActionType.Restart => "🔁 Перезагрузить ПК",
            ActionType.OpenSettings => "⚙ Настройки меню",
            _ => t.ToString()
        };
    }
}
