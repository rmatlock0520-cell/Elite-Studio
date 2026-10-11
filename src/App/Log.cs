namespace EliteStudio.App;

/// <summary>
/// One plain text log, logs\studio.log in the data folder. Problems are written here instead of
/// being shown in a box, because a box would pop up over the game.
/// </summary>
public static class Log
{
    const long MaxBytes = 1_000_000;
    static readonly Lock Gate = new();

    /// <summary>The log file, or null before <see cref="Start"/> (tests): writes are then dropped.</summary>
    public static string? FilePath { get; private set; }

    public static void Start(string dataDir)
    {
        string dir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "studio.log");
        // Keep one previous log so a long session cannot fill the disk.
        if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            File.Move(path, Path.Combine(dir, "studio.old.log"), overwrite: true);
        FilePath = path;
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? problem = null) =>
        Write("ERROR", problem is null ? message : $"{message}: {problem.GetType().Name}: {problem.Message}");

    static void Write(string level, string message)
    {
        if (FilePath is null) return;
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}"); }
            catch (IOException) { }   // a locked log must never take the app down
            catch (UnauthorizedAccessException) { }
        }
    }
}
