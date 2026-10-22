namespace PipeDream;

/// <summary>
/// The project's UberASM (reference/UBERASM_PORT.md): UberASM Tool's whole input as data in the .pdp, so sharing the file shares
/// the code. Files keep UberASM Tool's own paths ("level/waves.asm", "library/math.asm", "other/global_code.asm"), so a folder
/// reads in one to one; the list is <c>list.txt</c> read into entries. Nothing here assembles anything.
/// </summary>
public sealed class UberState
{
    /// <summary>Every file of the folder but the tool's own (<c>asm/</c>, the program): resources and what they include under
    /// level/, gamemode/ and overworld/, library/, routines/, other/. Path → bytes (text is UTF-8).</summary>
    public Dictionary<string, byte[]> Files { get; set; } = new();

    /// <summary>The list's sections: which resources run where, in list order.</summary>
    public List<UberEntry> Levels { get; set; } = new();
    public List<UberEntry> GameModes { get; set; } = new();
    public List<UberEntry> Overworlds { get; set; } = new();

    /// <summary>The list's <c>global:</c>, <c>statusbar:</c> and <c>macrolib:</c> files (paths in <see cref="Files"/>).</summary>
    public string Global { get; set; } = "other/global_code.asm";
    public string StatusBar { get; set; } = "other/status_code.asm";
    public string MacroLib { get; set; } = "other/macro_library.asm";

    /// <summary>The list's <c>freeram:</c>: two bytes for the previous game mode (1.x's <c>sprite:</c> is read as this).</summary>
    public int FreeRam { get; set; } = 0x7FAC80;

    public bool IsEmpty => Files.Count == 0 && Levels.Count == 0 && GameModes.Count == 0 && Overworlds.Count == 0;
}

/// <summary>One list line: a number (or <see cref="All"/> for <c>*</c>) and the resources it runs, in order.</summary>
public sealed class UberEntry
{
    public const int All = -1;
    public int Number { get; set; }
    public List<UberCall> Calls { get; set; } = new();
}

/// <summary>A resource on a list line: its path in <see cref="UberState.Files"/>, the extra bytes given to it, or a skip of a
/// <c>*</c> resource (<c>-name</c>).</summary>
public sealed class UberCall
{
    public string File { get; set; } = "";
    public bool Skip { get; set; }
    public byte[] Bytes { get; set; } = [];
}
