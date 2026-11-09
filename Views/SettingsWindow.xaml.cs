using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using InteractiveMenu.Models;
using InteractiveMenu.Services;

namespace InteractiveMenu.Views
{
    public partial class SettingsWindow : Window
    {
        private static readonly string[] Icons =
        {
            "🔊", "🔇", "🎵", "🎧", "⏯", "📁", "📂", "💾", "🚀", "💻", "🖥", "🌐",
            "🎮", "🎬", "📺", "📝", "📷", "💬", "⭐", "❤", "🔥", "⚡", "🔒", "💤",
            "🔌", "🔁", "⚙", "🧰", "🗂", "📋", "✂", "🛒"
        };

        private static readonly string[] Swatches =
        {
            "#7F77DD", "#1D9E75", "#D85A30", "#D4537E", "#378ADD",
            "#639922", "#BA7517", "#E24B4A", "#00B8D9", "#888780"
        };

        private readonly Action<bool> _suspendHook;
        private readonly Action<AppConfig> _preview;
        private readonly Updater _updater;
        private readonly Random _rnd = new Random();

        private AppConfig _cfg;        // то, что редактируем
        private AppConfig _snapshot;   // последнее сохранённое — для «Отменить изменения»
        private ModifierKeys _mods;
        private Key _key;
        private MenuNode _current;
        private bool _loadingKind;
        private bool _loadingUi;
        private bool _dirty;

        /// <summary>Сохранили — App применяет копию конфига.</summary>
        public event Action<AppConfig> Saved;

        public SettingsWindow(AppConfig cfg, Action<bool> suspendHook, Action<AppConfig> preview, Updater updater)
        {
            InitializeComponent();

            _suspendHook = suspendHook;
            _preview = preview;
            _updater = updater;
            _snapshot = ConfigService.Clone(cfg);
            _cfg = cfg;

            VersionText.Text = AppInfo.IsDevBuild ? "локальная сборка" : "v" + AppInfo.Version;
            Title = "InteractiveMenu — настройки";

            BuildIconPanel();
            BuildSwatches();
            BuildStepTypes();
            LoadConfigIntoUi();

            if (_updater != null) _updater.StatusChanged += OnUpdaterStatus;
            RefreshUpdateUi();

            SourceInitialized += (s, e) => NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
            PreviewKeyDown += OnWindowKeyDown;
            Closing += OnClosing;
            Closed += (s, e) =>
            {
                _suspendHook(false);
                if (_updater != null) _updater.StatusChanged -= OnUpdaterStatus;
            };
        }

        // ================== загрузка конфига в интерфейс ==================

        private void LoadConfigIntoUi()
        {
            _loadingUi = true;

            _mods = _cfg.HotkeyModifiers;
            _key = _cfg.HotkeyKey;
            UpdateHotkeyTexts();

            AutostartBox.IsChecked = AutostartService.IsEnabled();
            BlurBox.IsChecked = _cfg.BlurBackground;
            AutoUpdateBox.IsChecked = _cfg.AutoUpdate;
            DimSlider.Value = _cfg.DimOpacity;
            ScaleSlider.Value = _cfg.BallScale;
            DimValue.Text = $"{Math.Round(_cfg.DimOpacity * 100)}%";
            ScaleValue.Text = $"{Math.Round(_cfg.BallScale * 100)}%";

            _cfg.Root.Kind = NodeKind.Category;
            _cfg.Root.IsExpanded = true;
            Track(_cfg.Root);
            Tree.ItemsSource = new ObservableCollection<MenuNode> { _cfg.Root };
            _cfg.Root.IsSelected = true;
            Select(_cfg.Root);

            _loadingUi = false;
            SetDirty(false);
        }

        // ================== «есть несохранённые изменения» ==================

        private void Track(MenuNode node)
        {
            node.PropertyChanged += OnNodeChanged;
            node.Children.CollectionChanged += OnChildrenChanged;
            node.Steps.CollectionChanged += OnStepsChanged;
            foreach (var step in node.Steps) step.PropertyChanged += OnStepChanged;
            foreach (var child in node.Children) Track(child);
        }

