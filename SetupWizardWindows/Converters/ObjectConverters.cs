using Avalonia.Data.Converters;
using System.Globalization;
using SetupWizardWindows.ViewModels;

namespace SetupWizardWindows.Converters;

/// <summary>
/// Converter that checks if the bound enum value equals the ConverterParameter.
/// Used for screen visibility switching.
/// </summary>
public class ScreenEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MainViewModel.Screen screen && parameter is string paramStr)
        {
            return screen.ToString() == paramStr;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
