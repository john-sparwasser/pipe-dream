using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PipeDream.Ui;
using Xunit;

namespace PipeDream.Tests;

/// <summary>
/// A file the editor cannot read or write must reach the user as something to act on — which
/// file, why, what to try — never as a crash and never as only a line in the corner. Three
/// layers, each pinned: the description, the session's guard, and the dispatcher's safety net.
/// </summary>
public class FileProblemTests
{
    private static string Vanilla => Path.Combine(
        Environment.GetEnvironmentVariable("PIPEDREAM_SMW_ROOT") ?? @"C:\SMW\Projects", ".resources", "SMW.smc");

    [Fact]
    public void each_kind_of_failure_names_the_file_and_a_way_forward()
    {
        var missing = FileProblem.From(new FileNotFoundException("gone", "/p/project.pdp"), "open the project");
        Assert.Equal("Could not open the project", missing.Title);
        Assert.Equal("/p/project.pdp", missing.Path);
        Assert.Contains("moved", missing.Next);
        Assert.Contains("recent project", missing.Next);

        // The path lifted out of the OS message when the exception does not carry one.
        var denied = FileProblem.From(new UnauthorizedAccessException("Access to the path '/p/project.pdp' is denied."), "save the project");
        Assert.Equal("/p/project.pdp", denied.Path);
        Assert.Contains("read-only", denied.Next);

        var held = FileProblem.From(new IOException("sharing violation"), "build the ROM", "/p/build/p.smc");
        Assert.Contains("emulator", held.Next);
        Assert.StartsWith("could not build the ROM:", held.OneLine);
        Assert.Contains("/p/build/p.smc", held.Message);

        Assert.True(FileProblem.IsFile(new DirectoryNotFoundException()));
        Assert.False(FileProblem.IsFile(new InvalidOperationException()));      // a bug stays loud
    }

    [Fact]
    public void opening_a_project_that_is_not_there_is_reported_not_thrown()
    {
        var s = new EditorSession();
        FileProblem? shown = null;
        s.Problem += (_, p) => shown = p;
        string gone = Path.Combine(Path.GetTempPath(), "pd-nope-" + Guid.NewGuid().ToString("N")[..8], "project.pdp");

        Assert.False(s.OpenProject(gone));

        Assert.NotNull(shown);
        Assert.Equal("Could not open the project", shown!.Title);
        Assert.Equal(gone, shown.Path);
        Assert.StartsWith("could not open the project", s.Status);
    }

    /// <summary>The safety net: a file exception that escapes every guard is shown, not fatal —
    /// and only a FILE exception; anything else still surfaces as the bug it is.</summary>
    [AvaloniaFact]
    public void a_file_exception_nobody_guarded_becomes_a_problem_instead_of_a_crash()
    {
        FileProblem? shown = null;
        App.InstallFileProblemNet(p => shown = p);

        Dispatcher.UIThread.Post(() => throw new UnauthorizedAccessException("Access to the path '/x/y.smc' is denied."));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(shown);
        Assert.Equal("/x/y.smc", shown!.Path);
        Assert.Equal("Could not finish the last action", shown.Title);
    }

    /// <summary>Ctrl+S into a folder that cannot be written — the case that used to end the
    /// process. A project needs a real ROM, so this skips where the suite has none; and it needs
    /// a read-only FOLDER, which on Windows is an ACL rather than an attribute, so it is Unix-only.</summary>
    [RealRomFact]
    public void saving_into_a_read_only_folder_is_reported_not_thrown()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = Path.Combine(Path.GetTempPath(), "pd-ro-" + Guid.NewGuid().ToString("N")[..8]);
        string proj = Path.Combine(dir, "proj");
        var rw = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        try
        {
            var s = new EditorSession();
            Assert.True(s.NewProject(proj, Vanilla), s.Status);
            FileProblem? shown = null;
            s.Problem += (_, p) => shown = p;

            File.SetUnixFileMode(proj, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try { s.Save(); }
            finally { File.SetUnixFileMode(proj, rw); }

            Assert.NotNull(shown);
            Assert.Equal("Could not save the project", shown!.Title);
            Assert.Contains("Permission", shown.Why);
            Assert.Contains("read-only", shown.Next);
            Assert.True(s.HasUnsavedWork, "a failed save must not pretend the work is on disk");
        }
        finally { try { File.SetUnixFileMode(proj, rw); Directory.Delete(dir, true); } catch { } }
    }
}
