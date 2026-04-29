using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MemoryBooster.Views;

public partial class ToolTipPopup : Window
{
    public ToolTipPopup(string message)
    {
        InitializeComponent();
        TxtMessage.Text = message;
        Width = 180;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
            fadeOut.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fadeOut);
        };
        timer.Start();
    }
}
