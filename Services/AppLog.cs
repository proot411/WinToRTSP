using System;
using System.IO;

namespace WinToRTSP.Services;

/// <summary>
/// Minimal file logger kept intentionally lightweight: a single append-only
/// text file next to config.json (%LOCALAPPDATA%\WinToRTSP\log.txt).
/// Logging must never throw or grow unbounded.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 1_000_000; // truncate after ~1 MB
    private static readonly object Sync = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinToRTSP",
        "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                string? dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.WriteAllText(LogPath, string.Empty);
                }

                File.AppendAllText(
                    LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Swallow: diagnostics must never take the app down.
        }
    }
}
