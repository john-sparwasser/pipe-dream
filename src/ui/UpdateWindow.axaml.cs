using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace PipeDream.Ui;

/// <summary>
/// "Version X is out — install it?" Three answers: install now, not now, or never for this
/// version. Everything it does goes through <see cref="EditorSession"/>, which is what keeps
/// the download and the installer launch out of the presentation layer.
///
/// <see cref="Restarting"/> tells the caller to shut the app down: on Windows the installer
/// cannot replace a running exe, so closing is part of installing rather than an afterthought.
/// </summary>
public partial class UpdateWindow : Window
{
    /// <summary>True when an install is under way and the editor must now exit.</summary>
    public bool Restarting { get; private set; }

    private readonly EditorSession? session;
    private readonly UpdateInfo? update;
    private CancellationTokenSource? cancel;

    public UpdateWindow() => AvaloniaXamlLoader.Load(this);

    public UpdateWindow(EditorSession session, UpdateInfo update) : this()
    {
        this.session = session;
        this.update = update;

        this.GetControl<TextBlock>("Headline").Text = $"Pipe Dream {update.Display} is available";
        this.GetControl<TextBlock>("Detail").Text =
            $"You have {session.CurrentVersion}. "
            + (update.Size > 0 ? $"The download is {update.Size / 1048576} MB. " : "")
            + (OperatingSystem.IsWindows()
                ? "Pipe Dream will close, update itself, and reopen. Your projects and settings "
                + "are not touched."
                : "Pipe Dream will replace itself and reopen. Your projects and settings are not "
                + "touched.");

        if (!string.IsNullOrWhiteSpace(update.Notes))
        {
            this.GetControl<TextBlock>("Notes").Text = update.Notes!.Trim();
            this.GetControl<ScrollViewer>("NotesBox").IsVisible = true;
        }
    }

    /// <summary>Downloading is cancelled by closing the window, so a slow link never traps
    /// someone in a dialog they cannot dismiss.</summary>
    protected override void OnClosed(EventArgs e)
    {
        cancel?.Cancel();
        base.OnClosed(e);
    }

    private void OnSkip(object? sender, RoutedEventArgs e)
    {
        if (session is not null && update is not null) session.SkipUpdate(update);
        Close();
    }

    private void OnLater(object? sender, RoutedEventArgs e) => Close();

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (session is null || update is null) return;

        var bar = this.GetControl<ProgressBar>("Bar");
        var detail = this.GetControl<TextBlock>("Detail");
        foreach (string name in new[] { "InstallButton", "SkipButton", "LaterButton" })
            this.GetControl<Button>(name).IsEnabled = false;
        bar.IsVisible = true;
        detail.Text = "Downloading…";

        cancel = new CancellationTokenSource();
        var progress = new Progress<double>(f => bar.Value = f);
        try
        {
            string file = await session.DownloadUpdate(update, progress, cancel.Token);
            detail.Text = "Starting the installer…";

            if (session.ApplyUpdate(file) is { } problem)
            {
                // Failing to install is worth saying out loud — unlike a failed check, the user
                // asked for this one and is waiting on it.
                detail.Text = "Update failed: " + problem;
                bar.IsVisible = false;
                this.GetControl<Button>("LaterButton").Content = "Close";
                this.GetControl<Button>("LaterButton").IsEnabled = true;
                return;
            }

            Restarting = true;
            Close();
        }
        catch (OperationCanceledException) { /* window closed mid-download */ }
        catch (Exception ex)
        {
            detail.Text = "Download failed: " + ex.Message;
            bar.IsVisible = false;
            this.GetControl<Button>("LaterButton").Content = "Close";
            this.GetControl<Button>("LaterButton").IsEnabled = true;
        }
    }

    /// <summary>
    /// Show the prompt for an update the caller already found. True means an install started
    /// and the editor is on its way out — on Windows the installer is blocked until it is.
    /// </summary>
    public static async Task<bool> Prompt(Window owner, EditorSession session, UpdateInfo found)
    {
        var dialog = new UpdateWindow(session, found);
        await dialog.ShowDialog(owner);
        if (!dialog.Restarting) return false;

        // Shut down through the lifetime rather than Environment.Exit, so the editor's own
        // closing path still runs and an unsaved-changes prompt is not skipped.
        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime life)
                life.Shutdown();
        });
        return true;
    }
}
