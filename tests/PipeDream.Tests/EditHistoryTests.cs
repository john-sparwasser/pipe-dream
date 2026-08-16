using Xunit;

namespace PipeDream.Tests;

public class EditHistoryTests
{
    /// <summary>Editor-style helper: applies a value and pushes the matching undo/redo pair.</summary>
    private sealed class Counter
    {
        public int Value;
        private readonly EditHistory h;
        public Counter(EditHistory h) => this.h = h;
        public void SetTo(int nv)
        {
            int old = Value;
            h.Push(() => Value = old, () => Value = nv);
            Value = nv;
        }
    }

    [Fact]
    public void undo_restores_prior_states_and_redo_replays_them()
    {
        var h = new EditHistory();
        var c = new Counter(h);
        c.SetTo(1); c.SetTo(2);

        h.Undo(); Assert.Equal(1, c.Value);
        h.Undo(); Assert.Equal(0, c.Value);
        Assert.False(h.CanUndo);
        Assert.True(h.CanRedo);

        h.Redo(); Assert.Equal(1, c.Value);
        h.Redo(); Assert.Equal(2, c.Value);
        Assert.False(h.CanRedo);
        Assert.True(h.CanUndo);
    }

    [Fact]
    public void pushing_after_undo_drops_the_redo_tail()
    {
        var h = new EditHistory();
        var c = new Counter(h);
        c.SetTo(1); c.SetTo(2);
        h.Undo();                            // back to 1, redo available
        Assert.True(h.CanRedo);

        c.SetTo(5);                          // new edit forks history
        Assert.False(h.CanRedo);
        h.Redo();                            // no-op
        Assert.Equal(5, c.Value);

        h.Undo(); Assert.Equal(1, c.Value);  // undo walks the new branch, not the dropped one
        h.Undo(); Assert.Equal(0, c.Value);
    }

    [Fact]
    public void empty_history_flags_are_false_and_undo_redo_are_safe_no_ops()
    {
        var h = new EditHistory();
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
        h.Undo();                            // must not throw
        h.Redo();
    }

    [Fact]
    public void a_group_coalesces_multiple_pushes_into_one_undo_step()
    {
        var h = new EditHistory();
        var c = new Counter(h);
        h.BeginGroup();
        c.SetTo(1); c.SetTo(2); c.SetTo(3);
        h.EndGroup();

        h.Undo();                            // one undo reverts the whole group
        Assert.Equal(0, c.Value);
        Assert.False(h.CanUndo);

        h.Redo();                            // one redo replays it in order
        Assert.Equal(3, c.Value);
    }

    [Fact]
    public void clear_forgets_both_stacks()
    {
        var h = new EditHistory();
        var c = new Counter(h);
        c.SetTo(1); c.SetTo(2);
        h.Undo();
        h.Clear();
        Assert.False(h.CanUndo);
        Assert.False(h.CanRedo);
        h.Undo();                            // no-op: state stays wherever it was
        Assert.Equal(1, c.Value);
    }
}
