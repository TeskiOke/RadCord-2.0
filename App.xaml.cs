using System.IO;
using System.Windows;
using System.Windows.Media;
using RadCord.Core;
using RadCord.Views;

namespace RadCord;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Write any unhandled exception to crash.log next to the exe
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try { File.WriteAllText("crash.log", ex.ExceptionObject.ToString()); } catch { }
        };

        DispatcherUnhandledException += (_, ex) =>
        {
            try { File.WriteAllText("crash.log", ex.Exception.ToString()); } catch { }
            MessageBox.Show($"Критическая ошибка:\n{ex.Exception.Message}\n\nПодробности в crash.log",
                "RadCord", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
            Application.Current.Shutdown();
        };

        // Prevent auto-shutdown when SetupWindow closes before MainWindow opens
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Support --profile <dir> for running a second client instance
        var args = e.Args;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--profile")
            {
                AppConfig.ProfileDirectory = Path.GetFullPath(args[i + 1]);
                break;
            }
        }

        try
        {
            var config = AppConfig.Load();
            if (config == null)
            {
                var setup = new SetupWindow();
                if (setup.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
                config = AppConfig.Load();
                if (config == null)
                {
                    MessageBox.Show("Не удалось загрузить конфигурацию после настройки.", "RadCord",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown();
                    return;
                }
            }

            ApplyTheme(config);
            var main = new MainWindow(config);
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            main.Show();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText("crash.log", ex.ToString()); } catch { }
            MessageBox.Show($"Ошибка запуска:\n{ex.Message}\n\nПодробности в crash.log",
                "RadCord", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    public static void ApplyTheme(AppConfig config)
    {
        var res = Application.Current?.Resources;
        if (res == null) return;

        static void S(ResourceDictionary r, string key, Color c) => r[key] = new SolidColorBrush(c);

        try
        {
            var accent = (Color)ColorConverter.ConvertFromString(config.Profile.AppColor ?? "#5865f2");
            S(res, "AccentBrush", accent);
            S(res, "AccentHoverBrush", Color.FromRgb(
                (byte)Math.Max(0, (int)accent.R - 25),
                (byte)Math.Max(0, (int)accent.G - 25),
                (byte)Math.Max(0, (int)accent.B - 25)));
        }
        catch { }

        switch (config.Profile.Theme ?? "dark")
        {
            case "gray":   // Discord-dark, средне-тёмная
                S(res, "BgDeepBrush",        Color.FromRgb(0x1e, 0x1f, 0x22));
                S(res, "BgDarkBrush",        Color.FromRgb(0x2b, 0x2d, 0x31));
                S(res, "BgMidBrush",         Color.FromRgb(0x31, 0x33, 0x38));
                S(res, "BgLightBrush",       Color.FromRgb(0x38, 0x3a, 0x40));
                S(res, "BgInputBrush",       Color.FromRgb(0x1e, 0x1f, 0x22));
                S(res, "TextPrimaryBrush",   Color.FromRgb(0xdb, 0xde, 0xe1));
                S(res, "TextSecondaryBrush", Color.FromRgb(0xb5, 0xba, 0xc1));
                S(res, "TextMutedBrush",     Color.FromRgb(0x80, 0x84, 0x8e));
                break;
            case "light":  // белая тема
                S(res, "BgDeepBrush",        Color.FromRgb(0xe3, 0xe5, 0xe8));
                S(res, "BgDarkBrush",        Color.FromRgb(0xf2, 0xf3, 0xf5));
                S(res, "BgMidBrush",         Color.FromRgb(0xff, 0xff, 0xff));
                S(res, "BgLightBrush",       Color.FromRgb(0xd9, 0xdb, 0xde));
                S(res, "BgInputBrush",       Color.FromRgb(0xe3, 0xe5, 0xe8));
                S(res, "TextPrimaryBrush",   Color.FromRgb(0x06, 0x07, 0x07));
                S(res, "TextSecondaryBrush", Color.FromRgb(0x4e, 0x50, 0x58));
                S(res, "TextMutedBrush",     Color.FromRgb(0x74, 0x77, 0x80));
                break;
            default:       // dark — по-настоящему чёрная
                S(res, "BgDeepBrush",        Color.FromRgb(0x0d, 0x0f, 0x11));
                S(res, "BgDarkBrush",        Color.FromRgb(0x11, 0x13, 0x16));
                S(res, "BgMidBrush",         Color.FromRgb(0x17, 0x19, 0x1e));
                S(res, "BgLightBrush",       Color.FromRgb(0x20, 0x23, 0x2a));
                S(res, "BgInputBrush",       Color.FromRgb(0x0d, 0x0f, 0x11));
                S(res, "TextPrimaryBrush",   Color.FromRgb(0xdb, 0xde, 0xe1));
                S(res, "TextSecondaryBrush", Color.FromRgb(0xb5, 0xba, 0xc1));
                S(res, "TextMutedBrush",     Color.FromRgb(0x80, 0x84, 0x8e));
                break;
        }
    }
}
