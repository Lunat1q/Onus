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

    /// <summary>Keep the column header over the rows: the vertical scrollbar only takes width when the list
    /// overflows, so the header's right inset follows it (row padding 10 + whatever the bar takes).</summary>
    private void OnListScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange == 0 && e.ExtentHeightChange == 0) return;
        var bar = e.OriginalSource is ScrollViewer sv ? Math.Max(0, List.ActualWidth - sv.ViewportWidth) : 0;
        Header.Margin = new Thickness(10, 4, 10 + bar, 6);
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
            case Key.E when ctrl: OnEditAudio(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is MenuItem { Tag: string ext })
            await vm.SaveAsAsync(List.SelectedItems.OfType<Track>().ToList(), ext);
    }

    /// <summary>Waveform editor for the selected song; anything saved → rescan (new length, mood redone, a copy listed).</summary>
    private async void OnEditAudio(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Track t || Vm is not { } vm) return;
        var editor = new EditorWindow(t, vm.Player) { Owner = Window.GetWindow(this) };
        editor.ShowDialog();
        if (editor.Saved) await vm.RescanAsync();
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
