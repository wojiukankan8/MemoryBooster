using System.Collections.Generic;
using System.Windows;
using MemoryBooster.Models;

namespace MemoryBooster.Views;

public partial class MultiKillConfirmDialog : Window
{
    public MultiKillConfirmDialog(IList<ProcessInfo> processes)
    {
        InitializeComponent();
        TxtWarn.Text = $"\u5373\u5C06\u5F3A\u5236\u7ED3\u675F \u4EE5\u4E0B {processes.Count} \u4E2A\u8FDB\u7A0B\uFF0C\u64CD\u4F5C\u4E0D\u53EF\u6062\u590D\uFF0C\u53EF\u80FD\u5BFC\u81F4\u6570\u636E\u4E22\u5931\u6216\u7A0B\u5E8F\u6545\u969C\uFF01";
        ProcList.ItemsSource = processes;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
