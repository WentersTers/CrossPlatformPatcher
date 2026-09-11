using Avalonia.Controls;

namespace SetupWizardWindows.Views;

public partial class CompleteView : UserControl
{
    public CompleteView()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Close the application
        if (VisualRoot is Window window)
        {
            window.Close();
        }
    }
}
