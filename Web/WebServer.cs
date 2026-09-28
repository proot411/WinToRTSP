using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinToRTSP.Config;
using WinToRTSP.Security;
using WinToRTSP.Services;

namespace WinToRTSP.Web;

public class WebServer : IDisposable
{
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    // JsonSerializer.Serialize(anonymous) defaults to PascalCase, while the dashboard JS
    // reads camelCase (s.isStreaming, s.captureMethod...). Serializing the SSE payload with
    // web defaults keeps it identical to /api/status; otherwise every push overwrote the UI
    // with "undefined" and the dashboard showed "Stopped" while live.
    private static readonly JsonSerializerOptions LiveJsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsRunning => _isRunning;

    public bool Start(int port)
    {
        if (_isRunning) return true;

        try
        {
            var cts = new CancellationTokenSource();
            _cts = cts;

            // CreateBuilder (not CreateEmptyBuilder) is required: the "empty" builder
            // does not register Routing/HostFiltering/ForwardedHeaders, which makes
            // host startup crash asynchronously as soon as endpoints are mapped.
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>()
            });

            // Suppress verbose ASP.NET logs to keep console/memory lean
            builder.Logging.ClearProviders();

            builder.WebHost.UseKestrel(options =>
            {
                options.Listen(IPAddress.Any, port);
            });

            var app = builder.Build();

