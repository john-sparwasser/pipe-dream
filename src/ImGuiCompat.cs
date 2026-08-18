using System.Numerics;
using System.Text;
using ImGuiNET;

namespace PipeDream;

/// <summary>
/// The ImGui.NET 1.90 calls that have no usable managed overload, done once here instead of
/// hand-rolled at each call site. Both cases need a NUL-terminated UTF-8 label passed to the
/// native entry point: BeginPopupModal's managed form insists on a ref-bool close button, and
/// BeginTabItem's has no flags-only overload.
/// </summary>
internal static class ImGuiCompat
{
    /// <summary>Open (or keep open) a modal centered on the viewport and begin it. Returns
    /// false when it isn't visible this frame, in which case the caller must NOT call
    /// EndPopup — the same contract as ImGui.BeginPopupModal.</summary>
    internal static bool BeginCenteredModal(string title,
        ImGuiWindowFlags flags = ImGuiWindowFlags.AlwaysAutoResize)
    {
        ImGui.OpenPopup(title);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing,
                               new Vector2(0.5f, 0.5f));
        return BeginPopupModal(title, flags);
    }

    internal static unsafe bool BeginPopupModal(string title, ImGuiWindowFlags flags)
    {
        int len = Encoding.UTF8.GetByteCount(title);
        Span<byte> buf = stackalloc byte[len + 1];
        Encoding.UTF8.GetBytes(title, buf);
        buf[len] = 0;
        fixed (byte* p = buf) return ImGuiNative.igBeginPopupModal(p, null, flags) != 0;
    }

    /// <summary>An int slider at the dialogs' standard field width. Three dialogs each grew
    /// their own copy of this; the width policy and the ref-plumbing belong in one place.</summary>
    internal static bool Slider(string label, int value, int min, int max, out int result,
                                float widthEm = 10f)
    {
        result = value;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * widthEm);
        return ImGui.SliderInt(label, ref result, min, max);
    }

    /// <summary>Hex spinner for an int, clamped to [0, max]. InputScalar takes raw pointers,
    /// so the unsafe block lives here once rather than in every dialog that wants a hex
    /// field. Set the item width before calling; returns true on edit.</summary>
    internal static unsafe bool HexInput(string label, ref int value, int max, string format)
    {
        int v = value, step = 1;
        bool changed = ImGui.InputScalar(label, ImGuiDataType.S32, (IntPtr)(&v), (IntPtr)(&step),
                                        IntPtr.Zero, format, ImGuiInputTextFlags.CharsHexadecimal);
        if (changed) value = Math.Clamp(v, 0, max);
        return changed;
    }

    /// <summary>BeginTabItem with flags and no close button. Same contract as ImGui's: only
    /// call EndTabItem when this returns true.</summary>
    internal static unsafe bool BeginTabItem(string label, ImGuiTabItemFlags flags)
    {
        int len = Encoding.UTF8.GetByteCount(label);
        Span<byte> buf = stackalloc byte[len + 1];
        Encoding.UTF8.GetBytes(label, buf);
        buf[len] = 0;
        fixed (byte* p = buf) return ImGuiNative.igBeginTabItem(p, null, flags) != 0;
    }
}
