using System.Runtime.InteropServices;
using static SDL3.SDL;

namespace PipeDream;

/// <summary>
/// Native SDL3 file/folder pickers. SDL may invoke the result callback on a non-main
/// thread, so results are queued and delivered by Pump() from the UI thread (called once
/// per frame in EditorApp.Update). One dialog in flight at a time — calls while one is
/// open are ignored (guard with Busy). Filter strings live in unmanaged memory because
/// SDL requires them valid until the callback fires, not just for the Show call.
/// </summary>
internal static unsafe class FileDialog
{
    private static readonly object gate = new();
    private static Action<string?>? pending;           // completion for the in-flight dialog
    private static string? result;
    private static bool resultReady;
    private static SDL_DialogFileCallback? keepAlive;  // GC root until the callback fires
    private static IntPtr filterMem, nameMem, patternMem;

    internal static bool Busy { get { lock (gate) return pending is not null; } }

    /// <summary>Open-file picker. <paramref name="patterns"/> is SDL syntax: extensions
    /// without dots, semicolon-separated (e.g. "smc;sfc"). onDone gets null on cancel.</summary>
    internal static void OpenFile(string filterLabel, string patterns, IntPtr window, Action<string?> onDone)
        => Show(save: false, folder: false, filterLabel, patterns, window, onDone);

    internal static void SaveFile(string filterLabel, string patterns, IntPtr window, Action<string?> onDone)
        => Show(save: true, folder: false, filterLabel, patterns, window, onDone);

    internal static void OpenFolder(IntPtr window, Action<string?> onDone)
        => Show(save: false, folder: true, null, null, window, onDone);

    private static void Show(bool save, bool folder, string? filterLabel, string? patterns,
                             IntPtr window, Action<string?> onDone)
    {
        lock (gate)
        {
            if (pending is not null) return;
            pending = onDone;
            resultReady = false;
        }
        keepAlive = OnDialogDone;
        if (folder)
        {
            SDL_ShowOpenFolderDialog(keepAlive, IntPtr.Zero, window, null!, false);
            return;
        }
        nameMem = Marshal.StringToCoTaskMemUTF8(filterLabel);
        patternMem = Marshal.StringToCoTaskMemUTF8(patterns);
        filterMem = Marshal.AllocHGlobal(sizeof(SDL_DialogFileFilter));
        var f = (SDL_DialogFileFilter*)filterMem;
        f->name = (byte*)nameMem;
        f->pattern = (byte*)patternMem;
        var filters = new Span<SDL_DialogFileFilter>(f, 1);
        // The binding declares default_location non-nullable; SDL itself accepts NULL.
        if (save) SDL_ShowSaveFileDialog(keepAlive, IntPtr.Zero, window, filters, 1, null!);
        else SDL_ShowOpenFileDialog(keepAlive, IntPtr.Zero, window, filters, 1, null!, false);
    }

    /// <summary>SDL's error text when the last dialog failed (null list); null otherwise.</summary>
    internal static string? LastError { get; private set; }

    // May run on any thread — only stash the result; delivery happens in Pump().
    private static void OnDialogDone(IntPtr userdata, IntPtr fileList, int filterIndex)
    {
        string? picked = null;
        string? err = null;
        if (fileList != IntPtr.Zero)                      // null list = dialog error
        {
            IntPtr first = Marshal.ReadIntPtr(fileList);  // null first entry = cancelled
            if (first != IntPtr.Zero) picked = Marshal.PtrToStringUTF8(first);
        }
        else err = SDL_GetError();
        lock (gate) { result = picked; LastError = err; resultReady = true; }
    }

    /// <summary>Deliver a finished dialog's result on the UI thread. Call once per frame.</summary>
    internal static void Pump()
    {
        Action<string?>? done;
        string? r;
        lock (gate)
        {
            if (pending is null || !resultReady) return;
            done = pending; r = result;
            pending = null; result = null; resultReady = false;
        }
        if (filterMem != IntPtr.Zero) { Marshal.FreeHGlobal(filterMem); filterMem = IntPtr.Zero; }
        if (nameMem != IntPtr.Zero) { Marshal.FreeCoTaskMem(nameMem); nameMem = IntPtr.Zero; }
        if (patternMem != IntPtr.Zero) { Marshal.FreeCoTaskMem(patternMem); patternMem = IntPtr.Zero; }
        keepAlive = null;
        done(r);
    }
}
