namespace PipeDream;

/// <summary>
/// A plain undo/redo stack of command pairs. Each entry is two closures — undo and redo —
/// captured by the editor at the moment of the edit, so this class knows nothing about
/// tiles, sprites, objects, or palettes. Pushing a new command clears the redo stack.
/// </summary>
public sealed class EditHistory
{
    private readonly record struct Command(Action Undo, Action Redo);
    private readonly List<Command> undo = new();
    private readonly List<Command> redo = new();
    private const int Max = 256;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Push(Action undoAction, Action redoAction)
    {
        undo.Add(new Command(undoAction, redoAction));
        if (undo.Count > Max) undo.RemoveAt(0);
        redo.Clear();
    }

    public void Undo()
    {
        if (undo.Count == 0) return;
        var c = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        c.Undo();
        redo.Add(c);
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        var c = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        c.Redo();
        undo.Add(c);
    }

    public void Clear() { undo.Clear(); redo.Clear(); }
}
