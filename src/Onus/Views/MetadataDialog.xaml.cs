using System.Windows;
using Onus.Core;

namespace Onus.Views;

public partial class MetadataDialog : Window
{
    public MetadataDialog(Track track)
    {
        InitializeComponent();
        TitleBox.Text = track.Title;
        ArtistBox.Text = track.Artist;
        GenreBox.Text = track.StyleTags.Length > 0 ? track.StyleTags : track.Genre;
        BpmBox.Text = track.BpmText;
        LyricsBox.Text = track.Lyrics;
        FileText.Text = track.Path;
        Loaded += (_, _) => TitleBox.Focus();
    }

    public string TitleText => TitleBox.Text.Trim();
    public string ArtistText => ArtistBox.Text.Trim();
    public string GenreText => GenreBox.Text.Trim();
    public string BpmText => BpmBox.Text.Trim();
    public string LyricsText => LyricsBox.Text;

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
}
