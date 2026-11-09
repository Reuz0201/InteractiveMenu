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

namespace InteractiveMenu.Views
{
    /// <summary>
    /// Полноэкранное окно с «деревом»: шар в центре, от него ветки к категориям,
    /// при наведении на категорию из неё вырастают новые ветки наружу.
    ///
    /// Про производительность:
    ///  • окно НЕ прозрачное (AllowsTransparency=false): прозрачное окно на весь экран WPF рисует
    ///    через CPU и копирует каждый кадр — отсюда были лаги. Фон — снимок экрана;
    ///  • никаких DropShadowEffect — свечение нарисовано градиентом;
    ///  • каждый шарик кешируется на видеокарте (BitmapCache), анимации двигают готовую текстуру.
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
            public FrameworkElement Element;
            public ScaleTransform Scale;
            public TranslateTransform Translate;
            public Ellipse Glow;
            public TextBlock IconText;
            public TextBlock TitleText;
            public Line Line;
            public bool Removing;
            public TimeSpan PendingDelay;
        }

        private sealed record Placement(MenuNode Node, MenuNode Parent, int Depth, Point Pos, double Angle);

        private static readonly Duration MoveDuration = TimeSpan.FromMilliseconds(300);
        private static readonly Duration FastDuration = TimeSpan.FromMilliseconds(140);
        private static readonly IEasingFunction MoveEase = Freeze(new QuinticEase { EasingMode = EasingMode.EaseOut });
        private static readonly IEasingFunction PopEase = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

        private readonly Dictionary<MenuNode, NodeVisual> _visuals = new Dictionary<MenuNode, NodeVisual>();
        private readonly Dictionary<MenuNode, MenuNode> _parents = new Dictionary<MenuNode, MenuNode>();
        private List<MenuNode> _path = new List<MenuNode>(); // раскрытая цепочка категорий (без корня)

        private AppConfig _cfg;
        private MenuNode _root;
        private MenuNode _hovered;
        private Point _center;
        private DateTime _openedAt;
        private bool _isOpen;
        private bool _clickMode;
        private bool _renderHooked;
        private IntPtr _prevForeground;
        private Action _afterClose;

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

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) CloseMenu();
                else if (e.Key == Key.Back) GoBack();
            };

            MouseRightButtonUp += (s, e) => GoBack();
        }

        private static IEasingFunction Freeze(EasingFunctionBase e)
        {
            e.Freeze();
            return e;
        }

        /// <summary>
        /// Создаём окно и Direct3D-поверхность заранее, за экраном, чтобы первое открытие было мгновенным.
        /// </summary>
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

            _prevForeground = NativeMethods.GetForegroundWindow();
            _cfg = cfg;
            _root = root;
            _path = new List<MenuNode>();
            _hovered = null;
            _clickMode = clickMode;
            _afterClose = null;

            _parents.Clear();
            IndexParents(root);

            LineLayer.Children.Clear();
            NodeLayer.Children.Clear();
            _visuals.Clear();

            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            _center = new Point(Width / 2, Height / 2);

            CaptureBackground();
            UpdateHint();

            ClearAnim(NodeLayer);
            ClearAnim(LineLayer);
            NodeLayer.Opacity = 1;
            LineLayer.Opacity = 1;

            _isOpen = true;
            _openedAt = DateTime.Now;

            if (!IsVisible) Show();
            Activate();

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

        private void CaptureBackground()
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                int pxW = (int)Math.Round(Width * dpi.DpiScaleX);
                int pxH = (int)Math.Round(Height * dpi.DpiScaleY);
                var (sharp, blurred) = ScreenCapture.Capture(pxW, pxH, _cfg.BlurBackground);
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
            Dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, FastDuration));
            ShotBlur.BeginAnimation(OpacityProperty, fade);
        }

        private static void ClearAnim(UIElement el) => el.BeginAnimation(OpacityProperty, null);

        /// <summary>Вызывается, когда отпустили хоткей.</summary>
        public void OnHotkeyReleased()
        {
            if (!_isOpen || _clickMode) return;

            double heldMs = (DateTime.Now - _openedAt).TotalMilliseconds;

            // отпустил над кнопкой — выполняем
            if (_hovered != null && _hovered != _root && !_hovered.IsCategory)
            {
                Choose(_hovered);
                return;
            }

            // быстро тапнул хоткей или отпустил над категорией — меню остаётся, дальше мышкой
            if (heldMs < 350 || (_hovered != null && _hovered.IsCategory))
            {
                _clickMode = true;
                UpdateHint();
                return;
            }

            // долго держал и отпустил в пустоте — отмена
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
            if (_path.Count > 0)
            {
                _path = _path.Take(_path.Count - 1).ToList();
                UpdateTree();
            }
            else if (_clickMode)
            {
                CloseMenu();
            }
        }

        private void Dim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseMenu();

        private void UpdateHint()
        {
            HintText.Text = _clickMode
                ? "ЛКМ — выбрать    ·    ПКМ — назад    ·    Esc — закрыть"
                : "Наведи и отпусти хоткей над кнопкой    ·    отпустить в пустоте — отмена";
        }

        // ================== раскладка дерева ==================

        private void IndexParents(MenuNode node)
        {
            foreach (var child in node.Children)
            {
                _parents[child] = node;
                IndexParents(child);
            }
        }

        private double K => Math.Clamp(_cfg?.BallScale ?? 1.0, 0.6, 1.6);

        private static Vector Polar(double radius, double angleDeg)
        {
            double r = angleDeg * Math.PI / 180;
            return new Vector(Math.Cos(r) * radius, Math.Sin(r) * radius);
        }

        private Point Clamp(Point p) => new Point(
            Math.Clamp(p.X, 70, Math.Max(70, Width - 70)),
            Math.Clamp(p.Y, 60, Math.Max(60, Height - 120)));

        private List<Placement> ComputeLayout()
        {
            var list = new List<Placement> { new Placement(_root, null, 0, _center, -90) };

            // первый уровень — кругом вокруг центра
            int n = _root.Children.Count;
            double r1 = Math.Max(190, n * 30) * K;
            for (int i = 0; i < n; i++)
            {
                double angle = -90 + 360.0 * i / n;
                list.Add(new Placement(_root.Children[i], _root, 1, Clamp(_center + Polar(r1, angle)), angle));
            }

            // раскрытые категории — веером наружу, в направлении ветки
            for (int d = 0; d < _path.Count; d++)
            {
                var cat = _path[d];
                var parentPl = list.FirstOrDefault(p => p.Node == cat);
                if (parentPl == null) break;

                int m = cat.Children.Count;
                if (m == 0) continue;

                double spread = m == 1 ? 0 : Math.Min(130, (m - 1) * 34);
                double dist = (150 + Math.Max(0, m - 4) * 14) * K;
                for (int j = 0; j < m; j++)
                {
                    double a = parentPl.Angle + (m == 1 ? 0 : -spread / 2 + spread * j / (m - 1));
                    list.Add(new Placement(cat.Children[j], cat, d + 2, Clamp(parentPl.Pos + Polar(dist, a)), a));
                }
            }

            return list;
        }

        private void UpdateTree()
        {
            var placements = ComputeLayout();
            var desired = new HashSet<MenuNode>(placements.Select(p => p.Node));

            foreach (var kv in _visuals.ToList())
            {
                if (!desired.Contains(kv.Key))
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
                MoveTo(nv, p.Pos, MoveDuration, nv.PendingDelay);
            }

            UpdateStates();
        }

        // ================== визуал шарика ==================

        private NodeVisual CreateVisual(Placement p)
        {
            var node = p.Node;
            double size = (p.Depth == 0 ? 124 : p.Depth == 1 ? 88 : 70) * K;
            double width = size + 90;
            var color = ColorUtil.Parse(node.Color);

            var ball = new Grid
            {
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top
            };

            // свечение — обычный радиальный градиент, а не эффект
            var glow = new Ellipse
            {
                Width = size * 2,
                Height = size * 2,
                Margin = new Thickness(-size / 2),
                Fill = ColorUtil.GlowBrush(color),
                Opacity = 0.55,
                IsHitTestVisible = false
            };
            ball.Children.Add(glow);

            if (node.IsCategory && p.Depth > 0)
            {
                // у категорий — пунктирное кольцо, чтобы отличать от кнопок
                ball.Children.Add(new Ellipse
                {
                    Width = size + 14,
                    Height = size + 14,
                    Margin = new Thickness(-7),
                    Stroke = new SolidColorBrush(ColorUtil.WithAlpha(ColorUtil.Lighten(color, 0.35), 190)),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 3, 3 },
                    IsHitTestVisible = false
                });
            }

            ball.Children.Add(new Ellipse
            {
                Width = size,
                Height = size,
                Fill = ColorUtil.BallBrush(color),
                Stroke = new SolidColorBrush(ColorUtil.WithAlpha(ColorUtil.Lighten(color, 0.6), 210)),
                StrokeThickness = 1.5
            });

            // блик
            var gloss = new LinearGradientBrush(Color.FromArgb(90, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            gloss.Freeze();
            ball.Children.Add(new Ellipse
            {
                Width = size * 0.56,
                Height = size * 0.34,
                Margin = new Thickness(0, size * 0.08, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Fill = gloss,
                IsHitTestVisible = false
            });

            var icon = new TextBlock
            {
                Text = node.Icon,
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = size * 0.38,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            ball.Children.Add(icon);

            var title = new TextBlock
            {
                Text = node.Title,
                Foreground = Brushes.White,
                FontSize = (p.Depth == 0 ? 15 : 12.5) * Math.Sqrt(K),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var titlePill = new Border
            {
                Child = title,
                Background = new SolidColorBrush(Color.FromArgb(120, 10, 10, 16)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 2, 8, 3),
                MaxWidth = width,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            var container = new Grid
            {
                Width = width,
                Height = size + 40,
                Cursor = Cursors.Hand,
                // шарик рендерится один раз в текстуру; масштаб/сдвиг/прозрачность — силами видеокарты
                CacheMode = new BitmapCache { RenderAtScale = 1.25, SnapsToDevicePixels = false }
            };
            container.Children.Add(ball);
            container.Children.Add(titlePill);

            var scale = new ScaleTransform(0, 0, width / 2, size / 2);
            var translate = new TranslateTransform();
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(translate);
            container.RenderTransform = group;

            container.MouseEnter += (s, e) => OnHover(node);
            container.MouseLeave += (s, e) =>
            {
                if (_hovered == node)
                {
                    _hovered = null;
                    UpdateStates();
                }
            };
            container.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                OnClick(node);
            };

            Line line = null;
            if (p.Parent != null)
            {
                line = new Line
                {
                    Stroke = new SolidColorBrush(ColorUtil.WithAlpha(ColorUtil.Lighten(color, 0.25), 175)),
                    StrokeThickness = (p.Depth == 1 ? 3 : 2) * K,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                LineLayer.Children.Add(line);
            }

            NodeLayer.Children.Add(container);

            return new NodeVisual
            {
                Node = node,
                Parent = p.Parent,
                Depth = p.Depth,
                Size = size,
                Width = width,
                Element = container,
                Scale = scale,
                Translate = translate,
                Glow = glow,
                IconText = icon,
                TitleText = title,
                Line = line
            };
        }

        private void RemoveVisual(NodeVisual nv)
        {
            nv.Removing = true;
            nv.Element.IsHitTestVisible = false;

            if (nv.Parent != null && _visuals.TryGetValue(nv.Parent, out var pv))
                MoveTo(nv, CenterOf(pv), FastDuration, TimeSpan.Zero); // втягивается обратно в родителя

            var fade = new DoubleAnimation(0, FastDuration);
            fade.Completed += (s, e) =>
            {
                NodeLayer.Children.Remove(nv.Element);
                if (nv.Line != null) LineLayer.Children.Remove(nv.Line);
            };
            nv.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, FastDuration));
            nv.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, FastDuration));
            nv.Line?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, FastDuration));
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

        /// <summary>Подсветка наведённого, затемнение невыбранных веток.</summary>
        private void UpdateStates()
        {
            foreach (var nv in _visuals.Values)
            {
                if (nv.Removing) continue;

                bool hovered = nv.Node == _hovered;
                bool onPath = _path.Contains(nv.Node);

                AnimateScale(nv, hovered ? 1.14 : onPath ? 1.05 : 1.0, nv.PendingDelay);
                nv.PendingDelay = TimeSpan.Zero;

                double glow = hovered ? 1.0 : onPath ? 0.8 : 0.5;
                if (Math.Abs(nv.Glow.Opacity - glow) > 0.01)
                    nv.Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(glow, FastDuration));

                double opacity = 1;
                if (nv.Depth >= 1 && _path.Count >= nv.Depth && _path[nv.Depth - 1] != nv.Node)
                    opacity = 0.38;

                if (Math.Abs(nv.Element.Opacity - opacity) > 0.01)
                {
                    nv.Element.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, FastDuration));
                    nv.Line?.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity * 0.9, FastDuration));
                }
            }

            // центральный шар показывает, что сейчас под курсором
            if (_root != null && _visuals.TryGetValue(_root, out var rootVisual))
            {
                var shown = _hovered != null && _hovered != _root ? _hovered : _root;
                if (rootVisual.TitleText.Text != shown.Title) rootVisual.TitleText.Text = shown.Title;
                if (rootVisual.IconText.Text != shown.Icon) rootVisual.IconText.Text = shown.Icon;
            }
        }

        // ================== ввод ==================

        private void OnHover(MenuNode node)
        {
            if (!_isOpen) return;
            _hovered = node;

            if (node == _root)
            {
                // наведение на центр ничего не сворачивает — просто подсветка
            }
            else if (node.IsCategory)
            {
                ExpandTo(node);
            }
            else if (_parents.TryGetValue(node, out var parent))
            {
                ExpandTo(parent); // сворачиваем чужие ветки, оставляем путь до этой кнопки
            }

            UpdateStates();
        }

        private void OnClick(MenuNode node)
        {
            if (!_isOpen) return;

            if (node == _root)
            {
                _path = new List<MenuNode>();
                UpdateTree();
            }
            else if (node.IsCategory)
            {
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

        private void OnRendering(object sender, EventArgs e)
        {
            // ветки всегда тянутся за шариками, даже во время анимации
            foreach (var nv in _visuals.Values)
            {
                if (nv.Line == null || nv.Parent == null) continue;
                if (!_visuals.TryGetValue(nv.Parent, out var pv)) continue;

                var a = CenterOf(pv);
                var b = CenterOf(nv);
                if (nv.Line.X1 != a.X) nv.Line.X1 = a.X;
                if (nv.Line.Y1 != a.Y) nv.Line.Y1 = a.Y;
                if (nv.Line.X2 != b.X) nv.Line.X2 = b.X;
                if (nv.Line.Y2 != b.Y) nv.Line.Y2 = b.Y;
            }
        }
    }
}
