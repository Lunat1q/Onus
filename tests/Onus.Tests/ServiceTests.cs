using System.Net;
using System.Text;
using Onus.Core;
using Onus.Services;

namespace Onus.Tests;

/// <summary>Records requests and answers from a script.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class SunoClientTests
{
    [Fact]
    public async Task Sends_bearer_and_refreshes_the_token_once_on_401()
    {
        var calls = 0;
        var handler = new StubHandler(r => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : StubHandler.Json("""[{"id":"a"}]"""));
        var refreshes = new List<bool>();
        var client = new SunoClient(new HttpClient(handler), (refresh, _) => { refreshes.Add(refresh); return Task.FromResult<string?>(refresh ? "fresh" : "stale"); });

        var (clips, _) = await client.GetPageAsync(FeedSource.Library, 1, new ClipFilter(), CancellationToken.None);

        clips.Select(c => c.Id).Should().Equal("a");
        refreshes.Should().Equal(false, true);
        handler.Requests[1].Headers.Authorization!.ToString().Should().Be("Bearer fresh");
    }

    [Fact]
    public async Task Second_401_is_an_auth_error_not_a_loop()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new SunoClient(new HttpClient(handler), (_, _) => Task.FromResult<string?>("t"));

        var act = () => client.GetPageAsync(FeedSource.Library, 1, new ClipFilter(), CancellationToken.None);

        await act.Should().ThrowAsync<SunoAuthException>();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Not_signed_in_throws_before_any_request()
    {
        var handler = new StubHandler(_ => StubHandler.Json("[]"));
        var client = new SunoClient(new HttpClient(handler), (_, _) => Task.FromResult<string?>(null));

        await client.Invoking(c => c.GetPageAsync(FeedSource.Library, 1, new ClipFilter(), CancellationToken.None))
            .Should().ThrowAsync<SunoAuthException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Workspace_404_falls_back_to_playlist_endpoint()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath.Contains("/api/project/")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : StubHandler.Json("""{"playlist_clips":[{"clip":{"id":"p1"}}]}"""));
        var client = new SunoClient(new HttpClient(handler), (_, _) => Task.FromResult<string?>("t"));

        var (clips, source) = await client.GetPageAsync(new FeedSource(SourceKind.Workspace, "abc"), 1, new ClipFilter(), CancellationToken.None);

        source.Kind.Should().Be(SourceKind.Playlist);
        clips.Single().Id.Should().Be("p1");
        handler.Requests.Last().RequestUri!.AbsolutePath.Should().Be("/api/playlist/abc/");
    }

    [Fact]
    public async Task Search_posts_term_rank_and_offset_to_the_api_and_reads_public_songs()
    {
        string? body = null;
        var handler = new StubHandler(r =>
        {
            body = r.Content!.ReadAsStringAsync().Result;
            return StubHandler.Json("""{"result":{"public_song":{"total_hits":2,"result":[{"id":"s1"},{"id":"s2"}]}}}""");
        });
        var client = new SunoClient(new HttpClient(handler), (_, _) => Task.FromResult<string?>("t"));
        var source = FeedSource.Search with { Id = " neon ", Rank = "play_count" };

        var (clips, _) = await client.GetPageAsync(source, 3, new ClipFilter(), CancellationToken.None);

        clips.Select(c => c.Id).Should().Equal("s1", "s2");
        var req = handler.Requests.Single();
        req.Method.Should().Be(HttpMethod.Post);
        req.RequestUri!.ToString().Should().Be("https://studio-api.prod.suno.com/api/search/");
        req.Headers.Authorization!.ToString().Should().Be("Bearer t");
        body.Should().Contain("\"term\":\"neon\"").And.Contain("\"from_index\":40").And.Contain("\"rank_by\":\"play_count\"")
            .And.Contain("\"search_type\":\"public_song\"");
    }

    [Fact]
    public void Rate_limit_is_retried_like_a_server_error()
    {
        SunoClient.IsTransient(new HttpRequestException("", null, HttpStatusCode.TooManyRequests), CancellationToken.None).Should().BeTrue();
        SunoClient.IsTransient(new HttpRequestException("", null, HttpStatusCode.Forbidden), CancellationToken.None).Should().BeFalse();
    }

    [Fact]
    public async Task Lists_playlists_across_pages_until_empty()
    {
        var handler = new StubHandler(r => r.RequestUri!.Query.Contains("page=1")
            ? StubHandler.Json("""{"playlists":[{"id":"p1","name":"Gym","num_total_results":12}]}""")
            : StubHandler.Json("""{"playlists":[]}"""));
        var client = new SunoClient(new HttpClient(handler), (_, _) => Task.FromResult<string?>("t"));

        var list = await client.GetPlaylistsAsync(CancellationToken.None);

        list.Should().ContainSingle(o => o.Label == "Gym" && o.Count == 12 && o.Source.Kind == SourceKind.Playlist);
    }
}

