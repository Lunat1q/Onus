using System.Text.Json;
using Onus.Core;

namespace Onus.Tests;

public class ClipInfoTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Parses_project_wrapper_and_metadata()
    {
        var c = ClipInfo.Parse(J("""
            {"clip":{"id":"a1","title":"Neon","display_name":"me","audio_url":"https://cdn1.suno.ai/a1.mp3",
             "created_at":"2026-03-04T10:00:00Z","is_public":true,
             "metadata":{"tags":"synthwave, 120 bpm","prompt":"[Verse] hi","duration":181.5,"type":"gen"}}}
            """))!;
        c.Id.Should().Be("a1");
        c.Artist.Should().Be("me");
        c.Lyrics.Should().Be("[Verse] hi");
        c.Duration.Should().Be(181.5);
        c.Month.Should().Be("2026-03");
        c.IsPublic.Should().BeTrue();
    }

    [Fact]
    public void Withheld_audio_url_is_locked_not_a_url_to_fetch()
    {
        var c = ClipInfo.Parse(J("""
            {"id":"a1","audio_url":"https://studio-api.prod.suno.com/api/forbidden",
             "media_urls":[{"url":"https://d2lwuy8qc234o3.cloudfront.net/1/clip/a1.m4a","delivery":"progressive","encoding":"1.0.0"}]}
            """))!;
        c.AudioUrl.Should().BeNull();
        c.AudioLocked.Should().BeTrue();

        var item = new Onus.ViewModels.CloudClip(c, onDisk: false).ToPlayItem()!;
        item.Source.Should().Be(new Uri("https://suno.com/song/a1"));
        Onus.Services.SunoWebPlayer.Handles(item.Source).Should().BeTrue();
    }

    [Fact]
    public void Ghost_entry_without_id_is_dropped() =>
        ClipInfo.Parse(J("""{"title":"Unknown"}""")).Should().BeNull();

    [Theory]
    [InlineData("""{"id":"x","is_liked":true}""", true, false)]
    [InlineData("""{"id":"x","reaction":{"reaction_type":"L"}}""", true, false)]
    [InlineData("""{"id":"x","metadata":{"vote":"up"}}""", true, false)]
    [InlineData("""{"id":"x","reaction":{"reaction_type":"D"}}""", false, true)]
    [InlineData("""{"id":"x","reaction":null,"vote":"down"}""", false, true)]
    public void Liked_and_disliked_come_from_any_of_three_fields(string json, bool liked, bool disliked)
    {
        var c = ClipInfo.Parse(J(json))!;
        c.IsLiked.Should().Be(liked);
        c.IsDisliked.Should().Be(disliked);
    }

    [Theory]
    [InlineData("""[{"id":"1"},{"id":"2"}]""")]
    [InlineData("""{"clips":[{"id":"1"},{"id":"2"}]}""")]
    [InlineData("""{"project_clips":[{"clip":{"id":"1"}},{"clip":{"id":"2"}}]}""")]
    [InlineData("""{"playlist":{"playlist_clips":[{"clip":{"id":"1"}},{"clip":{"id":"2"}}]}}""")]
    [InlineData("""{"result":{"public_song":{"total_hits":9,"result":[{"id":"1"},{"id":"2"}]}}}""")]
    public void Extracts_items_from_every_response_shape(string json) =>
        ClipInfo.ExtractItems(J(json)).Select(ClipInfo.Parse).Select(c => c!.Id).Should().Equal("1", "2");

    [Fact]
    public void Finds_wav_url_deep_in_payload() =>
        ClipInfo.FindWavUrl(J("""{"data":{"files":[{"u":"https://cdn/x.mp3"},{"u":"https://cdn/x.wav"}]}}"""))
            .Should().Be("https://cdn/x.wav");

    [Theory]
    [InlineData("Song (Vocals)", "gen", true)]
    [InlineData("Song", "gen_stem", true)]
    [InlineData("Song", "gen", false)]
    public void Detects_stems(string title, string type, bool stem) =>
        new ClipInfo { Id = "x", Title = title, ClipType = type }.IsStem.Should().Be(stem);

    [Fact]
    public void Base_title_strips_longest_stem_suffix_first() =>
        new ClipInfo { Id = "x", Title = "Song (Backing Vocals)" }.BaseTitle.Should().Be("Song");
}

public class ClipFilterTests
{
    private static ClipInfo C(string type = "gen", bool trashed = false, bool liked = false, bool disliked = false,
        string title = "t", string tags = "") =>
        new() { Id = Guid.NewGuid().ToString(), ClipType = type, IsTrashed = trashed, IsLiked = liked, IsDisliked = disliked, Title = title, Tags = tags };

