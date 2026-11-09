using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using InteractiveMenu.Models;
using InteractiveMenu.Services;
using WF = System.Windows.Forms;

namespace InteractiveMenu.Views
{
    /// <summary>
    /// Полноэкранное окно с «деревом»: шар в центре, от него ветки к категориям,
    /// при наведении на категорию из неё вырастают новые ветки наружу.
    ///
    /// Выбор без точного прицеливания: подсвечивается ближайший к курсору шарик,
    /// так что достаточно просто «махнуть» мышкой в его сторону.
    ///
    /// Про производительность:
    ///  • окно НЕ прозрачное — фон это снимок экрана, всё рисует видеокарта;
    ///  • никаких DropShadowEffect — свечение нарисовано градиентом;
    ///  • каждый шарик кешируется в текстуру (BitmapCache).
    /// </summary>
    public partial class MenuWindow : Window
    {
        private sealed class NodeVisual
        {
            public MenuNode Node;
            public MenuNode Parent;
            public int Depth;
            public double Size;
            public double Width;
            public Point Target;
            public double Angle;
            public FrameworkElement Element;
            public ScaleTransform Scale;
            public TranslateTransform Translate;
            public BallParts Parts;
            public TextBlock TitleText;
            public TextBlock SubText;
            public Border Badge;
            public TextBlock BadgeText;
            public Branch Branch;
            public bool Removing;
            public TimeSpan PendingDelay;
        }

        private static readonly Duration MoveDuration = TimeSpan.FromMilliseconds(300);
        private static readonly Duration FastDuration = TimeSpan.FromMilliseconds(140);
        private static readonly IEasingFunction MoveEase = Freeze(new QuinticEase { EasingMode = EasingMode.EaseOut });
        private static readonly IEasingFunction PopEase = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

        private readonly Dictionary<MenuNode, NodeVisual> _visuals = new Dictionary<MenuNode, NodeVisual>();
        private readonly Dictionary<MenuNode, MenuNode> _parents = new Dictionary<MenuNode, MenuNode>();
        private readonly Dictionary<MenuNode, IList<MenuNode>> _visibleKids = new Dictionary<MenuNode, IList<MenuNode>>();
        private List<MenuNode> _path = new List<MenuNode>(); // раскрытая цепочка категорий (без корня)

        private AppConfig _cfg;
        private MenuNode _root;
        private MenuNode _hovered;
        private Point _center;
        private Point _pointer;
        private DateTime _openedAt;
        private bool _isOpen;
        private bool _clickMode;
        private bool _renderHooked;
        private IntPtr _prevForeground;
        private System.Drawing.Point _savedCursor;
        private Action _afterClose;
        private string _ctxApp;
        private bool _hasContextNodes;
        private LiveStatus _live;
        private int _session;

        private string _query = "";
        private List<MenuNode> _matches = new List<MenuNode>();

        public bool IsOpen => _isOpen;

        /// <summary>Выбрана кнопка — App выполняет её действия (вызывается уже после того, как меню скрылось).</summary>
        public event Action<MenuNode> ActionChosen;

        public MenuWindow()
        {
            InitializeComponent();

            SourceInitialized += (s, e) =>
            {
                // прячем окно из Alt+Tab
                var h = new WindowInteropHelper(this).Handle;
                int ex = NativeMethods.GetWindowLong(h, NativeMethods.GWL_EXSTYLE);
                NativeMethods.SetWindowLong(h, NativeMethods.GWL_EXSTYLE, ex | NativeMethods.WS_EX_TOOLWINDOW);
            };

            Deactivated += (s, e) =>
            {
                if (_isOpen && _clickMode) CloseMenu();
            };

            MouseMove += (s, e) => UpdatePointer(e.GetPosition(this));
            MouseLeftButtonUp += (s, e) => OnLeftClick();
            MouseRightButtonUp += (s, e) => GoBack();
            PreviewKeyDown += OnKeyDown;
            TextInput += OnTextInput;
        }

        private static IEasingFunction Freeze(EasingFunctionBase e)
        {
            e.Freeze();
            return e;
        }

