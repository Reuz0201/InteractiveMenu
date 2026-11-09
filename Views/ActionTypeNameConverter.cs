using System;
using System.Globalization;
using System.Windows.Data;
using InteractiveMenu.Models;

namespace InteractiveMenu.Views
{
    public class ActionTypeNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is ActionType t ? ActionStep.DisplayName(t) : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Номер шага: AlternationIndex (с нуля) → «1», «2»…</summary>
    public class PlusOneConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int i ? (i + 1).ToString() : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>true → Collapsed, false → Visible.</summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Непустая строка → Visible.</summary>
    public class NonEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.IsNullOrWhiteSpace(value as string) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
