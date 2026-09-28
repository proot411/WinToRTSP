using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Microsoft.Win32;
using WinToRTSP.Config;
using WinToRTSP.Security;
using WinToRTSP.Services;
using WinToRTSP.UI;
using WinToRTSP.Web;

namespace WinToRTSP;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _metricsTimer;
    private TrayIconManager? _trayManager;
    private WebServer? _webServer;
    private bool _isInitializing = true;
    private bool _hasShownTrayNotice = false;

    // Reused every metrics tick (500ms) — a frozen brush allocates nothing and is thread-safe.
    private static readonly SolidColorBrush StoppedGrayBrush = CreateFrozenBrush(139, 148, 158);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public MainWindow()
    {
        InitializeComponent();

        FitWindowToScreen();

        _metricsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _metricsTimer.Tick += MetricsTimer_Tick;

        Loaded += MainWindow_Loaded;
    }

    /// <summary>
    /// Keeps the window fully inside the primary working area (taskbar included) so the
    /// title bar and the bottom config panels stay reachable on small screens
    /// (e.g. 1366x768, where a 780px-tall window used to open clipped top and bottom).
    /// </summary>
    private void FitWindowToScreen()
    {
        var wa = SystemParameters.WorkArea;

        if (Width > wa.Width) Width = Math.Max(MinWidth, wa.Width);
        if (Height > wa.Height) Height = Math.Max(MinHeight, wa.Height);

        // Deterministic centering inside the working area (overrides CenterScreen).
        Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
        Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AppLog.Write("=== WinToRTSP starting ===");

        // Each step is isolated so one failure cannot silently skip the rest.
        RunStartupStep("tray icon", () => _trayManager = new TrayIconManager(this));

        RunStartupStep("load config", LoadConfigToUi);

        RunStartupStep("web server", () =>
        {
            _webServer = new WebServer();
            if (!_webServer.Start(ConfigManager.Current.WebPort))
            {
                AppLog.Write($"[startup] web server reported failure on port {ConfigManager.Current.WebPort}");
            }
        });

        RunStartupStep("theme", () =>
        {
            ApplyTheme(ConfigManager.Current.Theme);
            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        });

        RunStartupStep("stream status", () =>
        {
            StreamService.Instance.StatusChanged += StreamService_StatusChanged;
            _metricsTimer.Start();
        });

        _isInitializing = false;

        // Auto-start stream if configured
        if (ConfigManager.Current.AutoStartStream)
        {
            RunStartupStep("auto-start stream", () =>
            {
                var (started, error) = StreamService.Instance.StartStream();
                if (!started)
                {
                    AppLog.Write($"[startup] auto-start stream failed: {error}");
                }
            });
        }

        AppLog.Write("=== WinToRTSP startup complete ===");
    }

    private static void RunStartupStep(string name, Action step)
    {
        try
        {
            step();
            AppLog.Write($"[startup] {name}: ok");
        }
        catch (Exception ex)
        {
            AppLog.Write($"[startup] {name}: FAILED - {ex}");
        }
    }

    private void LoadConfigToUi()
    {
        var cfg = ConfigManager.Current;

        // Theme combo — match by Tag, never by index (the enum is Auto/Light/Dark).
        foreach (ComboBoxItem item in ThemeComboBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), cfg.Theme.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                ThemeComboBox.SelectedItem = item;
                break;
            }
        }

        // Scale
        foreach (ComboBoxItem item in ScaleComboBox.Items)
        {
            if (int.TryParse(item.Tag?.ToString(), out int tagVal) && tagVal == cfg.ResolutionScalePercent)
            {
                ScaleComboBox.SelectedItem = item;
                break;
            }
        }

        // FPS
        foreach (ComboBoxItem item in FpsComboBox.Items)
        {
            if (int.TryParse(item.Tag?.ToString(), out int tagVal) && tagVal == cfg.TargetFps)
            {
                FpsComboBox.SelectedItem = item;
                break;
            }
        }

        BitrateSlider.Value = cfg.BitrateKbps;
        BitrateSliderLabel.Text = $"{cfg.BitrateKbps} kbps";
        AudioCheckBox.IsChecked = cfg.EnableAudio;

        RtspPortBox.Text = cfg.RtspPort.ToString();
        WebPortBox.Text = cfg.WebPort.ToString();
        StreamPathBox.Text = cfg.StreamPath;
        AuthRequiredCheckBox.IsChecked = cfg.RequireAuthentication;

        StartOnBootCheckBox.IsChecked = SystemIntegration.IsAutoStartEnabled();
        AutoStartStreamCheckBox.IsChecked = cfg.AutoStartStream;
        MinimizeToTrayCheckBox.IsChecked = cfg.MinimizeToTray;

        UpdateUiFromStatus(StreamService.Instance.GetStatus());
    }

    private void StreamService_StatusChanged(StreamStatusModel status)
    {
        // BeginInvoke (not Invoke): status can be raised from the MTA stream worker while the
        // UI thread is waiting for start/stop to finish — a synchronous Invoke would deadlock.
        Dispatcher.BeginInvoke(() => UpdateUiFromStatus(status));
    }

    private void MetricsTimer_Tick(object? sender, EventArgs e)
    {
        var status = StreamService.Instance.GetStatus();
        UpdateUiFromStatus(status);
    }

    private void UpdateUiFromStatus(StreamStatusModel s)
    {
        StreamUrlTextBox.Text = s.StreamUrl;

        if (s.IsStreaming)
        {
            StatusDot.Fill = (SolidColorBrush)FindResource("BrushAccent");
            StatusText.Text = "STREAMING LIVE";
            StreamToggleButton.Content = "Stop Stream";
            StreamToggleButton.Background = (SolidColorBrush)FindResource("BrushDanger");
            CaptureEngineText.Text = $"Capture: {s.CaptureMethod} | Encoder: {s.EncoderName}";
        }
        else
        {
            StatusDot.Fill = StoppedGrayBrush;
            StatusText.Text = "STOPPED";
            StreamToggleButton.Content = "Start Stream";
            StreamToggleButton.Background = (SolidColorBrush)FindResource("BrushAccent");
            CaptureEngineText.Text = "Capture: Idle";
        }

        ActiveViewersText.Text = s.ActiveViewers.ToString();
        LiveFpsText.Text = $"{s.CurrentFps:F1} FPS";
        TargetFpsSubText.Text = $"Target: {s.TargetFps} FPS ({s.ResolutionWidth}x{s.ResolutionHeight})";

        LiveBitrateText.Text = $"{s.CurrentBitrateKbps:F0} kbps";
        TargetBitrateSubText.Text = $"Target: {s.TargetBitrateKbps} kbps";

        CpuUsageText.Text = $"{s.CpuPercent:F1}%";
        string uptimeStr = $"{s.Uptime.Hours:D2}:{s.Uptime.Minutes:D2}:{s.Uptime.Seconds:D2}";
        RamUptimeSubText.Text = $"RAM: {s.RamMb:F1} MB | Uptime: {uptimeStr}";
    }

    private void StreamToggle_Click(object sender, RoutedEventArgs e)
    {
        if (StreamService.Instance.IsStreaming)
        {
            StreamService.Instance.StopStream();
        }
        else
        {
            var (started, error) = StreamService.Instance.StartStream();
            if (!started)
            {
                System.Windows.MessageBox.Show(
                    string.IsNullOrWhiteSpace(error)
                        ? "The stream could not be started. Details were written to the log; check that the RTSP port is free and that an H.264 encoder is available."
                        : error,
                    "Streaming Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(StreamUrlTextBox.Text);
            _trayManager?.ShowNotification("WinToRTSP", "RTSP URL copied to clipboard.");
        }
        catch { }
    }

    private void OpenWebUI_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var status = StreamService.Instance.GetStatus();
            Process.Start(new ProcessStartInfo(status.WebUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Could not open browser: {ex.Message}");
        }
    }

    private void Config_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        var cfg = ConfigManager.Current;

        if (ScaleComboBox.SelectedItem is ComboBoxItem scaleItem &&
            int.TryParse(scaleItem.Tag?.ToString(), out int scale))
        {
            cfg.ResolutionScalePercent = scale;
        }

        if (FpsComboBox.SelectedItem is ComboBoxItem fpsItem &&
            int.TryParse(fpsItem.Tag?.ToString(), out int fps))
        {
            cfg.TargetFps = fps;
        }

        cfg.BitrateKbps = (int)BitrateSlider.Value;
        cfg.EnableAudio = AudioCheckBox.IsChecked ?? true;

        if (int.TryParse(RtspPortBox.Text, out int rtspPort) && rtspPort > 0 && rtspPort <= 65535)
            cfg.RtspPort = rtspPort;

        if (int.TryParse(WebPortBox.Text, out int webPort) && webPort > 0 && webPort <= 65535)
            cfg.WebPort = webPort;

        if (!string.IsNullOrWhiteSpace(StreamPathBox.Text))
            cfg.StreamPath = StreamPathBox.Text.Trim();

        cfg.RequireAuthentication = AuthRequiredCheckBox.IsChecked ?? true;
        cfg.AutoStartStream = AutoStartStreamCheckBox.IsChecked ?? false;
        cfg.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked ?? true;

        ConfigManager.Save();
    }

    private void BitrateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BitrateSliderLabel != null)
        {
            BitrateSliderLabel.Text = $"{(int)BitrateSlider.Value} kbps";
        }
        Config_Changed(sender, e);
    }

    private void UpdatePassword_Click(object sender, RoutedEventArgs e)
    {
        string newPwd = NewPasswordBox.Password;
        if (string.IsNullOrEmpty(newPwd) || newPwd.Length < 12)
        {
            System.Windows.MessageBox.Show(
                "Password must be at least 12 characters in length for security compliance.",
                "Password Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = SecurityManager.SetPassword(newPwd);
        if (result.Success)
        {
            NewPasswordBox.Clear();
            System.Windows.MessageBox.Show(
                "Password successfully updated. The new credentials will apply to RTSP Digest Auth and Web UI login.",
                "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show(result.ErrorMessage, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartOnBoot_Click(object sender, RoutedEventArgs e)
    {
        bool enable = StartOnBootCheckBox.IsChecked ?? false;
        SystemIntegration.SetAutoStart(enable);
        ConfigManager.Current.StartOnBoot = enable;
        ConfigManager.Save();
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        // The combo items carry the ThemePreference name in Tag — index-based mapping
        // used to invert Dark/Light (enum order is Auto/Light/Dark, combo order differed).
        if (ThemeComboBox.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse(item.Tag?.ToString(), out ThemePreference pref))
        {
            return;
        }

        ConfigManager.Current.Theme = pref;
        ConfigManager.Save();
        ApplyTheme(pref);
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (ConfigManager.Current.Theme == ThemePreference.Auto)
        {
            // BeginInvoke: this fires on a broadcast thread and must never block on the UI.
            Dispatcher.BeginInvoke(() => ApplyTheme(ThemePreference.Auto));
        }
    }

    private void ApplyTheme(ThemePreference pref)
    {
        bool isDark = pref == ThemePreference.Auto
            ? SystemIntegration.IsWindowsDarkTheme()
            : pref == ThemePreference.Dark;

        // Frozen brushes: shared, thread-safe, no per-switch allocation. DynamicResource
        // consumers (controls, popup chrome) pick the swap up immediately.
        if (isDark)
        {
            Resources["BrushBackground"] = CreateFrozenBrush(15, 23, 42);
            Resources["BrushCard"] = CreateFrozenBrush(30, 41, 59);
            Resources["BrushCardBorder"] = CreateFrozenBrush(51, 65, 85);
            Resources["BrushTextPrimary"] = CreateFrozenBrush(248, 250, 252);
            Resources["BrushTextMuted"] = CreateFrozenBrush(148, 163, 184);
            Resources["BrushInputBg"] = CreateFrozenBrush(15, 23, 42);
            Resources["BrushInputBorder"] = CreateFrozenBrush(51, 65, 85);
            Resources["BrushBadgeBg"] = CreateFrozenBrush(51, 65, 85);
        }
        else
        {
            Resources["BrushBackground"] = CreateFrozenBrush(248, 250, 252);
            Resources["BrushCard"] = CreateFrozenBrush(255, 255, 255);
            Resources["BrushCardBorder"] = CreateFrozenBrush(226, 232, 240);
            Resources["BrushTextPrimary"] = CreateFrozenBrush(15, 23, 42);
            Resources["BrushTextMuted"] = CreateFrozenBrush(100, 116, 139);
            Resources["BrushInputBg"] = CreateFrozenBrush(241, 245, 249);
            Resources["BrushInputBorder"] = CreateFrozenBrush(203, 213, 225);
            Resources["BrushBadgeBg"] = CreateFrozenBrush(226, 232, 240);
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (ConfigManager.Current.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();

            if (!_hasShownTrayNotice)
            {
                _trayManager?.ShowNotification("WinToRTSP", "Application minimized to system tray. Double-click to restore.");
                _hasShownTrayNotice = true;
            }
        }
        else
        {
            CleanupAndShutdown();
        }
    }

    private void CleanupAndShutdown()
    {
        _metricsTimer.Stop();
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;

        StreamService.Instance.StopStream();
        _webServer?.Dispose();
        _trayManager?.Dispose();
        StreamService.Instance.Dispose();
    }
}