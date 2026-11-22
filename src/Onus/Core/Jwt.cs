using System.Text;
using System.Text.Json;

namespace Onus.Core;

public static class Jwt
{
    /// <summary>The "exp" claim of a JWT, or null when the token is not a readable JWT.
    /// Only reads the payload; the signature is Suno's business.</summary>
    public static DateTimeOffset? Expiry(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var secs)
                ? DateTimeOffset.FromUnixTimeSeconds(secs) : null;
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }

    /// <summary>Strip pasted junk (ellipsis, smart quotes, whitespace) the way SunoSync did.</summary>
    public static string Clean(string token) =>
        new(token.Where(c => c is > ' ' and < (char)0x7F && c != '"').ToArray());
}
