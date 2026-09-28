using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using WinToRTSP.Config;
using WinToRTSP.Resources;
using WinToRTSP.Services;

namespace WinToRTSP.UI;

public class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Window _mainWindow;
    private readonly ToolStripMenuItem _startMenuItem;
    private readonly ToolStripMenuItem _stopMenuItem;

    public TrayIconManager(Window mainWindow)
    {
        _mainWindow = mainWindow;

        _notifyIcon = new NotifyIcon
        {
            Icon = IconGenerator.GetAppIcon(),
            Text = "WinToRTSP - Screen Casting",
            Visible = true
        };

        var contextMenu = new ContextMenuStrip();

        var showMenuItem = new ToolStripMenuItem("Show WinToRTSP", null, (s, e) => ShowWindow());
        showMenuItem.Font = new Font(showMenuItem.Font, System.Drawing.FontStyle.Bold);
        contextMenu.Items.Add(showMenuItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        _startMenuItem = new ToolStripMenuItem("Start Stream", null, (s, e) => StreamService.Instance.StartStream());
        _stopMenuItem = new ToolStripMenuItem("Stop Stream", null, (s, e) => StreamService.Instance.StopStream());
        contextMenu.Items.Add(_startMenuItem);
        contextMenu.Items.Add(_stopMenuItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var webMenuItem = new ToolStripMenuItem("Open Web Dashboard", null, (s, e) =>
        {
            try
            {
                var status = StreamService.Instance.GetStatus();
                Process.Start(new ProcessStartInfo(status.WebUrl) { UseShellExecute = true });
            }
            catch { }
        });
        contextMenu.Items.Add(webMenuItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitMenuItem = new ToolStripMenuItem("Exit", null, (s, e) =>
        {
            _notifyIcon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        });
        contextMenu.Items.Add(exitMenuItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) => ShowWindow();

        StreamService.Instance.StatusChanged += OnStatusChanged;
        UpdateMenuItems(StreamService.Instance.IsStreaming);
    }

    private void OnStatusChanged(StreamStatusModel status)
    {
        // BeginInvoke: StatusChanged may fire from the MTA stream worker thread while the UI
        // thread waits for start/stop; a synchronous Invoke would deadlock.
        _mainWindow.Dispatcher.BeginInvoke(() =>
        {
            UpdateMenuItems(status.IsStreaming);
        });
    }

    private void UpdateMenuItems(bool isStreaming)
    {
        _startMenuItem.Enabled = !isStreaming;
        _stopMenuItem.Enabled = isStreaming;
        _notifyIcon.Text = isStreaming
            ? "WinToRTSP - Streaming Active"
            : "WinToRTSP - Stopped";
    }

    public void ShowWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void ShowNotification(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3000, title, text, icon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
