using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace WinToRTSP.Services;

public static class SystemIntegration
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WinToRTSP";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            if (key != null)
            {
                var val = key.GetValue(AppName);
                return val != null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[INTEGRATION] Registry read error: {ex.Message}");
        }
        return false;
    }

    public static bool SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null) return false;

            if (enable)
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName
                                 ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinToRTSP.exe");

                // Launch with --minimized if autostarting
                key.SetValue(AppName, $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[INTEGRATION] Registry write error: {ex.Message}");
            return false;
        }
    }

    public static bool IsWindowsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                object? val = key.GetValue("AppsUseLightTheme");
                if (val is int intVal)
                {
                    return intVal == 0; // 0 = Dark, 1 = Light
                }
            }
        }
        catch { }

        return true; // Default to dark theme for modern apps
    }
}
