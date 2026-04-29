using System.Windows;

namespace MemoryBooster.Views;

public enum ExitChoice { Cancel, Exit, Minimize }

public partial class ExitConfirmDialog : Window
{
    public ExitChoice Choice { get; private set; } = ExitChoice.Cancel;

    public ExitConfirmDialog()
    {
        InitializeComponent();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.Cancel;
        DialogResult = false;
        Close();
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.Minimize;
        DialogResult = true;
        Close();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.Exit;
        DialogResult = true;
        Close();
    }
}
