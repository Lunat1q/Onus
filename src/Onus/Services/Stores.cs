using System.Text.Json;
using Onus.Core;

namespace Onus.Services;

/// <summary>keep / star / trash marks, keyed by Suno id (or path for non-Suno files). Same format as SunoSync's tags.json.</summary>
public sealed class TagStore
{
    private readonly Dictionary<string, string> _tags = JsonStore.Load<Dictionary<string, string>>(AppPaths.Tags);

    public string? Get(string key) => _tags.GetValueOrDefault(key);

    /// <summary>Set a mark; setting the same mark again clears it (toggle). Returns the new mark.</summary>
    public string? Toggle(string key, string tag)
    {
        if (_tags.GetValueOrDefault(key) == tag) _tags.Remove(key);
        else _tags[key] = tag;
        Save();
        return _tags.GetValueOrDefault(key);
    }

    public void Merge(IReadOnlyDictionary<string, string> other)
    {
        foreach (var (k, v) in other) _tags.TryAdd(k, v);
        Save();
    }

    private void Save() => JsonStore.Save(AppPaths.Tags, _tags);
}

public sealed class PromptStore
{
    public List<Prompt> All { get; } = JsonStore.Load<List<Prompt>>(AppPaths.Prompts);

    public void Save() => JsonStore.Save(AppPaths.Prompts, All);
}

/// <summary>One-click move from SunoSync: download folder, prompt vault and keep/star/trash marks.</summary>
public static class SunoSyncImport
{
    public static string Run(Settings settings, PromptStore prompts, TagStore tags)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var found = new List<string>();

        // appdirs.user_data_dir("SunoSync", "InternetThot") → %LocalAppData%\InternetThot\SunoSync
        var config = Path.Combine(local, "InternetThot", "SunoSync", "config.json");
        if (TryRead(config) is { } cfg && cfg.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } folder
            && Directory.Exists(folder))
        {
            settings.DownloadFolder = folder;
            found.Add("download folder");
        }

        // appdirs.user_data_dir("SunoSync", "SunoSync") → %LocalAppData%\SunoSync\SunoSync
        var vault = Path.Combine(local, "SunoSync", "SunoSync", "prompts.json");
        if (TryRead(vault) is { ValueKind: JsonValueKind.Object } pj)
        {
            var added = 0;
            foreach (var entry in pj.EnumerateObject())
            {
                if (prompts.All.Any(x => x.Id == entry.Name)) continue;
                var v = entry.Value;
                prompts.All.Add(new Prompt
                {
                    Id = entry.Name,
                    Title = v.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Text = v.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "",
                    Tags = v.TryGetProperty("tags", out var g) && g.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", g.EnumerateArray().Select(e => e.GetString())) : "",
                });
                added++;
            }
            prompts.Save();
            found.Add($"{added} prompts");
        }

        // tags.json sat next to SunoSync's main.py / exe; look in the usual checkout spots.
        foreach (var dir in new[] { settings.DownloadFolder, Path.GetDirectoryName(settings.DownloadFolder) ?? "" })
        {
            if (TryRead(Path.Combine(dir, "tags.json")) is { ValueKind: JsonValueKind.Object } tj)
            {
                tags.Merge(tj.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(e => e.Name, e => e.Value.GetString()!));
                found.Add("song marks");
                break;
            }
        }

        return found.Count == 0 ? "No SunoSync data found." : "Imported " + string.Join(", ", found) + ".";
    }

    private static JsonElement? TryRead(string path)
    {
        try { return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone() : null; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }
}
