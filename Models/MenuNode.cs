using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json.Serialization;
using System.Windows.Media;
using InteractiveMenu.Services;

namespace InteractiveMenu.Models
{
    public enum NodeKind
    {
        Button,
        Category
    }

    /// <summary>
    /// Шарик в меню: либо категория (из неё растут ветки), либо кнопка (выполняет список действий).
    /// </summary>
    public class MenuNode : ObservableObject
    {
        private string _title = "Новая кнопка";
        private string _icon = "⭐";
        private string _color = "#7F77DD";
        private NodeKind _kind = NodeKind.Button;
        private bool _isSelected;
        private bool _isExpanded;
        private string _apps = "";
        private int _dropHint;
        private ObservableCollection<MenuNode> _children;
        private ObservableCollection<ActionStep> _steps;

        public MenuNode()
        {
            Children = new ObservableCollection<MenuNode>();
            Steps = new ObservableCollection<ActionStep>();
        }

        public string Title
        {
            get => _title;
            set => Set(ref _title, value);
        }

        public string Icon
        {
            get => _icon;
            set => Set(ref _icon, value);
        }

        public string Color
        {
            get => _color;
            set
            {
                if (Set(ref _color, value))
                {
                    Raise(nameof(ColorBrush));
                    Raise(nameof(BallBrush));
                }
            }
        }

        public NodeKind Kind
        {
            get => _kind;
            set
            {
                if (Set(ref _kind, value))
                {
                    Raise(nameof(IsCategory));
                    Raise(nameof(Summary));
                }
            }
        }

        /// <summary>
        /// Контекстная ветка: показывать шарик только когда активно одно из этих приложений
        /// (имена процессов через запятую: «chrome, Code»). Пусто — показывать всегда.
        /// </summary>
        public string Apps
        {
            get => _apps;
            set
            {
                if (Set(ref _apps, value ?? "")) Raise(nameof(Summary));
            }
        }

        [JsonIgnore]
        public bool HasApps => !string.IsNullOrWhiteSpace(Apps);

        /// <summary>Подходит ли шарик для активного приложения (процесса).</summary>
        public bool IsVisibleFor(string processName)
        {
            if (!HasApps) return true;
            if (string.IsNullOrEmpty(processName)) return false;
            foreach (var raw in Apps.Split(new[] { ',', ';', '|' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                string app = raw.Trim();
                if (app.EndsWith(".exe", System.StringComparison.OrdinalIgnoreCase)) app = app.Substring(0, app.Length - 4);
                if (app.Equals(processName, System.StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public ObservableCollection<MenuNode> Children
        {
            get => _children;
            set
            {
                if (_children != null) _children.CollectionChanged -= OnContentChanged;
                _children = value ?? new ObservableCollection<MenuNode>();
                _children.CollectionChanged += OnContentChanged;
                Raise(nameof(Children));
                Raise(nameof(Summary));
            }
        }

        public ObservableCollection<ActionStep> Steps
        {
            get => _steps;
            set
            {
                if (_steps != null) _steps.CollectionChanged -= OnContentChanged;
                _steps = value ?? new ObservableCollection<ActionStep>();
                _steps.CollectionChanged += OnContentChanged;
                Raise(nameof(Steps));
                Raise(nameof(Summary));
            }
        }

        private void OnContentChanged(object sender, NotifyCollectionChangedEventArgs e) => Raise(nameof(Summary));

        [JsonIgnore]
        public bool IsCategory => Kind == NodeKind.Category;

        /// <summary>Подпись в дереве настроек: «категория · 3 внутри», «кнопка · 2 действия».</summary>
        [JsonIgnore]
        public string Summary => (IsCategory
            ? (Children.Count == 0 ? "категория · пусто" : $"категория · {Children.Count} внутри")
            : (Steps.Count == 0 ? "кнопка · ничего не делает" : $"кнопка · {Plural(Steps.Count, "действие", "действия", "действий")}"))
            + (HasApps ? $" · 🎯 {Apps}" : "");

        [JsonIgnore]
        public Brush ColorBrush
        {
            get
            {
                var b = new SolidColorBrush(ColorUtil.Parse(Color));
                b.Freeze();
                return b;
            }
        }

        /// <summary>Объёмная заливка шарика для превью в настройках.</summary>
        [JsonIgnore]
        public Brush BallBrush => ColorUtil.BallBrush(ColorUtil.Parse(Color));

        // Только для дерева в настройках
        [JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        [JsonIgnore]
        public bool IsExpanded
        {
            get => _isExpanded;
            set => Set(ref _isExpanded, value);
        }

        /// <summary>Подсказка при перетаскивании в дереве: 0 — нет, 1 — над, 2 — внутрь, 3 — под.</summary>
        [JsonIgnore]
        public int DropHint
        {
            get => _dropHint;
            set => Set(ref _dropHint, value);
        }

        public static string Plural(int n, string one, string few, string many)
        {
            int n10 = n % 10, n100 = n % 100;
            string word = n10 == 1 && n100 != 11 ? one
                : n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14) ? few
                : many;
            return $"{n} {word}";
        }
    }
}