        /// <summary>Создаём окно и Direct3D-поверхность заранее, за экраном, чтобы первое открытие было мгновенным.</summary>
        public void WarmUp()
        {
            Left = -32000;
            Top = -32000;
            Width = 200;
            Height = 200;
            ShowActivated = false;
            Show();
            Hide();
            ShowActivated = true;
        }

        // ================== открыть / закрыть ==================

        public void Open(MenuNode root, AppConfig cfg, bool clickMode = false)
        {
            if (root == null || cfg == null) return;

            _session++;
            _prevForeground = NativeMethods.GetForegroundWindow();
            _ctxApp = ForegroundApp.ProcessNameOf(_prevForeground);
            _cfg = cfg;
            _root = root;
            _path = new List<MenuNode>();
            _hovered = null;
            _clickMode = clickMode;
            _afterClose = null;
            _query = "";
            _matches = new List<MenuNode>();
            SearchPill.Visibility = Visibility.Collapsed;

            _parents.Clear();
            _visibleKids.Clear();
            _hasContextNodes = false;
            IndexTree(root);

            LineLayer.Children.Clear();
            NodeLayer.Children.Clear();
            _visuals.Clear();

            // монитор, на котором сейчас курсор
            _savedCursor = WF.Cursor.Position;
            var screen = WF.Screen.FromPoint(_savedCursor);
            var bounds = screen.Bounds;
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = bounds.Left / dpi.DpiScaleX;
            Top = bounds.Top / dpi.DpiScaleY;
            Width = bounds.Width / dpi.DpiScaleX;
            Height = bounds.Height / dpi.DpiScaleY;
            _center = new Point(Width / 2, Height / 2);

            CaptureBackground(bounds);

            // курсор — в центр меню: дальше достаточно «махнуть» в сторону нужного шарика.
            // После закрытия вернём его на место.
            NativeMethods.SetCursorPos(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            _pointer = _center;

            _live = LiveStatus.ReadFast();
            StartSlowLiveRead();

            UpdateHint();

            ClearAnim(NodeLayer);
            ClearAnim(LineLayer);
            NodeLayer.Opacity = 1;
            LineLayer.Opacity = 1;

            _isOpen = true;
            _openedAt = DateTime.Now;

            if (!IsVisible) Show();
            Activate();
            Focus();

            var fadeIn = new Duration(TimeSpan.FromMilliseconds(170));
            ShotBlur.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, fadeIn));
            Dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Math.Clamp(cfg.DimOpacity, 0, 0.9), fadeIn));
            HintPill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(150) });

            if (!_renderHooked)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderHooked = true;
            }

            UpdateTree();
        }

        private void CaptureBackground(System.Drawing.Rectangle bounds)
        {
            try
            {
                var (sharp, blurred) = ScreenCapture.Capture(bounds.Left, bounds.Top, bounds.Width, bounds.Height, _cfg.BlurBackground);
                ShotSharp.Source = sharp;
                ShotBlur.Source = blurred ?? sharp;
            }
            catch (Exception ex)
            {
                // не получилось снять экран (бывает в эксклюзивном полноэкранном режиме игр) — просто тёмный фон
                Log.Warn("Снимок экрана: " + ex.Message);
                ShotSharp.Source = null;
                ShotBlur.Source = null;
            }
        }

        public void CloseMenu() => CloseMenu(null);

        private void CloseMenu(Action after)
        {
            if (!_isOpen) return;
            _isOpen = false;
            _afterClose = after;

            NativeMethods.SetCursorPos(_savedCursor.X, _savedCursor.Y);

            var fade = new DoubleAnimation(0, FastDuration);
            fade.Completed += (s, e) =>
            {
                if (_isOpen) return; // успели открыть заново
                Hide();

                NodeLayer.Children.Clear();
                LineLayer.Children.Clear();
                _visuals.Clear();
                if (_renderHooked)
                {
                    CompositionTarget.Rendering -= OnRendering;
                    _renderHooked = false;
                }

                var then = _afterClose;
                _afterClose = null;
                then?.Invoke();
            };

            NodeLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, FastDuration));
            LineLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, FastDuration));
            HintPill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, FastDuration));
            SearchPill.Visibility = Visibility.Collapsed;
            Dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, FastDuration));
            ShotBlur.BeginAnimation(OpacityProperty, fade);
        }

        private static void ClearAnim(UIElement el) => el.BeginAnimation(OpacityProperty, null);

        /// <summary>Esc: сначала сбрасывает поиск, потом закрывает меню.</summary>
        public void OnEscape()
        {
            if (!_isOpen) return;
            if (_query.Length > 0) SetQuery("");
            else CloseMenu();
        }

        /// <summary>Вызывается, когда отпустили хоткей.</summary>
        public void OnHotkeyReleased()
        {
            if (!_isOpen || _clickMode) return;

            double heldMs = (DateTime.Now - _openedAt).TotalMilliseconds;

            // отпустил, когда выбрана кнопка — выполняем
            if (_hovered != null && _hovered != _root && !_hovered.IsCategory)
            {
                Choose(_hovered);
                return;
            }

            // быстро тапнул хоткей или отпустил над категорией/центром — меню остаётся, дальше мышкой
            if (heldMs < 350 || _hovered != null)
            {
                _clickMode = true;
                UpdateHint();
                return;
            }

            // долго держал и увёл мышку далеко от всех шариков — отмена
            CloseMenu();
        }

        private void Choose(MenuNode node)
        {
            var prev = _prevForeground;
            CloseMenu(() =>
            {
                if (prev != IntPtr.Zero) NativeMethods.SetForegroundWindow(prev);
                ActionChosen?.Invoke(node);
            });
        }

        private void GoBack()
        {
            if (!_isOpen) return;
            if (_query.Length > 0)
            {
                SetQuery("");
            }
            else if (_path.Count > 0)
            {
                _path = _path.Take(_path.Count - 1).ToList();
                UpdateTree();
            }
            else if (_clickMode)
            {
                CloseMenu();
            }
        }

        private void OnLeftClick()
        {
            if (!_isOpen) return;
            if (_hovered == null) CloseMenu();
            else OnClick(_hovered);
        }

        private void UpdateHint()
        {
            string ctx = _hasContextNodes && _ctxApp != null ? $"🎯 {_ctxApp}    ·    " : "";
            HintText.Text = ctx + (_query.Length > 0
                ? "Enter — выполнить    ·    Esc — сбросить поиск"
                : _clickMode
                    ? "ЛКМ — выбрать    ·    ПКМ — назад    ·    печатай для поиска    ·    Esc — закрыть"
                    : "Махни мышкой к кнопке и отпусти хоткей    ·    отпустить вдали от шариков — отмена");
        }

        // ================== дерево и контекст ==================

        private void IndexTree(MenuNode node)
        {
            var kids = node.Children.Where(c => c.IsVisibleFor(_ctxApp)).ToList();
            _visibleKids[node] = kids;
            foreach (var child in kids)
            {
                if (child.HasApps) _hasContextNodes = true;
                _parents[child] = node;
                IndexTree(child);
            }
        }

        private IList<MenuNode> KidsOf(MenuNode node) =>
            _visibleKids.TryGetValue(node, out var kids) ? kids : Array.Empty<MenuNode>();

        private double K => Math.Clamp(_cfg?.BallScale ?? 1.0, 0.6, 1.6);

        private BallTheme Theme => _cfg?.Theme ?? BallTheme.Glass;

        private Point Clamp(Point p) => new Point(
            Math.Clamp(p.X, 70, Math.Max(70, Width - 70)),
            Math.Clamp(p.Y, 60, Math.Max(60, Height - 130)));

        private void UpdateTree()
        {
            var placements = _query.Length > 0
                ? MenuLayout.ComputeSearch(_root, _matches, _center, K, Clamp)
                : MenuLayout.Compute(_root, _path, _center, K, KidsOf, Clamp);

            var desired = placements.ToDictionary(p => p.Node);

            foreach (var kv in _visuals.ToList())
            {
                // шарик пропал или сменил место в дереве (например, при поиске) — пересоздаём
                if (!desired.TryGetValue(kv.Key, out var p) || p.Depth != kv.Value.Depth || p.Parent != kv.Value.Parent)
                {
                    RemoveVisual(kv.Value);
                    _visuals.Remove(kv.Key);
                }
            }

            int born = 0;
            foreach (var p in placements)
            {
                if (!_visuals.TryGetValue(p.Node, out var nv))
                {
                    nv = CreateVisual(p);
                    _visuals[p.Node] = nv;

                    // новый шарик вылетает из родителя, соседи — с маленькой задержкой «волной»
                    Point from = p.Parent != null && _visuals.TryGetValue(p.Parent, out var pv) ? CenterOf(pv) : p.Pos;
                    SetPosition(nv, from);
                    nv.PendingDelay = TimeSpan.FromMilliseconds(p.Depth == 0 ? 0 : Math.Min(born, 10) * 16);
                    born++;
                }
                nv.Target = p.Pos;
                nv.Angle = p.Angle;
                MoveTo(nv, p.Pos, MoveDuration, nv.PendingDelay);
            }

            UpdateStates();
        }

        // ================== визуал шарика ==================

        private NodeVisual CreateVisual(Placement p)
        {
            var node = p.Node;
            double k = K;
            double size = MenuLayout.BallSize(p.Depth, k);
            double width = size + 100;
            var color = ColorUtil.Parse(node.Color);

            var parts = BallFactory.Create(node, size, node.IsCategory && p.Depth > 0, Theme);

            // бейдж живого состояния (громкость, яркость, ▶)
            var badgeText = new TextBlock
            {
                FontSize = Math.Max(10, 11 * Math.Sqrt(k)),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI, Segoe UI Emoji")
            };
            var badge = new Border
            {
                Child = badgeText,
                Background = new SolidColorBrush(Color.FromArgb(235, 16, 16, 24)),
                BorderBrush = new SolidColorBrush(ColorUtil.WithAlpha(ColorUtil.Lighten(color, 0.3), 200)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(6, 1, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -6, -16, 0),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            parts.Ball.Children.Add(badge);

            var title = new TextBlock
            {
                Text = node.Title,
                Foreground = Brushes.White,
                FontSize = (p.Depth == 0 ? 15 : 12.5) * Math.Sqrt(k),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var sub = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                FontSize = 11 * Math.Sqrt(k),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Collapsed
            };
            var titleStack = new StackPanel();
            titleStack.Children.Add(title);
            titleStack.Children.Add(sub);
            var titlePill = new Border
            {
                Child = titleStack,
                Background = new SolidColorBrush(Color.FromArgb(125, 10, 10, 16)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 2, 8, 3),
                MaxWidth = width,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, size + 8, 0, 0)
            };

            var container = new Grid
            {
                Width = width,
                Height = size + 64,
                // шарик рендерится один раз в текстуру; масштаб/сдвиг/прозрачность — силами видеокарты
                CacheMode = new BitmapCache { RenderAtScale = 1.25, SnapsToDevicePixels = false }
            };
            container.Children.Add(parts.Ball);
            container.Children.Add(titlePill);

            var scale = new ScaleTransform(0, 0, width / 2, size / 2);
            var translate = new TranslateTransform();
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(translate);
            container.RenderTransform = group;

            Branch branch = null;
            if (p.Parent != null)
            {
                branch = new Branch(color, Theme, BallFactory.BranchThickness(p.Depth, k, Theme), _cfg.CurvedBranches);
                LineLayer.Children.Add(branch.Shape);
            }

            NodeLayer.Children.Add(container);

            var nv = new NodeVisual
            {
                Node = node,
                Parent = p.Parent,
                Depth = p.Depth,
                Size = size,
                Width = width,
                Target = p.Pos,
                Angle = p.Angle,
                Element = container,
                Scale = scale,
                Translate = translate,
                Parts = parts,
                TitleText = title,
                SubText = sub,
                Badge = badge,
                BadgeText = badgeText,
                Branch = branch
            };
            ApplyLive(nv);
            return nv;
        }

        private void RemoveVisual(NodeVisual nv)
        {
            nv.Removing = true;

            if (nv.Parent != null && _visuals.TryGetValue(nv.Parent, out var pv))
                MoveTo(nv, CenterOf(pv), FastDuration, TimeSpan.Zero); // втягивается обратно в родителя

            var fade = new DoubleAnimation(0, FastDuration);
            fade.Completed += (s, e) =>
            {
                NodeLayer.Children.Remove(nv.Element);
                if (nv.Branch != null) LineLayer.Children.Remove(nv.Branch.Shape);
            };
            nv.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, FastDuration));
            nv.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, FastDuration));
            nv.Branch?.Shape.BeginAnimation(OpacityProperty, new DoubleAnimation(0, FastDuration));
            nv.Element.BeginAnimation(OpacityProperty, fade);
        }

        private static Point CenterOf(NodeVisual v) =>
            new Point(v.Translate.X + v.Width / 2, v.Translate.Y + v.Size / 2);

        private static void SetPosition(NodeVisual v, Point center)
        {
            v.Translate.BeginAnimation(TranslateTransform.XProperty, null);
            v.Translate.BeginAnimation(TranslateTransform.YProperty, null);
            v.Translate.X = center.X - v.Width / 2;
            v.Translate.Y = center.Y - v.Size / 2;
        }

        private static void MoveTo(NodeVisual v, Point center, Duration duration, TimeSpan delay)
        {
            double tx = center.X - v.Width / 2, ty = center.Y - v.Size / 2;
            if (Math.Abs(v.Translate.X - tx) < 0.5 && Math.Abs(v.Translate.Y - ty) < 0.5) return; // уже на месте

            v.Translate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(tx, duration) { EasingFunction = MoveEase, BeginTime = delay });
            v.Translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(ty, duration) { EasingFunction = MoveEase, BeginTime = delay });
        }

        private static void AnimateScale(NodeVisual v, double to, TimeSpan delay)
        {
            if (Math.Abs(v.Scale.ScaleX - to) < 0.001 && delay == TimeSpan.Zero) return;
            var dur = new Duration(TimeSpan.FromMilliseconds(240));
            v.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, dur) { EasingFunction = PopEase, BeginTime = delay });
            v.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, dur) { EasingFunction = PopEase, BeginTime = delay });
        }

        /// <summary>Подсветка выбранного, затемнение невыбранных веток.</summary>
        private void UpdateStates()
        {
            foreach (var nv in _visuals.Values)
            {
                if (nv.Removing) continue;

                bool hovered = nv.Node == _hovered;
                bool onPath = _path.Contains(nv.Node);

                AnimateScale(nv, hovered ? 1.15 : onPath ? 1.05 : 1.0, nv.PendingDelay);
                nv.PendingDelay = TimeSpan.Zero;

                double baseGlow = nv.Parts.GlowBase;
                double glow = hovered ? 1.0 : onPath ? Math.Min(1, baseGlow + 0.25) : baseGlow;
                if (Math.Abs(nv.Parts.Glow.Opacity - glow) > 0.01)
                    nv.Parts.Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(glow, FastDuration));

                double opacity = 1;
                if (_query.Length == 0 && nv.Depth >= 1 && _path.Count >= nv.Depth && _path[nv.Depth - 1] != nv.Node)
                    opacity = 0.38;

                if (Math.Abs(nv.Element.Opacity - opacity) > 0.01)
                {
                    nv.Element.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, FastDuration));
                    nv.Branch?.Shape.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity * 0.9, FastDuration));
                }
            }

            // центральный шар показывает, что сейчас выбрано
            if (_root != null && _visuals.TryGetValue(_root, out var rootVisual))
            {
                var shown = _hovered != null && _hovered != _root ? _hovered : _root;
                if (rootVisual.TitleText.Text != shown.Title) rootVisual.TitleText.Text = shown.Title;
                BallFactory.SetIcon(rootVisual.Parts, shown);
            }
        }

        // ================== живые состояния ==================

        private void StartSlowLiveRead()
        {
            bool needMedia = false, needBrightness = false;
            void Scan(MenuNode n)
            {
                var t = n.IsCategory ? (ActionType?)null : n.Steps.FirstOrDefault()?.Type;
                if (t is ActionType.MediaPlayPause or ActionType.MediaNext or ActionType.MediaPrev) needMedia = true;
                if (t == ActionType.Brightness) needBrightness = true;
                foreach (var c in n.Children) Scan(c);
            }
            Scan(_root);
            if (!needMedia && !needBrightness) return;

            int session = _session;
            var live = _live;
            _ = RefreshLiveAsync(live, needMedia, needBrightness, session);
        }

        private async System.Threading.Tasks.Task RefreshLiveAsync(LiveStatus live, bool media, bool brightness, int session)
        {
            await live.ReadSlowAsync(media, brightness);
            if (session != _session || !_isOpen) return;
            foreach (var nv in _visuals.Values) ApplyLive(nv);
        }

        private void ApplyLive(NodeVisual nv)
        {
            if (_live == null) return;
            var step = nv.Node.IsCategory ? null : nv.Node.Steps.FirstOrDefault();
            string badge = null, sub = null;

            switch (step?.Type)
            {
                case ActionType.VolumeMute:
                    if (_live.Volume != null) badge = _live.Muted ? "🔇 выкл" : $"{_live.Volume}%";
                    break;
                case ActionType.VolumeUp:
                case ActionType.VolumeDown:
                    if (_live.Volume != null) badge = _live.Muted ? "🔇" : $"{_live.Volume}%";
                    break;
                case ActionType.SwitchAudioDevice:
                    sub = _live.OutputDevice;
                    break;
                case ActionType.MediaPlayPause:
                    sub = _live.MediaTitle;
                    if (_live.MediaPlaying != null) badge = _live.MediaPlaying.Value ? "▶" : "❚❚";
                    break;
                case ActionType.MediaNext:
                case ActionType.MediaPrev:
                    sub = _live.MediaTitle;
                    break;
                case ActionType.Brightness:
                    if (_live.BrightnessLevel != null) badge = $"🔆 {_live.BrightnessLevel}%";
                    break;
            }

            nv.BadgeText.Text = badge ?? "";
            nv.Badge.Visibility = badge == null ? Visibility.Collapsed : Visibility.Visible;
            nv.SubText.Text = sub ?? "";
            nv.SubText.Visibility = string.IsNullOrWhiteSpace(sub) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ================== выбор без прицеливания ==================

        /// <summary>
        /// Выбирается шарик, ближайший к курсору (по расстоянию до его края). Небольшой «гистерезис»
        /// не даёт выбору дрожать на границе между соседями. Слишком далеко от всех — ничего не выбрано.
        /// </summary>
        private void UpdatePointer(Point p)
        {
            _pointer = p;
            if (!_isOpen) return;

            NodeVisual best = null;
            double bestD = double.MaxValue;
            foreach (var nv in _visuals.Values)
            {
                if (nv.Removing) continue;
                double d = (nv.Target - p).Length - nv.Size / 2;
                if (d < bestD)
                {
                    bestD = d;
                    best = nv;
                }
            }

            MenuNode pick = best?.Node;
            if (bestD > 230 * K) pick = null;

            if (pick != _hovered && _hovered != null && pick != null && _visuals.TryGetValue(_hovered, out var cur) && !cur.Removing)
            {
                double curD = (cur.Target - p).Length - cur.Size / 2;
                if (curD - bestD < 16) pick = _hovered;
            }

            if (pick == _hovered) return;
            if (pick == null)
            {
                _hovered = null;
                UpdateStates();
            }
            else
            {
                OnHover(pick);
            }
        }

        private void OnHover(MenuNode node)
        {
            if (!_isOpen) return;
            _hovered = node;

            if (_query.Length == 0)
            {
                if (node == _root)
                {
                    // центр ничего не сворачивает — просто подсветка
                }
                else if (node.IsCategory)
                {
                    ExpandTo(node);
                }
                else if (_parents.TryGetValue(node, out var parent))
                {
                    ExpandTo(parent); // сворачиваем чужие ветки, оставляем путь до этой кнопки
                }
            }

            UpdateStates();
        }

        private void OnClick(MenuNode node)
        {
            if (!_isOpen) return;

            if (node == _root)
            {
                if (_query.Length > 0) SetQuery("");
                _path = new List<MenuNode>();
                UpdateTree();
            }
            else if (node.IsCategory)
            {
                if (_query.Length > 0) SetQuery("");
                ExpandTo(node);
            }
            else
            {
                Choose(node);
            }
        }

        private void ExpandTo(MenuNode category)
        {
            var chain = new List<MenuNode>();
            var x = category;
            while (x != null && x != _root)
            {
                chain.Insert(0, x);
                x = _parents.TryGetValue(x, out var parent) ? parent : null;
            }

            if (!chain.SequenceEqual(_path))
            {
                _path = chain;
                UpdateTree();
            }
        }

        // ================== поиск ==================

        private void OnTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!_isOpen || string.IsNullOrEmpty(e.Text)) return;
            var add = new string(e.Text.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '.').ToArray());
            if (add.Length == 0) return;
            if (_query.Length == 0 && add.Trim().Length == 0) return;
            e.Handled = true;
            SetQuery(_query + add);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isOpen) return;
            switch (e.Key)
            {
                case Key.Escape:
                    e.Handled = true;
                    OnEscape();
                    break;
                case Key.Back:
                    e.Handled = true;
                    if (_query.Length > 0) SetQuery(_query.Substring(0, _query.Length - 1));
                    else GoBack();
                    break;
                case Key.Enter:
                    e.Handled = true;
                    var target = _hovered != null && _hovered != _root ? _hovered : _matches.FirstOrDefault();
                    if (target != null) OnClick(target);
                    break;
            }
        }

        private void SetQuery(string query)
        {
            _query = query ?? "";
            _path = new List<MenuNode>();

            if (_query.Length == 0)
            {
                _matches = new List<MenuNode>();
                SearchPill.Visibility = Visibility.Collapsed;
                _hovered = null;
            }
            else
            {
                _clickMode = true; // начал печатать — значит управляешь с клавиатуры/мышки, а не хоткеем
                _matches = FindMatches(_query);
                SearchPill.Visibility = Visibility.Visible;
                SearchText.Text = _query;
                SearchCount.Text = _matches.Count == 0 ? "ничего не нашёл" : MenuNode.Plural(_matches.Count, "кнопка", "кнопки", "кнопок");
                _hovered = _matches.FirstOrDefault();
            }

            UpdateHint();
            UpdateTree();
        }

        private List<MenuNode> FindMatches(string query)
        {
            string q = query.Trim();
            var all = new List<MenuNode>();
            void Walk(MenuNode n)
            {
                foreach (var c in KidsOf(n))
                {
                    if (!c.IsCategory) all.Add(c);
                    Walk(c);
                }
            }
            Walk(_root);

            return all
                .Select(n => (n, score: Score(n.Title ?? "", q)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Take(10)
                .Select(x => x.n)
                .ToList();
        }

        /// <summary>Начало названия &gt; начало слова &gt; где-то внутри.</summary>
        private static int Score(string title, string q)
        {
            if (q.Length == 0) return 0;
            if (title.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 3;
            int i = title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase);
            if (i > 0 && !char.IsLetterOrDigit(title[i - 1])) return 2;
            return i >= 0 ? 1 : 0;
        }

        // ================== кадр ==================

        private void OnRendering(object sender, EventArgs e)
        {
            // ветки всегда тянутся за шариками, даже во время анимации
            foreach (var nv in _visuals.Values)
            {
                if (nv.Branch == null || nv.Parent == null) continue;
                if (!_visuals.TryGetValue(nv.Parent, out var pv)) continue;
                // у центра ветка идёт по радиусу, дальше — продолжает направление ветки родителя
                double startAngle = pv.Depth == 0 ? nv.Angle : pv.Angle;
                nv.Branch.Update(CenterOf(pv), CenterOf(nv), startAngle, nv.Angle);
            }
        }
    }
}
