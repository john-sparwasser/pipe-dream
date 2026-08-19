namespace PipeDream;

/// <summary>
/// Parsed Layer-1 level: header + decoded object list. The read side (ROM bytes → this
/// object) lives in LevelParser; the write side (object list → ROM bytes) in
/// LevelEncoder. This class is just the data model + fields.
/// </summary>
public sealed class Level
{
    public readonly int Number;
    public readonly int DataPointer;     // SNES address of the header
    public readonly LevelHeader Header;
    public readonly IReadOnlyList<LevelObject> Objects;
    public readonly bool Empty;          // first data byte was 0xFF (no objects)

    internal Level(int number, int ptr, LevelHeader header, List<LevelObject> objs, bool empty)
    {
        Number = number; DataPointer = ptr; Header = header; Objects = objs; Empty = empty;
    }
}
