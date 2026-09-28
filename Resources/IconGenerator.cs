using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace WinToRTSP.Resources;

public static class IconGenerator
{
    public static void EnsureIconExists()
    {
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string iconPath = Path.Combine(dir, "app.ico");
        if (File.Exists(iconPath)) return;

        try
        {
            using var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using var brushBg = new LinearGradientBrush(
                    new Point(0, 0),
                    new Point(64, 64),
                    Color.FromArgb(255, 31, 111, 235),
                    Color.FromArgb(255, 35, 134, 54));

                g.FillEllipse(brushBg, 2, 2, 60, 60);

                using var brushWhite = new SolidBrush(Color.White);
                Point[] points = {
                    new Point(24, 18),
                    new Point(46, 32),
                    new Point(24, 46)
                };
                g.FillPolygon(brushWhite, points);
            }

            IntPtr hIcon = bmp.GetHicon();
            using var icon = Icon.FromHandle(hIcon);
            using var fs = new FileStream(iconPath, FileMode.Create);
            icon.Save(fs);
        }
        catch { }
    }

    public static Icon GetAppIcon()
    {
        EnsureIconExists();
        string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "app.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                return new Icon(iconPath);
            }
            catch { }
        }
        return SystemIcons.Application;
    }
}
