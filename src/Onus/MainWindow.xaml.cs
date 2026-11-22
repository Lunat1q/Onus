using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Onus.ViewModels;
using Onus.Views;

namespace Onus;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private MiniPlayerWindow? _mini;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.SignInRequested += OpenSignIn;
        SourceInitialized += async (_, _) => await vm.Auth.InitAsync(new WindowInteropHelper(this).Handle);
        PreviewKeyDown += OnKey;
    }

    /// <summary>Space = play/pause, Ctrl+→/← = next/previous, A = analyzer, unless the user is typing.</summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Space) { _vm.Player.TogglePlayCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.Right) { _vm.Player.NextCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.Left) { _vm.Player.PreviousCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.None) { _vm.ToggleAnalyzerCommand.Execute(null); e.Handled = true; }
    }

    private void OpenSignIn()
    {
        if (OwnedWindows.OfType<SignInWindow>().FirstOrDefault() is { } open) { open.Activate(); return; }
        var window = new SignInWindow(_vm.Auth) { Owner = this };
        if (window.ShowDialog() == true) _vm.ShowToast("Signed in to Suno");
    }

    public void EnterMiniMode()
    {
        _mini ??= new MiniPlayerWindow(this) { DataContext = _vm.Player };
        _mini.Left = Left + Width - _mini.Width - 24;
        _mini.Top = Top + 24;
        _mini.Show();
        Hide();
    }

    public void ExitMiniMode()
    {
        _mini?.Hide();
        Show();
        Activate();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_mini is not null)
        {
            _mini.ForceClose = true;
            _mini.Close();
        }
        base.OnClosed(e);
    }
}
