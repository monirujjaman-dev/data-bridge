using Avalonia.Controls;
using DataBridge.Desktop.ViewModels;

namespace DataBridge.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Starts initialization once the window is visible, so the user sees progress instead of a frozen start.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.InitializeCommand.Execute(null);
        }
    }
}