public class DownloaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("onus-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Downloader Make(StubHandler cdn) =>
        new(new SunoClient(new HttpClient(cdn), (_, _) => Task.FromResult<string?>("secret")), new HttpClient(cdn));

    [Fact]
    public async Task Saves_via_part_file_and_never_sends_the_token_to_the_cdn()
    {
        var cdn = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) });
        var target = Path.Combine(_dir, "Song.mp3");

        var path = await Make(cdn).SaveStreamAsync("https://cdn1.suno.ai/a.mp3", target, null, CancellationToken.None);

        path.Should().Be(target);
        new FileInfo(path).Length.Should().Be(4096);
        Directory.GetFiles(_dir, "*.part").Should().BeEmpty();
        cdn.Requests.Should().OnlyContain(r => r.Headers.Authorization == null);
    }

    [Fact]
    public async Task Analytics_capture_is_tagged_like_a_download()
    {
        const string id = "0f8fad5b-d9cb-469f-a165-70867728950e";
        var http = new StubHandler(r => r.RequestUri!.AbsolutePath.StartsWith("/api/clip/")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$$"""
                {"id":"{{{id}}}","title":"Bit March","display_name":"me","image_large_url":"https://cdn2.suno.ai/i.jpg",
                 "created_at":"2026-11-21T10:00:00Z","metadata":{"tags":"chiptune","prompt":"[Verse] march"}}
                """) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]) });
        var path = Path.Combine(_dir, id + ".encoding.m4a");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.m4a"), path);

        // The Suno page's row knows only the id and title: prompt/lyrics/art come from a refetch.
        await Make(http).TagCaptureAsync(path, new ClipInfo { Id = id, Title = "Bit March" });

        var t = TagIO.Read(path);
        (t.Title, t.Artist, t.Genre, t.Lyrics, t.SunoId).Should().Be(("Bit March", "me", "chiptune", "[Verse] march", id));
        t.ArtPath.Should().NotBeNull();
        http.Requests.Where(r => r.RequestUri!.Host != "studio-api.prod.suno.com").Should().OnlyContain(r => r.Headers.Authorization == null);
    }

    [Fact]
    public async Task Same_title_twice_gets_a_v2_name()
    {
        var cdn = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) });
        var target = Path.Combine(_dir, "Song.mp3");
        var d = Make(cdn);

        await d.SaveStreamAsync("https://cdn/a.mp3", target, null, CancellationToken.None);
        var second = await d.SaveStreamAsync("https://cdn/b.mp3", target, null, CancellationToken.None);

        Path.GetFileName(second).Should().Be("Song v2.mp3");
    }

    [Fact]
    public async Task Tiny_response_is_rejected_after_retries_and_leaves_nothing_behind()
    {
        var cdn = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) });

        var act = () => Make(cdn).SaveStreamAsync("https://cdn/a.mp3", Path.Combine(_dir, "x.mp3"), null, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*too small*");
        Directory.GetFiles(_dir).Should().BeEmpty();
        cdn.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Forbidden_fails_at_once_and_names_the_refused_url_without_its_query()
    {
        var cdn = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var act = () => Make(cdn).SaveStreamAsync("https://cdn1.suno.ai/a.mp3?sig=secret", Path.Combine(_dir, "x.mp3"), null, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("403").And.Contain("cdn1.suno.ai/a.mp3").And.NotContain("secret");
        cdn.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Server_error_is_retried()
    {
        var cdn = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var act = () => Make(cdn).SaveStreamAsync("https://cdn/a.mp3", Path.Combine(_dir, "x.mp3"), null, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        cdn.Requests.Should().HaveCount(3);
    }
}
