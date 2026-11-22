using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Onus.Core;

namespace Onus.Services;

public sealed class SunoAuthException(string message) : Exception(message);

/// <summary>Suno studio API. Every call carries a fresh Clerk JWT; a 401 refreshes it once and retries.</summary>
/// <param name="token">(forceRefresh, ct) → bearer token, or null when not signed in.</param>
public sealed class SunoClient(HttpClient http, Func<bool, CancellationToken, Task<string?>> token)
{
    private const string Api = FeedSource.ApiBase;

    /// <summary>Raw clips on one page (unfiltered). Empty list = end of feed.
    /// A workspace id that 404s is retried as a playlist, like SunoSync did.</summary>
    public async Task<(IReadOnlyList<ClipInfo> Clips, FeedSource Source)> GetPageAsync(
        FeedSource source, int page, ClipFilter filter, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = source.Kind == SourceKind.Search
                    ? await SendAsync(HttpMethod.Post, source.PageUrl(page, filter), ct, source.SearchBody(page))
                    : await SendAsync(HttpMethod.Get, source.PageUrl(page, filter), ct);
                if (resp.StatusCode == HttpStatusCode.NotFound && source.Kind == SourceKind.Workspace)
                {
                    Log.Warn("Workspace endpoint returned 404, trying it as a playlist.");
                    source = source with { Kind = SourceKind.Playlist };
                    continue;
                }
                var root = await ReadJsonAsync(resp, ct);
                var clips = ClipInfo.ExtractItems(root).Select(ClipInfo.Parse).OfType<ClipInfo>().ToList();
                return (clips, source);
            }
            catch (Exception e) when (attempt < 3 && IsTransient(e, ct))
            {
                Log.Warn($"Page {page}: {e.Message} — retrying ({attempt}/3)");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    public Task<List<SourceOption>> GetWorkspacesAsync(CancellationToken ct) =>
        ListAsync("/api/project/me?sort=created_at&show_trashed=false", "projects", SourceKind.Workspace, ct);

    public Task<List<SourceOption>> GetPlaylistsAsync(CancellationToken ct) =>
        ListAsync("/api/playlist/me?show_trashed=false&show_sharelist=false", "playlists", SourceKind.Playlist, ct);

    /// <summary>Full clip details; list views often omit the prompt/lyrics.</summary>
    public async Task<ClipInfo?> GetClipAsync(string id, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"{Api}/api/clip/{Uri.EscapeDataString(id)}", ct);
        return resp.IsSuccessStatusCode ? ClipInfo.Parse(await ReadJsonAsync(resp, ct)) : null;
    }

    /// <summary>Ask Suno to render a WAV and poll until the link shows up (≤ 2 min).</summary>
    public async Task<string?> ConvertWavAsync(string id, CancellationToken ct)
    {
        var escaped = Uri.EscapeDataString(id);
        using (var resp = await SendAsync(HttpMethod.Post, $"{Api}/api/gen/{escaped}/convert_wav/", ct))
            if (!resp.IsSuccessStatusCode) { Log.Warn($"WAV conversion request failed: {(int)resp.StatusCode}"); return null; }

        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            using var resp = await SendAsync(HttpMethod.Get, $"{Api}/api/gen/{escaped}/wav_file/", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) continue;
            if (!resp.IsSuccessStatusCode) continue;
            if (ClipInfo.FindWavUrl(await ReadJsonAsync(resp, ct)) is { } url) return url;
        }
        Log.Warn("WAV conversion timed out.");
        return null;
    }

    private async Task<List<SourceOption>> ListAsync(string path, string key, SourceKind kind, CancellationToken ct)
    {
        var result = new List<SourceOption>();
        for (var page = 1; page <= 50; page++) // ponytail: 50-page cap guards against an endless feed
        {
            using var resp = await SendAsync(HttpMethod.Get, $"{Api}{path}&page={page}", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) break;
            var root = await ReadJsonAsync(resp, ct);
            if (!root.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                break;
            foreach (var item in list.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "Untitled";
                result.Add(new SourceOption(new FeedSource(kind, id, name), name, Count(item)));
            }
        }
        return result;
    }

    private static int? Count(JsonElement item)
    {
        foreach (var k in (string[])["clip_count", "num_total_results", "song_count"])
            if (item.TryGetProperty(k, out var v) && v.TryGetInt32(out var n)) return n;
        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct, string? json = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var bearer = await token(attempt > 0, ct) ?? throw new SunoAuthException("Not signed in to Suno.");
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (json is not null) req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;
            resp.Dispose();
            if (attempt > 0) throw new SunoAuthException("Suno rejected the session. Sign in again.");
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    internal static bool IsTransient(Exception e, CancellationToken ct) =>
        !ct.IsCancellationRequested && e is HttpRequestException { StatusCode: null or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError }
            or TaskCanceledException or IOException;
}
