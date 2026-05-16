using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using SetupWizardCore.Models;

namespace SetupWizardWindows.Converters
{
    public class StepToBackgroundConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is InstallerStep currentStep && parameter is string targetStepString)
            {
                if (Enum.TryParse<InstallerStep>(targetStepString, out var targetStep))
                {
                    return currentStep == targetStep ? "#FF007ACC" : "#FF555555";
                }
            }
            return "#FF555555";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class StepToVisibleConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is InstallerStep currentStep && parameter is string targetStepString)
            {
                if (Enum.TryParse<InstallerStep>(targetStepString, out var targetStep))
                {
                    return currentStep == targetStep;
                }
            }
            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class NullToBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value != null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class InverseBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return true;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}