        private void OnNodeChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MenuNode.IsSelected):
                case nameof(MenuNode.IsExpanded):
                case nameof(MenuNode.Summary):
                case nameof(MenuNode.ColorBrush):
                case nameof(MenuNode.BallBrush):
                case nameof(MenuNode.IsCategory):
                    return;
            }
            MarkDirty();
        }

        private void OnStepChanged(object sender, PropertyChangedEventArgs e) => MarkDirty();

        private void OnChildrenChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (MenuNode n in e.NewItems) Track(n);
            MarkDirty();
        }

        private void OnStepsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (ActionStep s in e.NewItems) s.PropertyChanged += OnStepChanged;
            UpdateNoSteps();
            MarkDirty();
        }

        private void MarkDirty()
        {
            if (!_loadingUi) SetDirty(true);
        }

        private void SetDirty(bool dirty)
        {
            _dirty = dirty;
            DirtyDot.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            DirtyText.Text = dirty ? "Есть несохранённые изменения" : "Все изменения сохранены";
            DirtyText.Foreground = (Brush)FindResource(dirty ? "TextBrush" : "TextDimBrush");
        }

        // ================== навигация ==================

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PageMenu == null || PageControls == null || PageLook == null || PageUpdates == null) return;
            PageMenu.Visibility = NavMenu.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageControls.Visibility = NavControls.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageLook.Visibility = NavLook.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageUpdates.Visibility = NavUpdates.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================== палитры ==================

        private void BuildIconPanel()
        {
            foreach (var icon in Icons)
            {
                var b = new Button
                {
                    Content = icon,
                    Style = (Style)FindResource("IconButton"),
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    FontSize = 16,
                    Width = 34,
                    Height = 34,
                    Margin = new Thickness(0, 0, 2, 2),
                    Foreground = Brushes.White
                };
                b.Click += (s, e) => { if (_current != null) _current.Icon = icon; };
                IconPanel.Children.Add(b);
            }
        }

        private void BuildSwatches()
        {
            foreach (var hex in Swatches)
            {
                var sw = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Margin = new Thickness(0, 0, 7, 0),
                    Background = ColorUtil.BallBrush(ColorUtil.Parse(hex)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = hex
                };
                sw.MouseLeftButtonUp += (s, e) => { if (_current != null) _current.Color = hex; };
                SwatchPanel.Children.Add(sw);
            }
        }

        private void BuildStepTypes()
        {
            foreach (var type in ActionStep.AllTypes)
            {
                var b = new Button
                {
                    Content = ActionStep.DisplayName(type),
                    Style = (Style)FindResource("GhostButton"),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Foreground = (Brush)FindResource("TextBrush"),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 2, 2)
                };
                b.Click += (s, e) =>
                {
                    AddStepPopup.IsOpen = false;
                    _current?.Steps.Add(new ActionStep { Type = type });
                };
                StepTypeGrid.Children.Add(b);
            }
        }

        // ================== выбор шарика ==================

        private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is MenuNode node) Select(node);
        }

        private void Select(MenuNode node)
        {
            _current = node;
            Editor.DataContext = node;
            Editor.IsEnabled = node != null;
            if (node == null) return;

            bool isRoot = node == _cfg.Root;

            _loadingKind = true;
            RbCategory.IsChecked = node.IsCategory;
            RbButton.IsChecked = !node.IsCategory;
            _loadingKind = false;

            RbCategory.IsEnabled = !isRoot;
            RbButton.IsEnabled = !isRoot;

            UpdateKindPanels();
        }

        private void UpdateKindPanels()
        {
            if (_current == null) return;
            bool isRoot = _current == _cfg.Root;
            bool cat = _current.IsCategory;

            RootInfo.Visibility = isRoot ? Visibility.Visible : Visibility.Collapsed;
            CategoryPanel.Visibility = cat && !isRoot ? Visibility.Visible : Visibility.Collapsed;
            StepsPanel.Visibility = cat ? Visibility.Collapsed : Visibility.Visible;
            UpdateNoSteps();
        }

        private void UpdateNoSteps()
        {
            NoStepsText.Visibility = _current != null && !_current.IsCategory && _current.Steps.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void Kind_Checked(object sender, RoutedEventArgs e)
        {
            if (_loadingKind || _current == null) return;

            if (sender == RbButton)
            {
                if (_current.Children.Count > 0)
                {
                    MessageBox.Show(this, "Внутри этой категории есть шарики. Сначала удали или перенеси их.",
                        "InteractiveMenu", MessageBoxButton.OK, MessageBoxImage.Information);
                    _loadingKind = true;
                    RbCategory.IsChecked = true;
                    _loadingKind = false;
                    return;
                }
                _current.Kind = NodeKind.Button;
            }
            else
            {
                _current.Kind = NodeKind.Category;
            }
            UpdateKindPanels();
        }

        // ================== операции с деревом ==================

        private static MenuNode FindParent(MenuNode root, MenuNode target)
        {
            foreach (var child in root.Children)
            {
                if (child == target) return root;
                var found = FindParent(child, target);
                if (found != null) return found;
            }
            return null;
        }

        private void AddNode(NodeKind kind)
        {
            var sel = _current ?? _cfg.Root;
            MenuNode parent;
            int index;

            if (sel.IsCategory)
            {
                parent = sel;
                index = sel.Children.Count;
            }
            else
            {
                parent = FindParent(_cfg.Root, sel) ?? _cfg.Root;
                index = parent.Children.IndexOf(sel) + 1;
            }

            var node = new MenuNode
            {
                Kind = kind,
                Title = kind == NodeKind.Category ? "Новая категория" : "Новая кнопка",
                Icon = kind == NodeKind.Category ? "📂" : "⭐",
                Color = kind == NodeKind.Category ? Swatches[_rnd.Next(Swatches.Length)] : parent.Color
            };

            parent.Children.Insert(index, node);
            parent.IsExpanded = true;
            node.IsSelected = true;
            Select(node);

            // сразу даём переименовать
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TitleBox.Focus();
                TitleBox.SelectAll();
            }), DispatcherPriority.Input);
        }

        private void AddCategory_Click(object sender, RoutedEventArgs e) => AddNode(NodeKind.Category);

        private void AddButton_Click(object sender, RoutedEventArgs e) => AddNode(NodeKind.Button);

        private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveCurrent(-1);

        private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveCurrent(1);

        private void MoveCurrent(int delta)
        {
            if (_current == null || _current == _cfg.Root) return;
            var parent = FindParent(_cfg.Root, _current);
            if (parent == null) return;
            int i = parent.Children.IndexOf(_current);
            int j = i + delta;
            if (j < 0 || j >= parent.Children.Count) return;
            var node = _current;
            parent.Children.Move(i, j);
            node.IsSelected = true;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _current == _cfg.Root) return;
            var parent = FindParent(_cfg.Root, _current);
            if (parent == null) return;

            if (_current.Children.Count > 0 &&
                MessageBox.Show(this, $"Удалить «{_current.Title}» вместе со всем, что внутри?", "InteractiveMenu",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            parent.Children.Remove(_current);
            parent.IsSelected = true;
            Select(parent);
        }

        // ================== шаги сценария ==================

        private static ActionStep StepOf(object sender) => (sender as FrameworkElement)?.DataContext as ActionStep;

        private void AddStep_Click(object sender, RoutedEventArgs e) => AddStepPopup.IsOpen = !AddStepPopup.IsOpen;

        private void StepUp_Click(object sender, RoutedEventArgs e) => MoveStep(StepOf(sender), -1);

        private void StepDown_Click(object sender, RoutedEventArgs e) => MoveStep(StepOf(sender), 1);

        private void MoveStep(ActionStep step, int delta)
        {
            if (_current == null || step == null) return;
            int i = _current.Steps.IndexOf(step);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _current.Steps.Count) return;
            _current.Steps.Move(i, j);
        }

        private void StepRemove_Click(object sender, RoutedEventArgs e)
        {
            var step = StepOf(sender);
            if (_current != null && step != null) _current.Steps.Remove(step);
        }

        private void BrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var step = StepOf(sender);
            if (step == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Все файлы|*.*|Программы и ярлыки|*.exe;*.lnk;*.bat;*.cmd;*.url" };
            if (dlg.ShowDialog(this) == true) step.Argument = dlg.FileName;
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var step = StepOf(sender);
            if (step == null) return;
            var dlg = new Microsoft.Win32.OpenFolderDialog();
            if (dlg.ShowDialog(this) == true) step.Argument = dlg.FolderName;
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _current.IsCategory) return;
            await ActionRunner.RunAsync(_current.Steps);
        }

        private void Preview_Click(object sender, RoutedEventArgs e) => _preview?.Invoke(_cfg);

        // ================== хоткей ==================

        private void UpdateHotkeyTexts()
        {
            string text = AppConfig.HotkeyText(_mods, _key);
            HotkeyBox.Text = text;
            SidebarHotkeyHint.Text = $"или зажми {text} где угодно";
        }

        private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _suspendHook(true);
            HotkeyBox.Text = "Нажми комбинацию…";
        }

        private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _suspendHook(false);
            UpdateHotkeyTexts();
        }

        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
                return;

            if (key == Key.Escape)
            {
                Keyboard.ClearFocus();
                FocusManager.SetFocusedElement(this, null);
                return;
            }

            var mods = Keyboard.Modifiers;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;

            _mods = mods;
            _key = key;
            HotkeyBox.Text = AppConfig.HotkeyText(_mods, _key);
            MarkDirty();

            bool plainTypingKey = mods == ModifierKeys.None &&
                ((key >= Key.A && key <= Key.Z) || (key >= Key.D0 && key <= Key.D9) || key == Key.Space);
            HotkeyHint.Text = plainTypingKey
                ? "⚠ без модификатора эта клавиша перестанет печататься"
                : "Готово — не забудь сохранить";
            HotkeyHint.Foreground = (Brush)FindResource(plainTypingKey ? "WarnBrush" : "TextDimBrush");
        }

        // ================== вид и прочие настройки ==================

        private void AnySetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_cfg == null || _loadingUi) return;
            _cfg.BlurBackground = BlurBox.IsChecked == true;
            _cfg.AutoUpdate = AutoUpdateBox.IsChecked == true;
            MarkDirty();
        }

        private void DimSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DimValue != null) DimValue.Text = $"{Math.Round(e.NewValue * 100)}%";
            if (_cfg == null || _loadingUi) return;
            _cfg.DimOpacity = Math.Round(e.NewValue, 2);
            MarkDirty();
        }

        private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ScaleValue != null) ScaleValue.Text = $"{Math.Round(e.NewValue * 100)}%";
            if (_cfg == null || _loadingUi) return;
            _cfg.BallScale = Math.Round(e.NewValue, 2);
            MarkDirty();
        }

        private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(ConfigService.Dir);
            Process.Start(new ProcessStartInfo(ConfigService.Dir) { UseShellExecute = true });
        }

        private void ResetMenu_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "Заменить текущее меню примером? Твоя структура пропадёт (если не сохранять — можно отменить).",
                    "InteractiveMenu", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _cfg.Root = ConfigService.CreateDefault().Root;
            _cfg.Root.IsExpanded = true;
            Track(_cfg.Root);
            Tree.ItemsSource = new ObservableCollection<MenuNode> { _cfg.Root };
            _cfg.Root.IsSelected = true;
            Select(_cfg.Root);
            NavMenu.IsChecked = true;
            SetDirty(true);
        }

        // ================== обновления ==================

        private void OnUpdaterStatus() => Dispatcher.BeginInvoke(new Action(RefreshUpdateUi));

        private void RefreshUpdateUi()
        {
            UpdVersion.Text = AppInfo.IsDevBuild ? $"{AppInfo.Version} (локальная сборка)" : AppInfo.Version;
            UpdStatus.Text = _updater?.Status ?? "";
            UpdCheckButton.IsEnabled = _updater != null && !_updater.IsBusy;
        }

        private void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_updater != null) _ = _updater.CheckAsync(manual: true);
        }

        private void OpenReleases_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo(AppInfo.RepoUrl + "/releases") { UseShellExecute = true });

        // ================== сохранение ==================

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                Save();
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e) => Save();

        private bool Save()
        {
            _cfg.HotkeyModifiers = _mods;
            _cfg.HotkeyKey = _key;
            _cfg.BlurBackground = BlurBox.IsChecked == true;
            _cfg.AutoUpdate = AutoUpdateBox.IsChecked == true;

            try
            {
                AutostartService.Set(AutostartBox.IsChecked == true);
                ConfigService.Save(_cfg);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не получилось сохранить: " + ex.Message, "InteractiveMenu",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            _snapshot = ConfigService.Clone(_cfg);
            Saved?.Invoke(ConfigService.Clone(_cfg));
            SetDirty(false);
            DirtyText.Text = "Сохранено ✓";
            return true;
        }

        private void Revert_Click(object sender, RoutedEventArgs e)
        {
            if (!_dirty) return;
            _cfg = ConfigService.Clone(_snapshot);
            LoadConfigIntoUi();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!_dirty) return;
            var answer = MessageBox.Show(this, "Сохранить изменения перед закрытием?", "InteractiveMenu",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) e.Cancel = true;
            else if (answer == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        }
    }
}
