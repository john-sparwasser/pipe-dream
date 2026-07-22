using PipeDream;
using Xunit;

namespace PipeDream.Tests;

// The generic undo/redo command stack — pure, no ROM/UI.
public class EditHistoryTests
{
    [Fact]
    public void Undo_Redo_InvokeTheClosures()
    {
        int v = 0;
        var h = new EditHistory();
        h.Push(undoAction: () => v = 0, redoAction: () => v = 1);
        // Push does NOT invoke (the edit already happened); simulate that:
        v = 1;

        Assert.True(h.CanUndo);
        Assert.False(h.CanRedo);

        h.Undo();
        Assert.Equal(0, v);
        Assert.False(h.CanUndo);
        Assert.True(h.CanRedo);

        h.Redo();
        Assert.Equal(1, v);
        Assert.True(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Push_ClearsRedo()
    {
        var h = new EditHistory();
        h.Push(() => { }, () => { });
        h.Undo();
        Assert.True(h.CanRedo);
        h.Push(() => { }, () => { });    // a new edit invalidates the redo branch
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Interleaved_Order_IsLastInFirstOut()
    {
        var log = new List<string>();
        var h = new EditHistory();
        h.Push(() => log.Add("undoA"), () => { });
        h.Push(() => log.Add("undoB"), () => { });
        h.Undo();   // undoes B first
        h.Undo();   // then A
        Assert.Equal(new[] { "undoB", "undoA" }, log);
    }

    [Fact]
    public void Undo_Empty_IsNoop()
    {
        var h = new EditHistory();
        h.Undo();   // must not throw
        h.Redo();
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Clear_DropsBothStacks()
    {
        var h = new EditHistory();
        h.Push(() => { }, () => { });
        h.Undo();
        h.Clear();
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
    }
}
