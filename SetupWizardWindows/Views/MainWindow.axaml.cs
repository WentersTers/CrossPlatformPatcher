using Avalonia.Controls;
using SetupWizardWindows.ViewModels;

namespace SetupWizardWindows.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Set up TopLevelVisual for folder picker access
        Loaded += (sender, args) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.TopLevelVisual = this;
            }
        };
    }
}
