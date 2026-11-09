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

                // шаг между детьми и расстояние подбираем так, чтобы соседние шарики
                // вместе с подписями не налезали друг на друга
                int depth = d + 2;
                double step = m == 1 ? 0 : Math.Min(40, 140.0 / (m - 1));
                double spread = step * (m - 1);
                double need = BallSize(depth, k) + 62 * k;
                double dist = m == 1
                    ? 160 * k
                    : Math.Max(160 * k, need / (2 * Math.Sin(step * Math.PI / 360)));
                dist = Math.Min(dist, 320 * k);

                for (int j = 0; j < m; j++)
                {
                    double a = parentPl.Angle - spread / 2 + step * j;
                    list.Add(new Placement(kids[j], cat, depth, clamp(parentPl.Pos + Polar(dist, a)), a));
                }
            }

            return Relax(list, k, clamp);
        }

        /// <summary>
        /// Если шарик (вместе с подписью) всё равно наезжает на соседа — например, у края экрана
        /// или рядом с подписью родителя — отодвигаем его наружу вдоль своей ветки вместе с его детьми.
        /// </summary>
        private static List<Placement> Relax(List<Placement> list, double k, Func<Point, Point> clamp)
        {
            int count = list.Count;
            var pos = list.Select(p => p.Pos).ToArray();
            var index = new Dictionary<MenuNode, int>();
            for (int i = 0; i < count; i++) index[list[i].Node] = i;

            bool IsUnder(int child, int ancestor)
            {
                var parent = list[child].Parent;
                while (parent != null && index.TryGetValue(parent, out int pi))
                {
                    if (pi == ancestor) return true;
                    parent = list[pi].Parent;
                }
                return false;
            }

            Rect Footprint(int i)
            {
                var pl = list[i];
                double s = BallSize(pl.Depth, k);
                double label = Math.Min(s + 100 * k, (pl.Node.Title?.Length ?? 0) * 7.4 * k + 16);
                double w = Math.Max(s, label) + 14 * k;
                return new Rect(pos[i].X - w / 2, pos[i].Y - s / 2 - 6 * k, w, s + 42 * k);
            }

            for (int iter = 0; iter < 24; iter++)
            {
                bool moved = false;
                for (int b = 0; b < count; b++)
                {
                    if (list[b].Depth < 2) continue;
                    for (int a = 0; a < count; a++)
                    {
                        if (a == b) continue;
                        double ov = Overlap(Footprint(a), Footprint(b));
                        if (ov <= 0) continue;

                        var target = clamp(pos[b] + Polar(ov * 0.6 + 1, list[b].Angle));
                        var delta = target - pos[b];
                        pos[b] = target;
                        for (int c = 0; c < count; c++)
                            if (IsUnder(c, b)) pos[c] = clamp(pos[c] + delta);
                        moved = true;
                    }
                }
                if (!moved) break;
            }

            var result = new List<Placement>(count);
            for (int i = 0; i < count; i++) result.Add(list[i] with { Pos = pos[i] });
            return result;
        }

        private static double Overlap(Rect a, Rect b)
        {
            double dx = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
            double dy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
            return dx > 0 && dy > 0 ? Math.Min(dx, dy) : 0;
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
        public Image Picture;
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

            // иконка программы / своя картинка — вместо эмодзи
            var picture = new Image
            {
                Width = size * 0.54,
                Height = size * 0.54,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            ball.Children.Add(picture);

            var parts = new BallParts { Ball = ball, Glow = glow, Icon = icon, Picture = picture, GlowBase = glowBase };
            SetIcon(parts, node);
            return parts;
        }

        /// <summary>Показать на шарике картинку узла (если есть) или его эмодзи.</summary>
        public static void SetIcon(BallParts parts, MenuNode node)
        {
            var img = node.IconSource;
            if (img != null)
            {
                parts.Picture.Source = img;
                parts.Picture.Visibility = Visibility.Visible;
                parts.Icon.Visibility = Visibility.Collapsed;
            }
            else
            {
                parts.Picture.Source = null;
                parts.Picture.Visibility = Visibility.Collapsed;
                parts.Icon.Text = node.Icon;
                parts.Icon.Visibility = Visibility.Visible;
            }
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

    /// <summary>
    /// Ветка между шариками. Рисуется залитым контуром, который сужается от родителя к ребёнку.
    /// Плавный режим — кубическая кривая: ветка выходит из родителя по направлению его собственной
    /// ветки и мягко поворачивает к ребёнку, как развилка у дерева. Прямой режим — сужающийся отрезок.
    /// Обновляется каждый кадр, пока шарики летят.
    /// </summary>
    public sealed class Branch
    {
        private const int Samples = 20;

        private readonly bool _curved;
        private readonly double _w0, _w1;
        private readonly LinearGradientBrush _brush;
        private readonly Point[] _left = new Point[Samples + 1];
        private readonly Point[] _right = new Point[Samples + 1];
        private Point _lastA, _lastB;
        private double _lastSa = double.NaN, _lastEa = double.NaN;

        public Path Shape { get; }

        /// <param name="fromColor">цвет родителя — ветка начинается его цветом</param>
        /// <param name="toColor">цвет ребёнка — и плавно переходит в него</param>
        public Branch(Color fromColor, Color toColor, BallTheme theme, double thickness, bool curved)
        {
            _curved = curved;
            _w0 = thickness * 2.6;   // у родителя
            _w1 = thickness * 1.0;   // у ребёнка

            Color c0, c1;
            switch (theme)
            {
                case BallTheme.Neon:
                    c0 = ColorUtil.WithAlpha(ColorUtil.Lighten(fromColor, 0.35), 245);
                    c1 = ColorUtil.WithAlpha(ColorUtil.Lighten(toColor, 0.25), 225);
                    break;
                case BallTheme.Flat:
                    c0 = Color.FromArgb(95, 255, 255, 255);
                    c1 = Color.FromArgb(55, 255, 255, 255);
                    break;
                default:
                    c0 = ColorUtil.WithAlpha(ColorUtil.Lighten(fromColor, 0.3), 215);
                    c1 = ColorUtil.WithAlpha(ColorUtil.Lighten(toColor, 0.15), 170);
                    break;
            }

            _brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute };
            _brush.GradientStops.Add(new GradientStop(c0, 0));
            _brush.GradientStops.Add(new GradientStop(c1, 1));

            Shape = new Path { Fill = _brush, IsHitTestVisible = false };
        }

        /// <param name="startAngle">куда «смотрит» ветка у родителя (градусы)</param>
        /// <param name="endAngle">куда «смотрит» ветка у ребёнка (градусы)</param>
        public void Update(Point a, Point b, double startAngle, double endAngle)
        {
            if (a == _lastA && b == _lastB && startAngle == _lastSa && endAngle == _lastEa) return;
            _lastA = a;
            _lastB = b;
            _lastSa = startAngle;
            _lastEa = endAngle;

            var ab = b - a;
            double len = ab.Length;
            if (len < 1)
            {
                Shape.Data = null;
                return;
            }

            Point p1, p2;
            if (_curved)
            {
                p1 = a + MenuLayout.Polar(len * 0.45, startAngle);
                p2 = b - MenuLayout.Polar(len * 0.35, endAngle);
            }
            else
            {
                p1 = a + ab / 3;
                p2 = a + ab * 2 / 3;
            }

            for (int i = 0; i <= Samples; i++)
            {
                double t = (double)i / Samples;
                var p = Bezier(a, p1, p2, b, t);
                var tangent = BezierTangent(a, p1, p2, b, t);
                if (tangent.Length < 1e-6) tangent = ab;
                tangent.Normalize();
                var normal = new Vector(-tangent.Y, tangent.X);
                double half = (_w0 + (_w1 - _w0) * t) / 2;
                _left[i] = p + normal * half;
                _right[i] = p - normal * half;
            }

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(_left[0], true, true);
                for (int i = 1; i <= Samples; i++) ctx.LineTo(_left[i], false, false);
                for (int i = Samples; i >= 0; i--) ctx.LineTo(_right[i], false, false);
            }
            geometry.Freeze();
            Shape.Data = geometry;

            _brush.StartPoint = a;
            _brush.EndPoint = b;
        }

        private static Point Bezier(Point p0, Point p1, Point p2, Point p3, double t)
        {
            double u = 1 - t;
            double b0 = u * u * u, b1 = 3 * u * u * t, b2 = 3 * u * t * t, b3 = t * t * t;
            return new Point(
                b0 * p0.X + b1 * p1.X + b2 * p2.X + b3 * p3.X,
                b0 * p0.Y + b1 * p1.Y + b2 * p2.Y + b3 * p3.Y);
        }

        private static Vector BezierTangent(Point p0, Point p1, Point p2, Point p3, double t)
        {
            double u = 1 - t;
            return 3 * u * u * (p1 - p0) + 6 * u * t * (p2 - p1) + 3 * t * t * (p3 - p2);
        }
    }
}
