using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using InteractiveMenu.Models;
using InteractiveMenu.Services;

namespace InteractiveMenu.Views
{
    public sealed record Placement(MenuNode Node, MenuNode Parent, int Depth, Point Pos, double Angle);

    /// <summary>Раскладка дерева: общая для самого меню и превью в настройках.</summary>
    public static class MenuLayout
    {
        public static double BallSize(int depth, double k) => (depth == 0 ? 124 : depth == 1 ? 88 : 70) * k;

        /// <param name="children">какие дети видны (контекстные ветки фильтруются здесь)</param>
        /// <param name="clamp">держать позиции в пределах экрана</param>
        public static List<Placement> Compute(MenuNode root, IReadOnlyList<MenuNode> path, Point center, double k,
            Func<MenuNode, IList<MenuNode>> children, Func<Point, Point> clamp)
        {
            clamp ??= p => p;
            var list = new List<Placement> { new Placement(root, null, 0, center, -90) };

            // первый уровень — кругом вокруг центра
            var first = children(root);
            int n = first.Count;
            double r1 = Math.Max(190, n * 30) * k;
            for (int i = 0; i < n; i++)
            {
                double angle = -90 + 360.0 * i / n;
                list.Add(new Placement(first[i], root, 1, clamp(center + Polar(r1, angle)), angle));
            }

            // раскрытые категории — веером наружу, в направлении ветки
            for (int d = 0; d < path.Count; d++)
            {
                var cat = path[d];
                var parentPl = list.FirstOrDefault(p => p.Node == cat);
                if (parentPl == null) break;

                var kids = children(cat);
                int m = kids.Count;
                if (m == 0) continue;

                double spread = m == 1 ? 0 : Math.Min(130, (m - 1) * 34);
                double dist = (150 + Math.Max(0, m - 4) * 14) * k;
                for (int j = 0; j < m; j++)
                {
                    double a = parentPl.Angle + (m == 1 ? 0 : -spread / 2 + spread * j / (m - 1));
                    list.Add(new Placement(kids[j], cat, d + 2, clamp(parentPl.Pos + Polar(dist, a)), a));
                }
            }

            return list;
        }

        /// <summary>Режим поиска: найденные шарики кольцом вокруг центра.</summary>
        public static List<Placement> ComputeSearch(MenuNode root, IList<MenuNode> matches, Point center, double k, Func<Point, Point> clamp)
        {
            clamp ??= p => p;
            var list = new List<Placement> { new Placement(root, null, 0, center, -90) };
            int n = matches.Count;
            double r = Math.Max(190, n * 30) * k;
            for (int i = 0; i < n; i++)
            {
                double angle = -90 + 360.0 * i / n;
                list.Add(new Placement(matches[i], root, 1, clamp(center + Polar(r, angle)), angle));
            }
            return list;
        }

        public static Vector Polar(double radius, double angleDeg)
        {
            double r = angleDeg * Math.PI / 180;
            return new Vector(Math.Cos(r) * radius, Math.Sin(r) * radius);
        }
    }

    /// <summary>Готовый шарик: сам визуал и части, которые анимируются.</summary>
    public sealed class BallParts
    {
        public Grid Ball;
        public Ellipse Glow;
        public TextBlock Icon;
        public double GlowBase;
    }

    /// <summary>Рисует шарик в выбранной теме. Без эффектов (DropShadow и т.п.) — только градиенты.</summary>
    public static class BallFactory
    {
        public static BallParts Create(MenuNode node, double size, bool showCategoryRing, BallTheme theme)
        {
            var color = ColorUtil.Parse(node.Color);
            var ball = new Grid
            {
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top
            };

            double glowScale = theme == BallTheme.Neon ? 2.4 : 2.0;
            var glow = new Ellipse
            {
                Width = size * glowScale,
                Height = size * glowScale,
                Margin = new Thickness(-size * (glowScale - 1) / 2),
                Fill = theme == BallTheme.Neon ? NeonGlow(color) : ColorUtil.GlowBrush(color),
                IsHitTestVisible = false
            };
            double glowBase = theme switch { BallTheme.Neon => 0.75, BallTheme.Flat => 0.22, _ => 0.5 };
            glow.Opacity = glowBase;
            ball.Children.Add(glow);

            if (showCategoryRing)
            {
                var ringColor = theme == BallTheme.Flat
                    ? Color.FromArgb(110, 255, 255, 255)
                    : ColorUtil.WithAlpha(ColorUtil.Lighten(color, 0.35), (byte)(theme == BallTheme.Neon ? 230 : 190));
                ball.Children.Add(new Ellipse
                {
                    Width = size + 14,
                    Height = size + 14,
                    Margin = new Thickness(-7),
                    Stroke = new SolidColorBrush(ringColor),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 3, 3 },
                    IsHitTestVisible = false
                });
            }

