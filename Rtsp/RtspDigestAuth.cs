using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WinToRTSP.Security;

namespace WinToRTSP.Rtsp;

public class DigestAuthChallenge
{
    public string Realm { get; set; } = "WinToRTSP";
    public string Nonce { get; set; } = string.Empty;
}

public static class RtspDigestAuth
{
    private static readonly Regex AuthRegex = new(
        @"(\w+)=(?:""([^""]*)""|([^,]*))",
        RegexOptions.Compiled);

    public static string GenerateNonce()
    {
        byte[] bytes = new byte[16];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static Dictionary<string, string> ParseAuthorizationHeader(string authHeader)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(authHeader)) return result;

        int spaceIdx = authHeader.IndexOf(' ');
        if (spaceIdx > 0)
        {
            authHeader = authHeader.Substring(spaceIdx + 1);
        }

        var matches = AuthRegex.Matches(authHeader);
        foreach (Match match in matches)
        {
            string key = match.Groups[1].Value;
            string value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            result[key] = value.Trim();
        }

        return result;
    }

    public static bool ValidateDigest(
        string method,
        string uri,
        string authHeader,
        string expectedNonce,
        string username)
    {
        var paramsDict = ParseAuthorizationHeader(authHeader);

        if (!paramsDict.TryGetValue("username", out var user) ||
            !paramsDict.TryGetValue("nonce", out var nonce) ||
            !paramsDict.TryGetValue("response", out var clientResponse))
        {
            return false;
        }

        if (!string.Equals(user, username, StringComparison.OrdinalIgnoreCase))
            return false;

        // Verify nonce
        if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            return false;

        string ha1 = SecurityManager.GetRtspHA1();
        if (string.IsNullOrEmpty(ha1)) return false;

        // HA2 = MD5(method:digestURI)
        string ha2 = SecurityManager.ComputeMd5($"{method}:{uri}");

        string expectedResponse;
        if (paramsDict.TryGetValue("qop", out var qop) && qop.Equals("auth", StringComparison.OrdinalIgnoreCase))
        {
            string nc = paramsDict.GetValueOrDefault("nc", "00000001");
            string cnonce = paramsDict.GetValueOrDefault("cnonce", "");
            expectedResponse = SecurityManager.ComputeMd5($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
        }
        else
        {
            // Standard RFC 2069 / 2617 without qop
            expectedResponse = SecurityManager.ComputeMd5($"{ha1}:{nonce}:{ha2}");
        }

        return string.Equals(expectedResponse, clientResponse, StringComparison.OrdinalIgnoreCase);
    }
}