            // 1. IP Ban & Rate Limit Middleware
            app.Use(async (context, next) =>
            {
                var ip = context.Connection.RemoteIpAddress;
                if (SecurityManager.IsIpBlocked(ip, out var remainingTime))
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        error = "IP blocked due to multiple failed login attempts.",
                        remainingMinutes = Math.Round(remainingTime.TotalMinutes, 1)
                    }));
                    return;
                }
                await next(context);
            });

            // 2. Serve Single Page Dashboard
            app.MapGet("/", async context =>
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(WebAssets.IndexHtml);
            });

            // 3. Auth: Login
            app.MapPost("/api/auth/login", async (HttpContext context) =>
            {
                var ip = context.Connection.RemoteIpAddress;
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    string body = await reader.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    string username = root.GetProperty("username").GetString() ?? "";
                    string password = root.GetProperty("password").GetString() ?? "";

                    var config = ConfigManager.Current;
                    if (string.Equals(username, config.Username, StringComparison.OrdinalIgnoreCase) &&
                        SecurityManager.VerifyPassword(password))
                    {
                        SecurityManager.RecordSuccessfulAttempt(ip);
                        string sessionToken = SecurityManager.CreateSession(username);
                        string csrfToken = SecurityManager.GetCsrfToken(sessionToken) ?? "";

                        context.Response.Cookies.Append("wintortsp_session", sessionToken, new CookieOptions
                        {
                            HttpOnly = true,
                            SameSite = SameSiteMode.Strict,
                            Expires = DateTimeOffset.UtcNow.AddHours(12)
                        });

                        return Results.Ok(new { success = true, token = csrfToken });
                    }
                }
                catch { }

                SecurityManager.RecordFailedAttempt(ip);
                return Results.Json(new { success = false, message = "Invalid username or password" }, statusCode: StatusCodes.Status401Unauthorized);
            });

            // 4. Auth: Logout
            app.MapPost("/api/auth/logout", (HttpContext context) =>
            {
                if (context.Request.Cookies.TryGetValue("wintortsp_session", out var sessionToken))
                {
                    SecurityManager.InvalidateSession(sessionToken);
                    context.Response.Cookies.Delete("wintortsp_session");
                }
                return Results.Ok(new { success = true });
            });

            // 5. CSRF Token retrieval
            app.MapGet("/api/csrf-token", (HttpContext context) =>
            {
                if (!context.Request.Cookies.TryGetValue("wintortsp_session", out var sessionToken) ||
                    !SecurityManager.ValidateSession(sessionToken, out _))
                {
                    return Results.Unauthorized();
                }

                string csrf = SecurityManager.GetCsrfToken(sessionToken) ?? "";
                return Results.Ok(new { token = csrf });
            });

            // 6. Status API
            app.MapGet("/api/status", () =>
            {
                var status = StreamService.Instance.GetStatus();
                return Results.Ok(new
                {
                    status.IsStreaming,
                    status.StreamUrl,
                    status.WebUrl,
                    status.ActiveViewers,
                    status.CurrentFps,
                    status.CurrentBitrateKbps,
                    status.TargetFps,
                    status.TargetBitrateKbps,
                    status.ResolutionWidth,
                    status.ResolutionHeight,
                    status.CaptureMethod,
                    status.EncoderName,
                    status.AudioEnabled,
                    status.CpuPercent,
                    status.RamMb,
                    uptimeTotalSeconds = status.Uptime.TotalSeconds
                });
            });

            // 7. Server-Sent Events (SSE) for Real-Time Dashboard Metrics
            app.MapGet("/api/events", async (HttpContext context, CancellationToken token) =>
            {
                context.Response.Headers.Append("Content-Type", "text/event-stream");
                context.Response.Headers.Append("Cache-Control", "no-cache");
                context.Response.Headers.Append("Connection", "keep-alive");

                while (!token.IsCancellationRequested)
                {
                    var status = StreamService.Instance.GetStatus();
                    string json = JsonSerializer.Serialize(new
                    {
                        status.IsStreaming,
                        status.StreamUrl,
                        status.WebUrl,
                        status.ActiveViewers,
                        status.CurrentFps,
                        status.CurrentBitrateKbps,
                        status.TargetFps,
                        status.TargetBitrateKbps,
                        status.ResolutionWidth,
                        status.ResolutionHeight,
                        status.CaptureMethod,
                        status.EncoderName,
                        status.AudioEnabled,
                        status.CpuPercent,
                        status.RamMb,
                        uptimeTotalSeconds = status.Uptime.TotalSeconds
                    }, LiveJsonOptions);

                    await context.Response.WriteAsync($"data: {json}\n\n", token);
                    await context.Response.Body.FlushAsync(token);

                    await Task.Delay(500, token);
                }
            });

            // 8. Stream Controls (Protected with CSRF and Session)
            app.MapPost("/api/stream/start", (HttpContext context) =>
            {
                if (!ValidateAuthAndCsrf(context)) return Results.Unauthorized();
                var (ok, error) = StreamService.Instance.StartStream();
                return ok
                    ? Results.Ok(new { success = true })
                    : Results.Problem(string.IsNullOrWhiteSpace(error) ? "Failed to start stream" : error);
            });

            app.MapPost("/api/stream/stop", (HttpContext context) =>
            {
                if (!ValidateAuthAndCsrf(context)) return Results.Unauthorized();
                StreamService.Instance.StopStream();
                return Results.Ok(new { success = true });
            });

            // 9. Update Settings
            app.MapPost("/api/settings", async (HttpContext context) =>
            {
                if (!ValidateAuthAndCsrf(context)) return Results.Unauthorized();

                using var reader = new StreamReader(context.Request.Body);
                string body = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var config = ConfigManager.Current;
                if (root.TryGetProperty("targetFps", out var fpsProp))
                    config.TargetFps = fpsProp.GetInt32();
                if (root.TryGetProperty("bitrateKbps", out var bitrateProp))
                    config.BitrateKbps = bitrateProp.GetInt32();
                if (root.TryGetProperty("resolutionScalePercent", out var scaleProp))
                    config.ResolutionScalePercent = scaleProp.GetInt32();
                if (root.TryGetProperty("enableAudio", out var audioProp))
                    config.EnableAudio = audioProp.GetBoolean();
                if (root.TryGetProperty("rtspPort", out var portProp))
                    config.RtspPort = portProp.GetInt32();
                if (root.TryGetProperty("streamPath", out var pathProp))
                    config.StreamPath = pathProp.GetString() ?? "/live/screen";

                ConfigManager.Save();
                return Results.Ok(new { success = true });
            });

            // 10. Update Password
            app.MapPost("/api/settings/password", async (HttpContext context) =>
            {
                if (!ValidateAuthAndCsrf(context)) return Results.Unauthorized();

                using var reader = new StreamReader(context.Request.Body);
                string body = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(body);

                if (doc.RootElement.TryGetProperty("newPassword", out var pwdProp))
                {
                    string newPwd = pwdProp.GetString() ?? "";
                    var result = SecurityManager.SetPassword(newPwd);
                    if (!result.Success)
                    {
                        return Results.BadRequest(new { message = result.ErrorMessage });
                    }
                    return Results.Ok(new { success = true });
                }

                return Results.BadRequest(new { message = "Missing newPassword field" });
            });

            // Make async bind/startup failures observable (Release strips Debug.WriteLine).
            app.Lifetime.ApplicationStarted.Register(() =>
                AppLog.Write($"[web] Kestrel listening on port {port}"));

            _app = app;
            _ = Task.Run(async () =>
            {
                try
                {
                    await app.RunAsync(cts.Token).ConfigureAwait(false);
                    AppLog.Write("[web] server stopped");
                }
                catch (OperationCanceledException)
                {
                    AppLog.Write("[web] server stopped (cancelled)");
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[web] server crashed: {ex}");
                }
            });
            _isRunning = true;
            Debug.WriteLine($"[WEB] Kestrel Minimal API listening on http://localhost:{port}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"[web] Failed to start on port {port}: {ex}");
            Debug.WriteLine($"[WEB] Failed to start WebServer on port {port}: {ex.Message}");
            return false;
        }
    }

    private static bool ValidateAuthAndCsrf(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue("wintortsp_session", out var sessionToken) ||
            !SecurityManager.ValidateSession(sessionToken, out _))
        {
            return false;
        }

        string? csrfSubmitted = context.Request.Headers["X-CSRF-Token"];
        return SecurityManager.ValidateCsrfToken(sessionToken, csrfSubmitted);
    }

    public async Task StopAsync()
    {
        if (!_isRunning || _app == null) return;

        _isRunning = false;
        _cts?.Cancel();

        try
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        catch { }

        _app = null;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _app?.DisposeAsync().AsTask().Wait(1000);
    }
}