            Brush iconBrush = Brushes.White;
            switch (theme)
            {
                case BallTheme.Neon:
                {
                    var core = new RadialGradientBrush();
                    core.GradientStops.Add(new GradientStop(ColorUtil.Darken(color, 0.8), 0));
                    core.GradientStops.Add(new GradientStop(ColorUtil.Darken(color, 0.55), 1));
                    core.Freeze();
                    ball.Children.Add(new Ellipse
                    {
                        Width = size,
                        Height = size,
                        Fill = core,
                        Stroke = new SolidColorBrush(ColorUtil.Lighten(color, 0.25)),
                        StrokeThickness = Math.Max(2.5, size * 0.04)
                    });
                    iconBrush = new SolidColorBrush(ColorUtil.Lighten(color, 0.6));
                    break;
                }
                case BallTheme.Flat:
                    ball.Children.Add(new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(color) });
                    break;
                default:
                {
                    ball.Children.Add(new Ellipse
                    {
                        Width = size,
                        Height = size,
                        Fill = ColorUtil.BallBrush(color),
                        Stroke = new SolidColorBrush(ColorUtil.WithAlpha(ColorUtil.Lighten(color, 0.6), 210)),
                        StrokeThickness = 1.5
                    });
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
                    break;
                }
            }

            var icon = new TextBlock
            {
                Text = node.Icon,
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = size * 0.38,
                Foreground = iconBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            ball.Children.Add(icon);

            return new BallParts { Ball = ball, Glow = glow, Icon = icon, GlowBase = glowBase };
        }

        public static Brush BranchBrush(Color color, BallTheme theme)
        {
            var c = theme switch
            {
                BallTheme.Neon => ColorUtil.WithAlpha(ColorUtil.Lighten(color, 0.35), 235),
                BallTheme.Flat => Color.FromArgb(80, 255, 255, 255),
                _ => ColorUtil.WithAlpha(ColorUtil.Lighten(color, 0.25), 175)
            };
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static double BranchThickness(int depth, double k, BallTheme theme) =>
            (depth == 1 ? 3 : 2) * k * (theme == BallTheme.Neon ? 1.3 : 1.0);

        private static Brush NeonGlow(Color color)
        {
            var glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(color, 210), 0.0));
            glow.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(color, 90), 0.42));
            glow.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(color, 0), 1.0));
            glow.Freeze();
            return glow;
        }
    }

    /// <summary>Ветка между шариками: прямая или плавная дуга. Обновляется каждый кадр.</summary>
    public sealed class Branch
    {
        private readonly PathFigure _figure;
        private readonly QuadraticBezierSegment _segment;
        private readonly bool _curved;
        private Point _lastA, _lastB;

        public Path Shape { get; }

        public Branch(Brush stroke, double thickness, bool curved)
        {
            _curved = curved;
            _segment = new QuadraticBezierSegment();
            _figure = new PathFigure { IsClosed = false, IsFilled = false };
            _figure.Segments.Add(_segment);
            var geometry = new PathGeometry();
            geometry.Figures.Add(_figure);
            Shape = new Path
            {
                Data = geometry,
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            };
        }

        public void Update(Point a, Point b)
        {
            if (a == _lastA && b == _lastB) return;
            _lastA = a;
            _lastB = b;

            Point control;
            if (_curved)
            {
                // изгиб вбок на 18% длины — ветка выглядит «живой», а не как линейка
                var d = b - a;
                var normal = new Vector(-d.Y, d.X) * 0.18;
                control = a + d * 0.5 + normal;
            }
            else
            {
                control = a + (b - a) * 0.5;
            }

            _figure.StartPoint = a;
            _segment.Point1 = control;
            _segment.Point2 = b;
        }
    }
}
