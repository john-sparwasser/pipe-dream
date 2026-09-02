namespace PipeDream;

/// <summary>
/// A file the editor could not read or write, as something a person can act on: what was being
/// done, which file, why the system refused, and what to try. The OS's own message names the
/// path and little else, and a save that fails needs to say what to do next — every gateway that
/// touches a file routes its failure through here, and the one that nobody guarded reaches it
/// through the dispatcher's safety net in App.
/// </summary>
public sealed record FileProblem(string Doing, string? Path, string Why, string Next)
{
    public string Title => $"Could not {Doing}";

    /// <summary>The dialog body: the file, the reason, the way forward.</summary>
    public string Message => (Path is null ? "" : Path + "\n\n") + Why + "\n\n" + Next;

    /// <summary>The status-line form.</summary>
    public string OneLine => $"could not {Doing}: {Why.TrimEnd('.')}" + (Path is null ? "" : $" — {Path}");

    /// <summary>The exceptions the file system throws for things a user can fix: a file that moved,
    /// a folder that is read-only, a disk that is full or unplugged, a file another program holds.
    /// Anything else is a bug and must stay loud.</summary>
    public static bool IsFile(Exception e)
        => e is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    public static FileProblem From(Exception e, string doing, string? path = null)
    {
        path ??= e is FileNotFoundException { FileName: { } named } ? named : PathIn(e.Message);
        var (why, next) = e switch
        {
            FileNotFoundException or DirectoryNotFoundException => (
                "The file or folder is not where it was.",
                "It may have been moved, renamed or deleted, or be on a drive or network share that is "
                + "disconnected. Find it and open it from its new place, or open a recent project from "
                + "the File menu."),
            DriveNotFoundException => (
                "The drive is not available.",
                "Reconnect the drive or network share and try again."),
            PathTooLongException => (
                "The path is too long for this system.",
                "Move the folder somewhere with a shorter path and open it from there."),
            UnauthorizedAccessException or System.Security.SecurityException => (
                "Permission was denied.",
                "The file or its folder may be read-only, or belong to another user. Check its "
                + "permissions — on Windows, right-click ▸ Properties ▸ Read-only — or copy it to a "
                + "folder you can write to, such as Documents, and open it from there."),
            _ => (
                "The file could not be read or written.",
                "Another program may have it open — an emulator running the built ROM, or a second "
                + "copy of pipe-dream — or the disk may be full or disconnected. Close whatever else is "
                + "using it, check the free space, and try again."),
        };
        return new(doing, path, why, next);
    }

    /// <summary>The path an OS message quotes, when it quotes one: "Access to the path '/x' is denied."</summary>
    private static string? PathIn(string message)
    {
        int a = message.IndexOf('\''), b = a < 0 ? -1 : message.IndexOf('\'', a + 1);
        return b > a + 1 ? message[(a + 1)..b] : null;
    }
}
