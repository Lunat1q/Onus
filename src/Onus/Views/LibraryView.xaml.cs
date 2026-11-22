using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Onus.Core;
using Onus.Services;
using Onus.ViewModels;

namespace Onus.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private LibraryViewModel? Vm => DataContext as LibraryViewModel;

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Track t) Vm?.PlayCommand.Execute(t);
    }

    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Enter: vm.PlayCommand.Execute(List.SelectedItem); break;
            case Key.Delete: vm.RecycleCommand.Execute(List.SelectedItems); break;
            case Key.C when ctrl: vm.CopyFilesCommand.Execute(List.SelectedItems); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Track t || Vm is not { } vm) return;
        var dialog = new MetadataDialog(t) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        try
        {
            TagIO.Update(t.Path, dialog.TitleText, dialog.ArtistText, dialog.GenreText, dialog.BpmText, dialog.LyricsText);
            vm.Reload(t);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TagLib.CorruptFileException)
        {
            MessageBox.Show($"Couldn't save tags:\n{ex.Message}", "Onus", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
