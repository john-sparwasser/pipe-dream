using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PipeDream.Ui;

public partial class App : Application
{
    public override void Initialize()
    {
        // SNES art is pixel art: never resample it. Render options merge down the visual tree,
        // so setting it on every window as it loads covers every Image and every DrawImage in
        // the custom canvases without touching them one by one. It has to be code: the option
        // is a struct, which AXAML cannot construct, and there is no styleable property field.
        Control.LoadedEvent.AddClassHandler<Window>(
            (w, _) => RenderOptions.SetBitmapInterpolationMode(w, BitmapInterpolationMode.None));
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Headless tests boot the same App with no desktop lifetime, so the window is only
        // created when there actually is one to show.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            InstallFileProblemNet(p => (desktop.MainWindow as MainWindow)?.ShowProblem(p));
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The last line of defence for a file that could not be read or written somewhere nobody
    /// guarded: an exception that escapes an event handler ends the process, and the user's
    /// unsaved work with it. Only file problems are caught — anything else is a bug and stays
    /// loud, because a swallowed bug is one nobody fixes.
    /// </summary>
    internal static void InstallFileProblemNet(Action<FileProblem> show)
        => Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (!FileProblem.IsFile(e.Exception)) return;
            e.Handled = true;
            show(FileProblem.From(e.Exception, "finish the last action"));
        };
}
