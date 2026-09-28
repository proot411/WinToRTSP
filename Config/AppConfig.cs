using System;
using System.IO;
using System.Text.Json;

namespace WinToRTSP.Config;

public enum ThemePreference
{
    Auto,
    Light,
    Dark
}

public class AppConfig
{
    public int RtspPort { get; set; } = 8554;
    public string StreamPath { get; set; } = "/live/screen";
    public int WebPort { get; set; } = 8080;
    
    public int TargetFps { get; set; } = 30;
    public int BitrateKbps { get; set; } = 2500;
    public int ResolutionScalePercent { get; set; } = 100; // 50, 75, 100
    
    public bool EnableAudio { get; set; } = true;
    public bool RequireAuthentication { get; set; } = true;
    public string Username { get; set; } = "admin";
    
    // Argon2id hash & salt
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    
    public ThemePreference Theme { get; set; } = ThemePreference.Auto;
    public bool StartOnBoot { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoStartStream { get; set; } = false;
}

public static class ConfigManager
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinToRTSP");

    private static readonly string ConfigPath = Path.Combine(AppDataFolder, "config.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly object FileLock = new();

    public static AppConfig Current { get; private set; } = new();

    static ConfigManager()
    {
        Load();
    }

    public static void Load()
    {
        lock (FileLock)
        {
            try
            {
                if (!Directory.Exists(AppDataFolder))
                {
                    Directory.CreateDirectory(AppDataFolder);
                }

                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var loaded = JsonSerializer.Deserialize<AppConfig>(json);
                    if (loaded != null)
                    {
                        Current = loaded;
                        EnsureDefaults();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load config: {ex.Message}");
            }

            Current = new AppConfig();
            EnsureDefaults();
            Save();
        }
    }

    public static void Save()
    {
        lock (FileLock)
        {
            try
            {
                if (!Directory.Exists(AppDataFolder))
                {
                    Directory.CreateDirectory(AppDataFolder);
                }

                string json = JsonSerializer.Serialize(Current, JsonOptions);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save config: {ex.Message}");
            }
        }
    }

    private static void EnsureDefaults()
    {
        if (Current.RtspPort <= 0 || Current.RtspPort > 65535) Current.RtspPort = 8554;
        if (Current.WebPort <= 0 || Current.WebPort > 65535) Current.WebPort = 8080;
        if (string.IsNullOrWhiteSpace(Current.StreamPath)) Current.StreamPath = "/live/screen";
        if (!Current.StreamPath.StartsWith('/')) Current.StreamPath = "/" + Current.StreamPath;
        if (Current.TargetFps < 5 || Current.TargetFps > 60) Current.TargetFps = 30;
        if (Current.BitrateKbps < 500 || Current.BitrateKbps > 25000) Current.BitrateKbps = 2500;
        if (Current.ResolutionScalePercent != 50 && Current.ResolutionScalePercent != 75 && Current.ResolutionScalePercent != 100)
            Current.ResolutionScalePercent = 100;
        if (string.IsNullOrWhiteSpace(Current.Username)) Current.Username = "admin";
    }
}
