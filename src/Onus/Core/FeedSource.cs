using System.Text.Json;

namespace Onus.Core;

public enum SourceKind { Library, Workspace, Playlist, Search }

/// <summary>Where to read clips from, and how to build the URL for page N (1-based, as shown to the user).
/// For <see cref="SourceKind.Search"/>, Id is the search term (empty = all public songs) and Rank the sort order.</summary>
public sealed record FeedSource(SourceKind Kind, string Id = "", string Name = "My Library", string Rank = SearchRank.Relevant)
{
    public const string ApiBase = "https://studio-api.prod.suno.com";
    public const int SearchPageSize = 20; // what /api/search/ returns per call

    public static FeedSource Library { get; } = new(SourceKind.Library);
    public static FeedSource Search { get; } = new(SourceKind.Search, Name: "Suno search");

    /// <summary>JSON body for POST /api/search/ (public songs, 0-based from_index).</summary>
    public string SearchBody(int page) => JsonSerializer.Serialize(new
    {
        search_queries = new[]
        {
            new { name = "public_song", search_type = "public_song", term = Id.Trim(),
                  from_index = (Math.Max(1, page) - 1) * SearchPageSize, rank_by = Rank },
        },
    });

    /// <summary>Playlists come back in one response; everything else pages.</summary>
    public bool Pages => Kind != SourceKind.Playlist;

    public string PageUrl(int page, ClipFilter filter)
    {
        var query = new List<string>();
        if (filter.LikedOnly) query.Add("liked=true");
        if (filter.IncludeTrashed) query.Add("trashed=true");

        string path;
        switch (Kind)
        {
            case SourceKind.Workspace:
                path = $"/api/project/{Uri.EscapeDataString(Id)}";
                query.Add($"page={page}"); // projects are 1-indexed
                break;
            case SourceKind.Playlist:
                path = $"/api/playlist/{Uri.EscapeDataString(Id)}/";
                break;
            case SourceKind.Search:
                return ApiBase + "/api/search/"; // POST with SearchBody; the feed filters don't apply
            default:
                if (filter.PublicOnly)
                {
                    path = "/api/feed/v2";
                    query.Add("is_public=true");
                }
                else path = "/api/feed/";
                query.Add($"page={page - 1}"); // the feed is 0-indexed: page=0 is newest
                break;
        }
        return query.Count == 0 ? ApiBase + path : $"{ApiBase}{path}?{string.Join('&', query)}";
    }
}

/// <summary>A workspace (project) or playlist the user can pick as a source.</summary>
public sealed record SourceOption(FeedSource Source, string Label, int? Count)
{
    public override string ToString() => Label;
}

/// <summary>Sort orders /api/search/ accepts (others answer 422).</summary>
public sealed record SearchRank(string Key, string Label)
{
    public const string Relevant = "most_relevant";

    public static SearchRank[] All { get; } =
    [
        new(Relevant, "Most relevant"), new("most_recent", "Newest"),
        new("play_count", "Most played"), new("upvote_count", "Most liked"),
    ];

    public override string ToString() => Label;
}
