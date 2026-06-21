using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MudAI.App;

/// <summary>Bool to Visibility where true collapses and false shows (for empty-state placeholders).</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
