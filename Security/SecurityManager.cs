using System;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using WinToRTSP.Config;

namespace WinToRTSP.Security;

public class FailedAttemptRecord
{
    public int Count { get; set; }
    public DateTime FirstAttempt { get; set; }
    public DateTime? BannedUntil { get; set; }
}

public static class SecurityManager
{
    private const int MinPasswordLength = 12;
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BanDuration = TimeSpan.FromMinutes(15);

    // IP -> Attempt tracker
    private static readonly ConcurrentDictionary<string, FailedAttemptRecord> IpAttempts = new();

    // Session token -> (Username, Expiration)
    private static readonly ConcurrentDictionary<string, (string Username, DateTime Expiration)> ActiveSessions = new();
    private static readonly ConcurrentDictionary<string, string> SessionCsrfTokens = new();

    // Precomputed HA1 for RTSP Digest Auth: MD5(username:realm:password)
    public static string RtspRealm { get; set; } = "WinToRTSP";
    private static string _rtspHA1 = string.Empty;

    static SecurityManager()
    {
        InitializeCredentials();
    }

    public static void InitializeCredentials()
    {
        var config = ConfigManager.Current;
        if (string.IsNullOrEmpty(config.PasswordHash) || string.IsNullOrEmpty(config.PasswordSalt))
        {
            // Set default secure password of min 12 chars
            SetPassword("WinToRTSP_Admin2026!");
        }
        else
        {
            // Ensure HA1 is loaded if available, or generated if we have stored HA1
            // In case HA1 needs refresh, it will be set upon SetPassword
        }
    }

    public static (bool Success, string ErrorMessage) ValidatePasswordStrength(string password)
    {
        if (string.IsNullOrEmpty(password))
            return (false, "Password cannot be empty.");

        if (password.Length < MinPasswordLength)
            return (false, $"Password must be at least {MinPasswordLength} characters in length.");

        return (true, string.Empty);
    }

    public static (bool Success, string ErrorMessage) SetPassword(string newPassword)
    {
        var validation = ValidatePasswordStrength(newPassword);
        if (!validation.Success)
            return validation;

        byte[] salt = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
        }

        byte[] hash = HashArgon2id(newPassword, salt);

        var config = ConfigManager.Current;
        config.PasswordSalt = Convert.ToBase64String(salt);
        config.PasswordHash = Convert.ToBase64String(hash);

        // Precompute RFC 2617 HA1 = MD5(username:realm:password)
        _rtspHA1 = ComputeMd5($"{config.Username}:{RtspRealm}:{newPassword}");

        ConfigManager.Save();
        return (true, string.Empty);
    }

    public static bool VerifyPassword(string inputPassword)
    {
        var config = ConfigManager.Current;
        if (string.IsNullOrEmpty(config.PasswordHash) || string.IsNullOrEmpty(config.PasswordSalt))
            return false;

        try
        {
            byte[] salt = Convert.FromBase64String(config.PasswordSalt);
            byte[] expectedHash = Convert.FromBase64String(config.PasswordHash);
            byte[] actualHash = HashArgon2id(inputPassword, salt);

            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] HashArgon2id(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = 2,
            MemorySize = 19456, // 19 MB (lightweight for legacy 2nd Gen CPUs while cryptographically sound)
            Iterations = 2
        };
        return argon2.GetBytes(32);
    }

    public static string GetRtspHA1()
    {
        return _rtspHA1;
    }

    // --- Brute Force & IP Ban Logic ---

    public static bool IsIpBlocked(IPAddress? ip, out TimeSpan remainingBanTime)
    {
        remainingBanTime = TimeSpan.Zero;
        if (ip == null) return false;

        string ipStr = ip.ToString();
        if (IpAttempts.TryGetValue(ipStr, out var record))
        {
            lock (record)
            {
                if (record.BannedUntil.HasValue)
                {
                    var now = DateTime.UtcNow;
                    if (now < record.BannedUntil.Value)
                    {
                        remainingBanTime = record.BannedUntil.Value - now;
                        return true;
                    }
                    else
                    {
                        // Ban expired, reset record
                        record.BannedUntil = null;
                        record.Count = 0;
                        record.FirstAttempt = now;
                    }
                }
            }
        }
        return false;
    }

    public static void RecordFailedAttempt(IPAddress? ip)
    {
        if (ip == null) return;
        string ipStr = ip.ToString();
        var now = DateTime.UtcNow;

        var record = IpAttempts.GetOrAdd(ipStr, _ => new FailedAttemptRecord
        {
            Count = 0,
            FirstAttempt = now
        });

        lock (record)
        {
            if (now - record.FirstAttempt > AttemptWindow)
            {
                // Reset attempt window
                record.Count = 1;
                record.FirstAttempt = now;
                record.BannedUntil = null;
            }
            else
            {
                record.Count++;
                if (record.Count >= MaxFailedAttempts)
                {
                    record.BannedUntil = now.Add(BanDuration);
                    System.Diagnostics.Debug.WriteLine($"[SECURITY] IP {ipStr} has been blocked for {BanDuration.TotalMinutes} minutes due to brute-force detection.");
                }
            }
        }
    }

    public static void RecordSuccessfulAttempt(IPAddress? ip)
    {
        if (ip == null) return;
        string ipStr = ip.ToString();
        IpAttempts.TryRemove(ipStr, out _);
    }

    // --- Session & CSRF Management ---

    public static string CreateSession(string username)
    {
        byte[] tokenBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(tokenBytes);
        }
        string sessionToken = Convert.ToHexString(tokenBytes);

        // Session valid for 12 hours
        ActiveSessions[sessionToken] = (username, DateTime.UtcNow.AddHours(12));

        // Generate CSRF token for this session
        byte[] csrfBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(csrfBytes);
        }
        string csrfToken = Convert.ToHexString(csrfBytes);
        SessionCsrfTokens[sessionToken] = csrfToken;

        return sessionToken;
    }

    public static bool ValidateSession(string? sessionToken, out string username)
    {
        username = string.Empty;
        if (string.IsNullOrEmpty(sessionToken)) return false;

        if (ActiveSessions.TryGetValue(sessionToken, out var session))
        {
            if (DateTime.UtcNow < session.Expiration)
            {
                username = session.Username;
                return true;
            }
            else
            {
                ActiveSessions.TryRemove(sessionToken, out _);
                SessionCsrfTokens.TryRemove(sessionToken, out _);
            }
        }
        return false;
    }

    public static string? GetCsrfToken(string sessionToken)
    {
        if (SessionCsrfTokens.TryGetValue(sessionToken, out var csrf))
            return csrf;
        return null;
    }

    public static bool ValidateCsrfToken(string? sessionToken, string? submittedCsrfToken)
    {
        if (string.IsNullOrEmpty(sessionToken) || string.IsNullOrEmpty(submittedCsrfToken))
            return false;

        if (SessionCsrfTokens.TryGetValue(sessionToken, out var expectedCsrf))
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedCsrf),
                Encoding.UTF8.GetBytes(submittedCsrfToken));
        }
        return false;
    }

    public static void InvalidateSession(string sessionToken)
    {
        ActiveSessions.TryRemove(sessionToken, out _);
        SessionCsrfTokens.TryRemove(sessionToken, out _);
    }

    public static string ComputeMd5(string input)
    {
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
