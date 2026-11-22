using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Onus.ViewModels;

namespace Onus.Views;

public partial class PlayerBar : UserControl
{
    public PlayerBar() => InitializeComponent();

    private PlayerViewModel? Vm => DataContext as PlayerViewModel;

    // The seek bar shows playback position one-way; while the mouse is down the user owns it, on release we seek.
    private void OnSeekStart(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm) vm.IsSeeking = true;
    }

    private void OnSeekEnd(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.Seek(((Slider)sender).Value);
        vm.IsSeeking = false;
    }

    private void OnMini(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.EnterMiniMode();
}
