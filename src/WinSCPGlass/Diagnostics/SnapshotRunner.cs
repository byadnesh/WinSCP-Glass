#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinScpGlass.Models;
using WinScpGlass.ViewModels;

namespace WinScpGlass.Debugging;

/// <summary>
/// Debug-build helper for visual checks: drives the UI through a few steps and saves PNG snapshots.
/// WINSCP_GLASS_SNAPSHOT_DIR=folder  WINSCP_GLASS_STEPS="shot:a.png,light,confirm,shot:b.png,quit"
/// </summary>
internal static class SnapshotRunner
{
    public static async void RunIfRequested(Window window)
    {
        var folder = Environment.GetEnvironmentVariable("WINSCP_GLASS_SNAPSHOT_DIR");
        var script = Environment.GetEnvironmentVariable("WINSCP_GLASS_STEPS");
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(script)) return;
        Directory.CreateDirectory(folder);

        await Task.Delay(1200);
        foreach (var raw in script.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(':', 2);
            var arg = parts.Length > 1 ? parts[1] : "";
            try
            {
                await RunStep(window, parts[0], arg, folder);
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(folder, "errors.txt"), $"{raw}: {ex}\n");
            }
        }
    }

    private static async Task RunStep(Window window, string step, string arg, string folder)
    {
        var shell = App.Shell;
        switch (step)
        {
            case "wait":
                await Task.Delay(int.Parse(arg));
                break;
            case "size":
                var wh = arg.Split('x');
                window.Width = double.Parse(wh[0]);
                window.Height = double.Parse(wh[1]);
                await Task.Delay(400);
                break;
            case "light":
                shell.IsDark = false;
                await Task.Delay(500);
                break;
            case "dark":
                shell.IsDark = true;
                await Task.Delay(500);
                break;
            case "advanced":
                shell.Connect.ShowAdvanced = true;
                await Task.Delay(300);
                break;
            case "confirm":
                _ = Dialogs.ConfirmAsync("Delete 3 items?", "3 items will be permanently deleted from the server. This can't be undone.", "Delete", danger: true, glyph: "");
                await Task.Delay(700);
                break;
            case "prompt":
                _ = Dialogs.PromptAsync("New folder", "Create a folder in /home/demo on the server.", "Create", "New folder", glyph: "");
                await Task.Delay(700);
                break;
            case "toast":
                shell.Notify("3 transfers complete", Tone.Success);
                await Task.Delay(700);
                break;
            case "close":
                shell.ActiveDialog?.Cancel();
                await Task.Delay(400);
                break;
            case "connect":
                // host;user;password
                var c = arg.Split(';');
                shell.Connect.NewSiteCommand.Execute(null);
                shell.Connect.Protocol = ProtocolKind.Sftp;
                shell.Connect.Host = c[0];
                shell.Connect.User = c[1];
                shell.Connect.Password = c[2];
                if (c.Length > 3) shell.Connect.LocalDirectory = c[3];
                _ = shell.Connect.ConnectCommand.ExecuteAsync(null);
                await Task.Delay(300);
                break;
            case "accept":
                for (int i = 0; i < 150 && shell.ActiveDialog == null; i++) await Task.Delay(100);
                await Task.Delay(500);
                shell.ActiveDialog?.Accept();
                break;
            case "until-session":
                for (int i = 0; i < 300 && (shell.Session == null || shell.Session.Remote.Items.Count == 0); i++) await Task.Delay(100);
                await Task.Delay(1200);
                break;
            case "select-remote":
                if (shell.Session != null)
                {
                    shell.Session.ActivePanel = shell.Session.Remote;
                }
                await Task.Delay(300);
                break;
            case "connect-ui":
                // host;user;password — password goes through the visible PasswordBox, not the view model
                var u = arg.Split(';');
                shell.Connect.NewSiteCommand.Execute(null);
                shell.Connect.Protocol = ProtocolKind.Sftp;
                shell.Connect.Host = u[0];
                shell.Connect.User = u[1];
                await Task.Delay(300);
                var box = FindAll<System.Windows.Controls.PasswordBox>(window).First(b => b.IsVisible);
                box.Password = u[2];
                await Task.Delay(200);
                File.AppendAllText(Path.Combine(folder, "log.txt"), $"view model password length after typing: {shell.Connect.Password.Length}{Environment.NewLine}");
                _ = shell.Connect.ConnectCommand.ExecuteAsync(null);
                await Task.Delay(300);
                break;
            case "disconnect":
                if (shell.Session != null) await shell.Session.DisconnectCommand.ExecuteAsync(null);
                await Task.Delay(1000);
                break;
            case "navigate-remote":
                if (shell.Session != null) await shell.Session.Remote.NavigateAsync(arg);
                await Task.Delay(800);
                break;
            case "download-first":
                if (shell.Session is { } s2)
                {
                    var files = s2.Remote.Items.Where(i => !i.IsDirectory).Take(int.Parse(arg)).ToList();
                    s2.Remote.UpdateSelection(files);
                    s2.DownloadCommand.Execute(null);
                }
                await Task.Delay(300);
                break;
            case "select-site":
                shell.Connect.SelectedSite = shell.Connect.Sites.First(x => x.DisplayName == arg);
                await Task.Delay(400);
                break;
            case "download":
                if (shell.Session is { } session)
                {
                    var entry = session.Remote.Items.First(i => i.Name == arg);
                    session.Remote.UpdateSelection(new[] { entry });
                    session.DownloadCommand.Execute(null);
                }
                await Task.Delay(300);
                break;
            case "until-idle":
                for (int i = 0; i < 300 && shell.Session?.Queue.IsBusy == true; i++) await Task.Delay(100);
                await Task.Delay(600);
                break;
            case "select-row":
                // remote panel grid is the last DataGrid in the tree
                var grids = FindAll<System.Windows.Controls.DataGrid>(window).ToList();
                var grid = grids.Last();
                grid.Focus();
                grid.SelectedItem = grid.Items.Cast<FileEntry>().First(i => i.Name == arg);
                await Task.Delay(400);
                break;
            case "logo":
                foreach (int px in new[] { 256, 128, 64, 48, 32, 24, 16 })
                {
                    SaveLogo(Path.Combine(folder, $"logo-{px}.png"), px);
                }
                break;
            case "shot":
                Capture(window, Path.Combine(folder, arg));
                break;
            case "quit":
                Application.Current.Shutdown();
                break;
        }
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    // The app mark: accent gradient squircle, top gloss, light rim and the swap glyph.
    private static void SaveLogo(string file, int px)
    {
        double size = px, radius = px * 0.3;
        var gradient = new LinearGradientBrush(Color.FromRgb(0x6F, 0x8D, 0xFF), Color.FromRgb(0xA1, 0x6B, 0xFF), 45);
        var gloss = new LinearGradientBrush(Color.FromArgb(0x60, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
        var grid = new System.Windows.Controls.Grid { Width = size, Height = size };
        grid.Children.Add(new System.Windows.Controls.Border { CornerRadius = new CornerRadius(radius), Background = gradient });
        grid.Children.Add(new System.Windows.Controls.Border
        {
            CornerRadius = new CornerRadius(radius), Background = gloss,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), BorderThickness = new Thickness(Math.Max(1, px / 64.0)),
        });
        grid.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = px * 0.5,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        grid.Measure(new Size(size, size));
        grid.Arrange(new Rect(0, 0, size, size));
        grid.UpdateLayout();
        var bitmap = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(grid);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    private static void Capture(Window window, string file)
    {
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var size = new Size(root.ActualWidth, root.ActualHeight);
        var bitmap = new RenderTargetBitmap((int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        // Stand-in for the blurred desktop the OS acrylic would show through the glass.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var backdrop = new LinearGradientBrush(
                App.Shell.IsDark ? Color.FromRgb(38, 42, 62) : Color.FromRgb(214, 220, 234),
                App.Shell.IsDark ? Color.FromRgb(24, 30, 44) : Color.FromRgb(232, 226, 240), 35);
            dc.DrawRectangle(backdrop, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(size));
        }
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
#endif
