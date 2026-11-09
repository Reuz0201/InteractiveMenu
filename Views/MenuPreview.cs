using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InteractiveMenu.Models;
using InteractiveMenu.Services;

namespace InteractiveMenu.Views
{
    /// <summary>
    /// Живое превью меню в окне настроек: та же раскладка и те же шарики, что в настоящем меню,
    /// раскрыта ветка до выбранного шарика. Клик по шарику выбирает его в дереве.
    /// Перерисовка с небольшой задержкой, чтобы не дёргаться на каждую букву.
    /// </summary>
    public sealed class MenuPreview : Border
    {
        private readonly Canvas _canvas = new Canvas();
        private readonly Viewbox _viewbox;
        private readonly DispatcherTimer _debounce;

        private MenuNode _root;
        private MenuNode _selected;
        private AppConfig _cfg;

        public event Action<MenuNode> NodeClicked;

        public MenuPreview()
        {
            ClipToBounds = true;
            _viewbox = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = _canvas };
            Child = _viewbox;
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                Render();
            };
        }

        public void Show(MenuNode root, MenuNode selected, AppConfig cfg)
        {
            _root = root;
            _selected = selected;
            _cfg = cfg;
            RequestRender();
        }

        public void RequestRender()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void Render()
        {
            _canvas.Children.Clear();
            if (_root == null || _cfg == null) return;

            var theme = _cfg.Theme;
            const double k = 1.0;

            // путь до выбранного: раскрываем все категории по дороге (и саму, если это категория)
            var path = new List<MenuNode>();
            var chain = PathTo(_root, _selected) ?? new List<MenuNode>();
            foreach (var n in chain)
                if (n != _root && n.IsCategory) path.Add(n);

            var placements = MenuLayout.Compute(_root, path, new Point(0, 0), k, n => n.Children, null);

            // границы, чтобы вписать всё во Viewbox
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in placements)
            {
                double half = MenuLayout.BallSize(p.Depth, k) / 2 + 60;
                minX = Math.Min(minX, p.Pos.X - half);
                maxX = Math.Max(maxX, p.Pos.X + half);
                minY = Math.Min(minY, p.Pos.Y - half);
                maxY = Math.Max(maxY, p.Pos.Y + half + 30);
            }
            var offset = new Vector(-minX, -minY);
            _canvas.Width = Math.Max(1, maxX - minX);
            _canvas.Height = Math.Max(1, maxY - minY);

            var pos = placements.ToDictionary(p => p.Node, p => p.Pos + offset);
            var angles = placements.ToDictionary(p => p.Node, p => p.Angle);
            bool dimSiblings = path.Count > 0;

            // ветки
            foreach (var p in placements)
            {
                if (p.Parent == null) continue;
                var branch = new Branch(ColorUtil.Parse(p.Node.Color), theme,
                    BallFactory.BranchThickness(p.Depth, k, theme), _cfg.CurvedBranches);
                double startAngle = p.Depth == 1 ? p.Angle : angles[p.Parent];
                branch.Update(pos[p.Parent], pos[p.Node], startAngle, p.Angle);
                branch.Shape.Opacity = IsDimmed(p, path) ? 0.35 : 0.9;
                _canvas.Children.Add(branch.Shape);
            }

            // шарики
            foreach (var p in placements)
            {
                double size = MenuLayout.BallSize(p.Depth, k);
                var parts = BallFactory.Create(p.Node, size, p.Node.IsCategory && p.Depth > 0, theme);
                bool selected = p.Node == _selected;
                if (selected)
                {
                    parts.Glow.Opacity = 1;
                    parts.Ball.Children.Insert(1, new System.Windows.Shapes.Ellipse
                    {
                        Width = size + 26,
                        Height = size + 26,
                        Margin = new Thickness(-13),
                        Stroke = Brushes.White,
                        StrokeThickness = 3,
                        IsHitTestVisible = false
                    });
                }

                var title = new TextBlock
                {
                    Text = p.Node.Title,
                    Foreground = Brushes.White,
                    FontSize = p.Depth == 0 ? 17 : 14,
                    FontWeight = FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = size + 90,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, size + 6, 0, 0)
                };

                var holder = new Grid { Width = size + 100, Cursor = Cursors.Hand, Background = Brushes.Transparent };
                holder.Children.Add(parts.Ball);
                holder.Children.Add(title);
                holder.Opacity = IsDimmed(p, path) ? 0.4 : 1;

                var node = p.Node;
                holder.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    NodeClicked?.Invoke(node);
                };

                Canvas.SetLeft(holder, pos[p.Node].X - (size + 100) / 2);
                Canvas.SetTop(holder, pos[p.Node].Y - size / 2);
                _canvas.Children.Add(holder);
            }
        }

        private static bool IsDimmed(Placement p, List<MenuNode> path) =>
            p.Depth >= 1 && path.Count >= p.Depth && path[p.Depth - 1] != p.Node;

        private static List<MenuNode> PathTo(MenuNode current, MenuNode target)
        {
            if (current == null || target == null) return null;
            if (current == target) return new List<MenuNode> { current };
            foreach (var child in current.Children)
            {
                var sub = PathTo(child, target);
                if (sub != null)
                {
                    sub.Insert(0, current);
                    return sub;
                }
            }
            return null;
        }
    }
}