    [Fact]
    public void Defaults_hide_trash_stems_disliked_and_studio_clips()
    {
        var f = new ClipFilter();
        f.Matches(C()).Should().BeTrue();
        f.Matches(C(trashed: true)).Should().BeFalse();
        f.Matches(C(type: "gen_stem")).Should().BeFalse();
        f.Matches(C(disliked: true)).Should().BeFalse();
        f.Matches(C(type: "studio_clip")).Should().BeFalse();
    }

    [Fact]
    public void Stems_only_overrides_hide_stems()
    {
        var f = new ClipFilter { StemsOnly = true, HideStems = true };
        f.Matches(C(type: "gen_stem")).Should().BeTrue();
        f.Matches(C()).Should().BeFalse();
    }

    [Fact]
    public void Kind_splits_uploads_from_generations()
    {
        new ClipFilter { Kind = ClipKind.Uploads }.Matches(C(type: "upload")).Should().BeTrue();
        new ClipFilter { Kind = ClipKind.Uploads }.Matches(C()).Should().BeFalse();
        new ClipFilter { Kind = ClipKind.Generations }.Matches(C(type: "upload")).Should().BeFalse();
    }

    [Fact]
    public void Tag_include_and_exclude_are_comma_lists()
    {
        var f = new ClipFilter { TagsInclude = "lofi, jazz", TagsExclude = "metal" };
        f.Matches(C(tags: "Chill Lofi beats")).Should().BeTrue();
        f.Matches(C(tags: "jazz metal")).Should().BeFalse();
        f.Matches(C(tags: "pop")).Should().BeFalse();
    }

    [Fact]
    public void Search_looks_at_title_tags_and_prompt() =>
        new ClipFilter { Search = "neon" }.Matches(C(title: "Neon Nights")).Should().BeTrue();
}

public class FeedSourceTests
{
    private static readonly ClipFilter None = new() { HideDisliked = false };

    [Fact]
    public void Library_feed_is_zero_indexed() =>
        FeedSource.Library.PageUrl(1, None).Should().Be("https://studio-api.prod.suno.com/api/feed/?page=0");

    [Fact]
    public void Workspace_is_one_indexed_and_escaped() =>
        new FeedSource(SourceKind.Workspace, "a b").PageUrl(2, None).Should().EndWith("/api/project/a%20b?page=2");

    [Fact]
    public void Playlist_does_not_page()
    {
        var p = new FeedSource(SourceKind.Playlist, "pl");
        p.Pages.Should().BeFalse();
        p.PageUrl(1, None).Should().EndWith("/api/playlist/pl/");
    }

    [Fact]
    public void Search_pages_by_twenty_from_zero()
    {
        FeedSource.Search.Pages.Should().BeTrue();
        FeedSource.Search.PageUrl(1, None).Should().Be("https://studio-api.prod.suno.com/api/search/");
        FeedSource.Search.SearchBody(1).Should().Contain("\"from_index\":0").And.Contain("\"term\":\"\"")
            .And.Contain("\"rank_by\":\"most_relevant\"");
        FeedSource.Search.SearchBody(2).Should().Contain("\"from_index\":20");
    }

    [Fact]
    public void Public_and_liked_filters_go_to_the_server() =>
        FeedSource.Library.PageUrl(3, None with { PublicOnly = true, LikedOnly = true })
            .Should().EndWith("/api/feed/v2?liked=true&is_public=true&page=2");
}

public class SunoWebPlayerTests
{
    [Theory]
    [InlineData("https://suno.com/song/a1", true)]
    [InlineData("https://cdn1.suno.ai/a1.mp3", false)]
    [InlineData("https://suno.com/me", false)]
    [InlineData("file:///C:/music/a.mp3", false)]
    public void Only_song_pages_go_through_suno(string url, bool expected) =>
        Onus.Services.SunoWebPlayer.Handles(new Uri(url)).Should().Be(expected);

    [Fact]
    public void Parses_progress_and_errors_from_the_page()
    {
        var m = Onus.Services.SunoWebPlayer.Parse("""{"t":"time","pos":12.5,"dur":153.2,"playing":true}""")!;
        m.Should().Be(new Onus.Services.SunoWebPlayer.Message("time", 12.5, 153.2, true, ""));
        Onus.Services.SunoWebPlayer.Parse("""{"t":"error","m":"NotAllowedError"}""")!.Text.Should().Be("NotAllowedError");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("\"just a string\"")]
    [InlineData("""{"pos":1}""")]
    public void Malformed_page_messages_are_ignored(string json) =>
        Onus.Services.SunoWebPlayer.Parse(json).Should().BeNull();

    [Fact]
    public void Missing_or_null_numbers_read_as_zero() =>
        Onus.Services.SunoWebPlayer.Parse("""{"t":"time","pos":null}""")!.Duration.Should().Be(0);
}
