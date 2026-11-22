namespace Onus.Core;

/// <summary>Append-only file log plus an in-memory feed for the Cloud page's activity panel.
/// Never pass tokens or cookies in here.</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    public static event Action<string, string>? Written; // level, message

    public static void Info(string msg) => Write("info", msg);
    public static void Warn(string msg) => Write("warn", msg);
    public static void Error(string msg) => Write("error", msg);
    public static void Success(string msg) => Write("ok", msg);

    private static void Write(string level, string msg)
    {
        lock (Gate)
        {
            try { File.AppendAllText(AppPaths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}"); }
            catch (IOException) { /* log is best effort */ }
        }
        Written?.Invoke(level, msg);
    }

    /// <summary>Keep the log from growing forever: trim to the last ~1 MB at startup.</summary>
    public static void Trim()
    {
        try
        {
            var info = new FileInfo(AppPaths.Log);
            if (!info.Exists || info.Length < 2_000_000) return;
            var text = File.ReadAllText(AppPaths.Log);
            File.WriteAllText(AppPaths.Log, text[^1_000_000..]);
        }
        catch (IOException) { }
    }
}
