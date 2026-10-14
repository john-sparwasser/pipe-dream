using System.Runtime.InteropServices;

namespace PipeDream;

/// <summary>
/// Asar, the SNES assembler GPS and most SMW tools run their patches through, loaded as a native
/// library (asar.dll / libasar.so / libasar.dylib, built from the pinned ~/asar, API 3.3). Only
/// the handful of calls a block insert needs are bound, from asardll.h's documented interface;
/// upstream's asar.cs is not vendored.
///
/// Asar's state is process-global, so every patch runs behind one lock. Set PIPEDREAM_ASAR to
/// the library's path in development; otherwise it is looked for beside the app.
/// </summary>
public static class Asar
{
    /// <summary>What a patch did: Ok is false when Asar reported errors, and then the ROM is left
    /// unchanged. Errors and warnings read "file:line: message", as Asar prints them.</summary>
    public sealed record Result(bool Ok, string[] Errors, string[] Warnings, string[] Prints, WrittenBlock[] Blocks,
                                IReadOnlyDictionary<string, int> Labels);
    public readonly record struct WrittenBlock(int Snes, int Length);

    private const string Lib = "asar";
    private const int ApiVersion = 303;
    private const int MaxRomSize = 16 * 1024 * 1024;
    private static readonly object gate = new();
    private static bool? available;

    static Asar() => NativeLibrary.SetDllImportResolver(typeof(Asar).Assembly, (name, asm, path) =>
        name == Lib && Environment.GetEnvironmentVariable("PIPEDREAM_ASAR") is { Length: > 0 } p
            ? NativeLibrary.Load(p)
            : NativeLibrary.TryLoad(name, asm, path, out var h) ? h : IntPtr.Zero);

    /// <summary>The library loads and speaks API 3.x at 3.3 or later — the check gps.exe makes.</summary>
    public static bool Available
    {
        get
        {
            lock (gate)
                return available ??= Probe();
        }
    }

    private static bool Probe()
    {
        try
        {
            int v = asar_apiversion();
            return v >= ApiVersion && v / 100 == ApiVersion / 100 && asar_init();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>Assemble <paramref name="asmPath"/> onto <paramref name="rom"/> in place. The ROM
    /// may grow (Asar expands it for freespace); a copier header is kept. <paramref name="files"/>
    /// are files that exist only in memory, by absolute path: Asar finds them before the disk, and
    /// asmPath may be one of them.</summary>
    public static unsafe Result Patch(Rom rom, string asmPath, IReadOnlyDictionary<string, byte[]>? files = null)
    {
        if (!Available) throw new InvalidOperationException("Asar library not found (set PIPEDREAM_ASAR)");
        lock (gate)
        {
            int len = rom.Data.Length - rom.HeaderOffset;
            var buf = new byte[MaxRomSize];
            Array.Copy(rom.Data, rom.HeaderOffset, buf, 0, len);
            var path = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(asmPath));
            var mem = (files ?? new Dictionary<string, byte[]>()).Select(f => (
                Path: Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(f.Key)), Pin: GCHandle.Alloc(f.Value, GCHandleType.Pinned), f.Value.Length)).ToArray();
            try
            {
                bool ok;
                var memFiles = mem.Select(m => new MemoryFile { Path = m.Path, Buffer = m.Pin.AddrOfPinnedObject(), Length = m.Length }).ToArray();
                fixed (byte* b = buf)
                fixed (MemoryFile* mf = memFiles)
                {
                    var p = new PatchParams
                    {
                        StructSize = sizeof(PatchParams), PatchLoc = path, RomData = (IntPtr)b,
                        BufLen = MaxRomSize, RomLen = (IntPtr)(&len), ShouldReset = 1,
                        MemoryFiles = (IntPtr)mf, MemoryFileCount = memFiles.Length,
                    };
                    ok = asar_patch_ex(ref p);
                }
                var result = new Result(ok, Messages(asar_geterrors), Messages(asar_getwarnings), Prints(), Blocks(), Labels());
                if (ok)
                {
                    var data = new byte[rom.HeaderOffset + len];
                    Array.Copy(rom.Data, data, rom.HeaderOffset);
                    Array.Copy(buf, 0, data, rom.HeaderOffset, len);
                    rom.Data = data;
                }
                return result;
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
                foreach (var m in mem) { Marshal.FreeCoTaskMem(m.Path); m.Pin.Free(); }
            }
        }
    }

    private delegate IntPtr ListFn(out int count);

    private static unsafe string[] Messages(ListFn fn)
    {
        var p = (ErrorData*)fn(out int n);
        return Enumerable.Range(0, n).Select(i => Marshal.PtrToStringUTF8(p[i].FullErrData) ?? "").ToArray();
    }

    private static unsafe string[] Prints()
    {
        var p = (IntPtr*)asar_getprints(out int n);
        return Enumerable.Range(0, n).Select(i => Marshal.PtrToStringUTF8(p[i]) ?? "").ToArray();
    }

    private static unsafe WrittenBlock[] Blocks()
    {
        var p = (WrittenBlockData*)asar_getwrittenblocks(out int n);
        return Enumerable.Range(0, n).Select(i => new WrittenBlock(p[i].SnesOffset, p[i].NumBytes)).ToArray();
    }

    private static unsafe Dictionary<string, int> Labels()
    {
        var p = (LabelData*)asar_getalllabels(out int n);
        var labels = new Dictionary<string, int>();
        for (int i = 0; i < n; i++) labels[Marshal.PtrToStringUTF8(p[i].Name) ?? ""] = p[i].Location;
        return labels;
    }

    // asardll.h's structs, field for field. bools are one byte (C99 bool).
    [StructLayout(LayoutKind.Sequential)]
    private struct PatchParams
    {
        public int StructSize;
        public IntPtr PatchLoc, RomData;
        public int BufLen;
        public IntPtr RomLen, IncludePaths;
        public int NumIncludePaths;
        public byte ShouldReset;
        public IntPtr AdditionalDefines;
        public int AdditionalDefineCount;
        public IntPtr StdIncludesFile, StdDefinesFile, WarningSettings;
        public int WarningSettingCount;
        public IntPtr MemoryFiles;
        public int MemoryFileCount;
        public byte OverrideChecksumGen, GenerateChecksum;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ErrorData
    {
        public IntPtr FullErrData, RawErrData, Block, FileName;
        public int Line;
        public IntPtr CallerFileName;
        public int CallerLine, ErrId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LabelData { public IntPtr Name; public int Location; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryFile { public IntPtr Path, Buffer; public nint Length; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WrittenBlockData { public int PcOffset, SnesOffset, NumBytes; }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int asar_apiversion();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool asar_init();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool asar_patch_ex(ref PatchParams p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr asar_geterrors(out int count);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr asar_getwarnings(out int count);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr asar_getprints(out int count);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr asar_getwrittenblocks(out int count);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr asar_getalllabels(out int count);
}
