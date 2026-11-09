using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using InteractiveMenu.Models;
using InteractiveMenu.Services;
using InteractiveMenu.Views;
using WF = System.Windows.Forms;

namespace InteractiveMenu
{
    public partial class App : Application
    {
        public static AppConfig Config { get; private set; }

        private Mutex _mutex;
        private KeyboardHook _hook;
        private MenuWindow _menu;
        private SettingsWindow _settings;
        private WF.NotifyIcon _tray;
        private Updater _updater;

        protected override void OnStartup(StartupEventArgs e)
        {
            // WPF по умолчанию ограничивает анимации 60 кадрами — на 120/144/165 Гц это ощущается как лаг.
            // Поднимаем потолок, реальная частота всё равно упрётся в частоту монитора.
            Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline),
                new FrameworkPropertyMetadata { DefaultValue = 240 });

            base.OnStartup(e);

            string updatedTo = ArgValue(e.Args, "--updated");
            string rolledBackFrom = ArgValue(e.Args, "--rollback");

            _mutex = new Mutex(true, "InteractiveMenu_SingleInstance_7F77DD", out bool createdNew);
            if (!createdNew)
            {
                // после обновления старый процесс может ещё пару мгновений завершаться — подождём
                bool got = false;
                if (updatedTo != null || rolledBackFrom != null)
                {
                    try { got = _mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                    catch (AbandonedMutexException) { got = true; }
                }
                if (!got)
                {
                    MessageBox.Show("InteractiveMenu уже запущен — ищи иконку в трее.", "InteractiveMenu");
                    Shutdown();
                    return;
                }
            }

            Log.Info($"Запуск {AppInfo.Version}");
            DispatcherUnhandledException += (s, a) =>
            {
                Log.Error("Необработанная ошибка", a.Exception);
                a.Handled = true;
            };

            Config = ConfigService.Load();

            _menu = new MenuWindow();
            _menu.ActionChosen += OnActionChosen;
            _menu.WarmUp();

            _hook = new KeyboardHook(Dispatcher) { IsMenuOpen = () => _menu.IsOpen };
            _hook.Pressed += OnHotkeyPressed;
            _hook.Released += () => _menu.OnHotkeyReleased();
            _hook.EscapePressed += () => _menu.OnEscape();
            ApplyHotkey();

            ActionRunner.OpenSettingsRequested = () => Dispatcher.BeginInvoke(new Action(ShowSettings));
            ActionRunner.Error += msg => ShowBalloon("Не получилось", msg, WF.ToolTipIcon.Warning);
            ActionRunner.Notice += (title, text) => ShowBalloon(title, text, WF.ToolTipIcon.None);

            CreateTray();

            _updater = new Updater(
                () => Config.AutoUpdate,
                (title, text) => Dispatcher.BeginInvoke(new Action(() => ShowBalloon(title, text, WF.ToolTipIcon.Info))),
                () => Dispatcher.BeginInvoke(new Action(Shutdown)));
            _updater.Start();

            if (rolledBackFrom != null)
            {
                Updater.MarkBadVersion(rolledBackFrom);
                ShowBalloon("Обновление откатилось", $"Версия {rolledBackFrom} не запустилась, вернул {AppInfo.Version}", WF.ToolTipIcon.Warning);
            }
            else if (updatedTo != null)
            {
                // даём себе пару секунд «пожить», потом подтверждаем скрипту обновления, что всё ок
                var confirm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                confirm.Tick += (s, a) =>
                {
                    confirm.Stop();
                    Updater.ConfirmHealthy();
                };
                confirm.Start();
                ShowBalloon($"Обновлено до {AppInfo.Version}", "Что нового — на странице релизов на GitHub", WF.ToolTipIcon.Info);
            }
            else
            {
                ShowBalloon("InteractiveMenu запущен",
                    $"Зажми {AppConfig.HotkeyText(Config.HotkeyModifiers, Config.HotkeyKey)}, чтобы открыть меню",
                    WF.ToolTipIcon.Info);
            }

            if (ConfigService.LastLoadWasNew) ShowSettings();
        }

        private static string ArgValue(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private void ApplyHotkey()
        {
            _hook.HotkeyVk = KeyInterop.VirtualKeyFromKey(Config.HotkeyKey);
            _hook.HotkeyModifiers = Config.HotkeyModifiers;
        }

        private void OnHotkeyPressed()
        {
            // «Пустой» нажим vkE8 (трюк из AutoHotkey):
            //  • отпускание Alt/Win после него не открывает меню-бар окна и «Пуск»;
            //  • Windows считает нас источником последнего ввода и разрешает забрать фокус —
            //    тогда в открытом меню работает поиск с клавиатуры.
            NativeMethods.Tap(0xE8);

            if (_menu.IsOpen) _menu.CloseMenu();
            else _menu.Open(Config.Root, Config);
        }

        private async void OnActionChosen(MenuNode node)
        {
            if (Config.ClickSound) SoundFx.PlayClick();

            await Task.Delay(40); // фокус возвращается в прошлое окно

            // ждём, пока отпустят модификаторы хоткея, чтобы они не «прилипли» к отправляемым комбинациям
            for (int i = 0; i < 40 && NativeMethods.AnyModifierDown(); i++)
                await Task.Delay(25);

            await ActionRunner.RunAsync(node.Steps);
        }

        private void ShowSettings()
        {
            if (_settings != null)
            {
                if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
                _settings.Activate();
                return;
            }

            _settings = new SettingsWindow(
                ConfigService.Clone(Config),
                suspended => _hook.Suspended = suspended,
                cfg => _menu.Open(cfg.Root, cfg, clickMode: true),
                _updater);
            _settings.Saved += cfg =>
            {
                Config = cfg;
                ApplyHotkey();
            };
            _settings.Closed += (s, a) => _settings = null;
            _settings.Show();
            _settings.Activate();
        }

        // ---------- трей ----------

        private void CreateTray()
        {
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Открыть меню", null, (s, a) => _menu.Open(Config.Root, Config, clickMode: true));
            menu.Items.Add("Настройки", null, (s, a) => ShowSettings());
            menu.Items.Add("Проверить обновления", null, (s, a) => _ = _updater.CheckAsync(manual: true));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Выход", null, (s, a) => Shutdown());

            _tray = new WF.NotifyIcon
            {
                Icon = LoadIcon(),
                Text = $"InteractiveMenu {AppInfo.Version}",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, a) => ShowSettings();
        }

        private static System.Drawing.Icon LoadIcon()
        {
            try
            {
                var info = GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"));
                if (info != null)
                {
                    using var stream = info.Stream;
                    return new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Иконка трея: " + ex.Message);
            }
            return System.Drawing.SystemIcons.Application;
        }

        private void ShowBalloon(string title, string text, WF.ToolTipIcon icon)
        {
            if (_tray == null) return;
            if (text.Length > 250) text = text.Substring(0, 250);
            _tray.ShowBalloonTip(3000, title, text, icon);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            _updater?.Dispose();
            _hook?.Dispose();
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
