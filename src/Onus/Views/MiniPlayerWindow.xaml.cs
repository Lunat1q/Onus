using System.Windows;
using System.Windows.Input;

namespace Onus.Views;

/// <summary>Always-on-top compact player. Not owned by the main window, so hiding the main window keeps it visible.</summary>
public partial class MiniPlayerWindow : Window
{
    private readonly MainWindow _main;

    public bool ForceClose { get; set; }

    public MiniPlayerWindow(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        Closing += (_, e) =>
        {
            if (ForceClose) return; // app shutting down
            e.Cancel = true; // Alt+F4 on the mini player = back to the full window
            _main.ExitMiniMode();
        };
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnExpand(object sender, RoutedEventArgs e) => _main.ExitMiniMode();
}
