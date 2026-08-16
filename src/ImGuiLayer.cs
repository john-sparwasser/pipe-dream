using System;
using System.Diagnostics;
using System.Numerics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Foster.Framework;
using ImGuiNET;

namespace PipeDream;

// Foster <-> ImGui.NET rendering backend. Lifted from the Helios platformer
// (Source/Editor/ImGuiLayer.cs); only change is the namespace and BaseScale default
// (EditorConfig dependency dropped). GetTextureID() binds any Foster Texture into an
// ImGui window — that's how the level canvas / tile pickers get drawn.
public class ImGuiLayer : IDisposable
{
    private readonly App app;
    private readonly IntPtr context;
    private readonly Mesh<PosTexColVertex, ushort> mesh;
    private readonly Material material;
    private readonly Texture fontTexture;
    private readonly List<Texture> boundTextures = [];
    private readonly List<Batcher> batchersUsed = [];
    private readonly Stack<Batcher> batchersStack = [];
    private readonly Stack<Batcher> batcherPool = [];
    private readonly List<(ImGuiKey, Keys)> keys = new()
    {
        (ImGuiKey.Tab, Keys.Tab),
        (ImGuiKey.LeftArrow, Keys.Left),
        (ImGuiKey.RightArrow, Keys.Right),
        (ImGuiKey.UpArrow, Keys.Up),
        (ImGuiKey.DownArrow, Keys.Down),
        (ImGuiKey.PageUp, Keys.PageUp),
        (ImGuiKey.PageDown, Keys.PageDown),
        (ImGuiKey.Home, Keys.Home),
        (ImGuiKey.End, Keys.End),
        (ImGuiKey.Insert, Keys.Insert),
        (ImGuiKey.Delete, Keys.Delete),
        (ImGuiKey.Backspace, Keys.Backspace),
        (ImGuiKey.Space, Keys.Space),
        (ImGuiKey.Enter, Keys.Enter),
        (ImGuiKey.Escape, Keys.Escape),
        (ImGuiKey.LeftCtrl, Keys.LeftControl),
        (ImGuiKey.LeftShift, Keys.LeftShift),
        (ImGuiKey.LeftAlt, Keys.LeftAlt),
        (ImGuiKey.LeftSuper, Keys.LeftOS),
        (ImGuiKey.RightCtrl, Keys.RightControl),
        (ImGuiKey.RightShift, Keys.RightShift),
        (ImGuiKey.RightAlt, Keys.RightAlt),
        (ImGuiKey.RightSuper, Keys.RightOS),
        (ImGuiKey.Menu, Keys.Menu),
        (ImGuiKey._0, Keys.D0),
        (ImGuiKey._1, Keys.D1),
        (ImGuiKey._2, Keys.D2),
        (ImGuiKey._3, Keys.D3),
        (ImGuiKey._4, Keys.D4),
        (ImGuiKey._5, Keys.D5),
        (ImGuiKey._6, Keys.D6),
        (ImGuiKey._7, Keys.D7),
        (ImGuiKey._8, Keys.D8),
        (ImGuiKey._9, Keys.D9),
        (ImGuiKey.A, Keys.A),
        (ImGuiKey.B, Keys.B),
        (ImGuiKey.C, Keys.C),
        (ImGuiKey.D, Keys.D),
        (ImGuiKey.E, Keys.E),
        (ImGuiKey.F, Keys.F),
        (ImGuiKey.G, Keys.G),
        (ImGuiKey.H, Keys.H),
        (ImGuiKey.I, Keys.I),
        (ImGuiKey.J, Keys.J),
        (ImGuiKey.K, Keys.K),
        (ImGuiKey.L, Keys.L),
        (ImGuiKey.M, Keys.M),
        (ImGuiKey.N, Keys.N),
        (ImGuiKey.O, Keys.O),
        (ImGuiKey.P, Keys.P),
        (ImGuiKey.Q, Keys.Q),
        (ImGuiKey.R, Keys.R),
        (ImGuiKey.S, Keys.S),
        (ImGuiKey.T, Keys.T),
        (ImGuiKey.U, Keys.U),
        (ImGuiKey.V, Keys.V),
        (ImGuiKey.W, Keys.W),
        (ImGuiKey.X, Keys.X),
        (ImGuiKey.Y, Keys.Y),
        (ImGuiKey.Z, Keys.Z),
        (ImGuiKey.F1, Keys.F1),
        (ImGuiKey.F2, Keys.F2),
        (ImGuiKey.F3, Keys.F3),
        (ImGuiKey.F4, Keys.F4),
        (ImGuiKey.F5, Keys.F5),
        (ImGuiKey.F6, Keys.F6),
        (ImGuiKey.F7, Keys.F7),
        (ImGuiKey.F8, Keys.F8),
        (ImGuiKey.F9, Keys.F9),
        (ImGuiKey.F10, Keys.F10),
        (ImGuiKey.F11, Keys.F11),
        (ImGuiKey.F12, Keys.F12),
        (ImGuiKey.Apostrophe, Keys.Apostrophe),
        (ImGuiKey.Comma, Keys.Comma),
        (ImGuiKey.Minus, Keys.Minus),
        (ImGuiKey.Period, Keys.Period),
        (ImGuiKey.Slash, Keys.Slash),
        (ImGuiKey.Semicolon, Keys.Semicolon),
        (ImGuiKey.Equal, Keys.Equals),
        (ImGuiKey.LeftBracket, Keys.LeftBracket),
        (ImGuiKey.Backslash, Keys.Backslash),
        (ImGuiKey.RightBracket, Keys.RightBracket),
        (ImGuiKey.GraveAccent, Keys.Tilde),
        (ImGuiKey.CapsLock, Keys.Capslock),
        (ImGuiKey.ScrollLock, Keys.ScrollLock),
        (ImGuiKey.NumLock, Keys.Numlock),
        (ImGuiKey.PrintScreen, Keys.PrintScreen),
        (ImGuiKey.Pause, Keys.Pause),
        (ImGuiKey.Keypad0, Keys.Keypad0),
        (ImGuiKey.Keypad1, Keys.Keypad1),
        (ImGuiKey.Keypad2, Keys.Keypad2),
        (ImGuiKey.Keypad3, Keys.Keypad3),
        (ImGuiKey.Keypad4, Keys.Keypad4),
        (ImGuiKey.Keypad5, Keys.Keypad5),
        (ImGuiKey.Keypad6, Keys.Keypad6),
        (ImGuiKey.Keypad7, Keys.Keypad7),
        (ImGuiKey.Keypad8, Keys.Keypad8),
        (ImGuiKey.Keypad9, Keys.Keypad9),
        (ImGuiKey.KeypadDecimal, Keys.KeypadPeroid),
        (ImGuiKey.KeypadDivide, Keys.KeypadDivide),
        (ImGuiKey.KeypadMultiply, Keys.KeypadMultiply),
        (ImGuiKey.KeypadSubtract, Keys.KeypadMinus),
        (ImGuiKey.KeypadAdd, Keys.KeypadPlus),
        (ImGuiKey.KeypadEnter, Keys.KeypadEnter),
        (ImGuiKey.KeypadEqual, Keys.KeypadEquals),
    };

    private PosTexColVertex[] vertices = Array.Empty<PosTexColVertex>();
    private ushort[] indices = Array.Empty<ushort>();
    private GCHandle fontDataHandle;

    /// <summary>Base UI scale factor (before DPI adjustment). Settable from the app.</summary>
    public float BaseScale = 2.0f;

    /// <summary>Actual scale including DPI/content scale factor</summary>
    public float Scale => BaseScale * ContentScale;

    /// <summary>Display content scale (1.0 on standard displays, 2.0 on Retina)</summary>
    public float ContentScale => app.Window.Size.X > 0 ? app.Window.WidthInPixels / (float)app.Window.Size.X : 1.0f;

    public Vector2 MousePosition => app.Input.Mouse.Position / Scale;
    public bool WantsTextInput { get; private set; }
    public bool WantsMouse { get; private set; }

    public ImGuiLayer(App app, string? customFontPath = null)
    {
        this.app = app;

        context = ImGui.CreateContext(null);
        ImGui.SetCurrentContext(context);

        var io = ImGui.GetIO();
        io.BackendFlags = ImGuiBackendFlags.None;
        io.ConfigFlags = ImGuiConfigFlags.DockingEnable;

        // Rasterize at physical size (virtual size * Scale) and shrink layout back
        // with FontGlobalScale, so text is crisp instead of a 13px atlas upscaled.
        // Scale is sampled once here; a runtime DPI change won't re-rasterize.
        // ponytail: rebuild the atlas on DPI change if multi-monitor crispness matters.
        const float uiFontSize = 10f; // virtual units — same space as the style metrics
        io.FontGlobalScale = 1f / Scale;

        // Prefer an explicit override, then Cascadia Mono (ships with Win11) or
        // Consolas — monospace with crisp pixel widths at any DPI.
        // ponytail: Mac/Linux falls back to embedded Roboto (not monospace).
        string cascadia = @"C:\Windows\Fonts\CascadiaMono.ttf";
        string consolas = @"C:\Windows\Fonts\consola.ttf";
        byte[] fontBytes =
            customFontPath != null && File.Exists(customFontPath) ? File.ReadAllBytes(customFontPath)
            : File.Exists(cascadia) ? File.ReadAllBytes(cascadia)
            : File.Exists(consolas) ? File.ReadAllBytes(consolas)
            : LoadEmbeddedFont();
        fontDataHandle = GCHandle.Alloc(fontBytes, GCHandleType.Pinned);
        unsafe
        {
            // Default ranges stop at U+00FF, so "…" and "→" rendered as '?'. Add the
            // general-punctuation run (dashes, quotes, ellipsis) and the arrows block.
            // Pairs, 0-terminated; must stay pinned until the atlas is built below.
            ushort[] ranges = { 0x0020, 0x00FF, 0x2010, 0x2026, 0x2190, 0x2193, 0 };
            fixed (ushort* rangesPtr = ranges)
            {
                var cfg = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
                cfg.FontDataOwnedByAtlas = false; // we own the pinned bytes; freed in Dispose
                io.Fonts.AddFontFromMemoryTTF(fontDataHandle.AddrOfPinnedObject(),
                    fontBytes.Length, uiFontSize * Scale, cfg, (IntPtr)rangesPtr);
                cfg.Destroy();

                io.Fonts.GetTexDataAsRGBA32(out byte* pixelData, out int width, out int height, out int _);
                fontTexture = new Texture(app.GraphicsDevice, width, height, new ReadOnlySpan<byte>(pixelData, width * height * 4));
            }
        }

        mesh = new Mesh<PosTexColVertex, ushort>(app.GraphicsDevice);
        material = app.GraphicsDevice.Defaults.TexturedMaterial.Clone();
        ApplyNuklearDarkGray();
        ImGui.SetCurrentContext(nint.Zero);
    }

    // Dark studio theme (modeled on the SMB3 Workshop reference: near-black chrome,
    // subtle 1px control borders, small radii on controls only, one blue accent for
    // active/selected state, bright text). Rounding/padding are in ImGui's virtual
    // units, so they scale with Scale automatically — no manual DPI multiply.
    private static void ApplyNuklearDarkGray()
    {
        var style = ImGui.GetStyle();
        var c = style.Colors;

        // Palette: slightly blue-tinted greys + a single accent.
        var text    = new Vector4(0.92f, 0.92f, 0.94f, 1.00f);
        var textDim = new Vector4(0.46f, 0.47f, 0.52f, 1.00f);
        var bg0     = new Vector4(0.055f, 0.055f, 0.065f, 1.00f);   // menu bar / chrome
        var bg1     = new Vector4(0.095f, 0.097f, 0.110f, 1.00f);   // panels
        var bg2     = new Vector4(0.135f, 0.140f, 0.160f, 1.00f);   // buttons / inputs
        var bg3     = new Vector4(0.175f, 0.180f, 0.205f, 1.00f);   // hovered
        var bg4     = new Vector4(0.215f, 0.225f, 0.255f, 1.00f);   // pressed
        var line    = new Vector4(0.215f, 0.225f, 0.255f, 1.00f);   // 1px control borders
        var accent  = new Vector4(0.13f, 0.46f, 0.95f, 1.00f);      // #2175F2
        var accentHi = new Vector4(0.24f, 0.55f, 1.00f, 1.00f);

        c[(int)ImGuiCol.Text]                  = text;
        c[(int)ImGuiCol.TextDisabled]          = textDim;
        c[(int)ImGuiCol.WindowBg]              = bg1;
        c[(int)ImGuiCol.ChildBg]               = new Vector4(0, 0, 0, 0);
        c[(int)ImGuiCol.PopupBg]               = new Vector4(0.105f, 0.107f, 0.122f, 1.00f);
        c[(int)ImGuiCol.Border]                = line;
        c[(int)ImGuiCol.BorderShadow]          = new Vector4(0, 0, 0, 0);
        c[(int)ImGuiCol.FrameBg]               = bg2;
        c[(int)ImGuiCol.FrameBgHovered]        = bg3;
        c[(int)ImGuiCol.FrameBgActive]         = bg4;
        c[(int)ImGuiCol.TitleBg]               = bg0;
        c[(int)ImGuiCol.TitleBgActive]         = bg1;
        c[(int)ImGuiCol.TitleBgCollapsed]      = bg0;
        c[(int)ImGuiCol.MenuBarBg]             = bg0;
        c[(int)ImGuiCol.ScrollbarBg]           = new Vector4(0.07f, 0.07f, 0.08f, 1.00f);
        c[(int)ImGuiCol.ScrollbarGrab]         = new Vector4(0.24f, 0.25f, 0.28f, 1.00f);
        c[(int)ImGuiCol.ScrollbarGrabHovered]  = new Vector4(0.31f, 0.32f, 0.36f, 1.00f);
        c[(int)ImGuiCol.ScrollbarGrabActive]   = new Vector4(0.38f, 0.39f, 0.44f, 1.00f);
        c[(int)ImGuiCol.CheckMark]             = accent;
        c[(int)ImGuiCol.SliderGrab]            = accent;
        c[(int)ImGuiCol.SliderGrabActive]      = accentHi;
        c[(int)ImGuiCol.Button]                = bg2;
        c[(int)ImGuiCol.ButtonHovered]         = bg3;
        c[(int)ImGuiCol.ButtonActive]          = bg4;
        c[(int)ImGuiCol.Header]                = accent with { W = 0.32f };   // list/tree selection
        c[(int)ImGuiCol.HeaderHovered]         = accent with { W = 0.45f };
        c[(int)ImGuiCol.HeaderActive]          = accent with { W = 0.58f };
        c[(int)ImGuiCol.Separator]             = line with { W = 0.60f };
        c[(int)ImGuiCol.SeparatorHovered]      = accent with { W = 0.60f };
        c[(int)ImGuiCol.SeparatorActive]       = accent;
        c[(int)ImGuiCol.ResizeGrip]            = line;
        c[(int)ImGuiCol.ResizeGripHovered]     = accent with { W = 0.70f };
        c[(int)ImGuiCol.ResizeGripActive]      = accent;
        c[(int)ImGuiCol.Tab]                   = new Vector4(0, 0, 0, 0);     // text-only tabs;
        c[(int)ImGuiCol.TabHovered]            = bg3;                          // active = raised block
        c[(int)ImGuiCol.TabActive]             = bg3;
        c[(int)ImGuiCol.TabUnfocused]          = new Vector4(0, 0, 0, 0);
        c[(int)ImGuiCol.TabUnfocusedActive]    = bg2;
        c[(int)ImGuiCol.DockingPreview]        = accent with { W = 0.60f };
        c[(int)ImGuiCol.DockingEmptyBg]        = bg0;
        c[(int)ImGuiCol.PlotLines]             = accent;
        c[(int)ImGuiCol.PlotLinesHovered]      = accentHi;
        c[(int)ImGuiCol.PlotHistogram]         = accent;
        c[(int)ImGuiCol.PlotHistogramHovered]  = accentHi;
        c[(int)ImGuiCol.TableHeaderBg]         = bg2;
        c[(int)ImGuiCol.TableBorderStrong]     = line;
        c[(int)ImGuiCol.TableBorderLight]      = line with { W = 0.50f };
        c[(int)ImGuiCol.TableRowBg]            = new Vector4(0, 0, 0, 0);
        c[(int)ImGuiCol.TableRowBgAlt]         = new Vector4(1, 1, 1, 0.04f);
        c[(int)ImGuiCol.TextSelectedBg]        = accent with { W = 0.35f };
        c[(int)ImGuiCol.DragDropTarget]        = accentHi;
        c[(int)ImGuiCol.NavHighlight]          = accent;
        c[(int)ImGuiCol.NavWindowingHighlight] = new Vector4(1, 1, 1, 0.70f);
        c[(int)ImGuiCol.NavWindowingDimBg]     = new Vector4(0, 0, 0, 0.45f);
        c[(int)ImGuiCol.ModalWindowDimBg]      = new Vector4(0, 0, 0, 0.45f);

        style.WindowRounding    = 0.0f;   // panels stay square...
        style.ChildRounding     = 0.0f;
        style.FrameRounding     = 3.0f;   // ...controls get the reference's small radius
        style.PopupRounding     = 3.0f;
        style.ScrollbarRounding = 0.0f;
        style.GrabRounding      = 6.0f;   // round slider knobs
        style.TabRounding       = 3.0f;

        style.WindowBorderSize  = 0.0f;   // panels separated by contrast, not borders
        style.ChildBorderSize   = 0.0f;
        style.FrameBorderSize   = 1.0f;   // subtle 1px outline on buttons/inputs
        style.PopupBorderSize   = 1.0f;

        style.WindowPadding     = new Vector2(10, 8);
        style.FramePadding      = new Vector2(8, 4);
        style.ItemSpacing       = new Vector2(8, 6);
        style.ItemInnerSpacing  = new Vector2(5, 4);
        style.IndentSpacing     = 16.0f;
        style.ScrollbarSize     = 12.0f;
        style.GrabMinSize       = 8.0f;
    }

    private static byte[] LoadEmbeddedFont()
    {
        using var s = typeof(ImGuiLayer).Assembly.GetManifestResourceStream("Roboto-Regular.ttf")
            ?? throw new InvalidOperationException("Embedded font Roboto-Regular.ttf not found");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    ~ImGuiLayer() => Dispose();

    public void BeginLayout()
    {
        Debug.Assert(ImGui.GetCurrentContext() == nint.Zero);
        ImGui.SetCurrentContext(context);

        boundTextures.Clear();
        batchersStack.Clear();
        batchersUsed.ForEach(batcherPool.Push);
        batchersUsed.Clear();

        var io = ImGui.GetIO();
        io.Fonts.SetTexID(GetTextureID(fontTexture));

        io.DeltaTime = app.Time.Delta;
        io.DisplaySize = new Vector2(app.Window.WidthInPixels / Scale, app.Window.HeightInPixels / Scale);
        io.DisplayFramebufferScale = Vector2.One * Scale;

        io.AddMousePosEvent(MousePosition.X, MousePosition.Y);
        io.AddMouseButtonEvent(0, app.Input.Mouse.LeftDown || app.Input.Mouse.LeftPressed);
        io.AddMouseButtonEvent(1, app.Input.Mouse.RightDown || app.Input.Mouse.RightPressed);
        io.AddMouseButtonEvent(2, app.Input.Mouse.MiddleDown || app.Input.Mouse.MiddlePressed);
        io.AddMouseWheelEvent(app.Input.Mouse.Wheel.X, app.Input.Mouse.Wheel.Y);

        foreach (var k in keys)
        {
            if (app.Input.Keyboard.Pressed(k.Item2))
                io.AddKeyEvent(k.Item1, true);
            if (app.Input.Keyboard.Released(k.Item2))
                io.AddKeyEvent(k.Item1, false);
        }

        bool shift = app.Input.Keyboard.Down(Keys.LeftShift) || app.Input.Keyboard.Down(Keys.RightShift);
        bool alt = app.Input.Keyboard.Down(Keys.LeftAlt) || app.Input.Keyboard.Down(Keys.RightAlt);
        bool ctrl = app.Input.Keyboard.Down(Keys.LeftControl) || app.Input.Keyboard.Down(Keys.RightControl);
        bool super = app.Input.Keyboard.Down(Keys.LeftOS) || app.Input.Keyboard.Down(Keys.RightOS);
        io.AddKeyEvent(ImGuiKey.ModShift, shift);
        io.AddKeyEvent(ImGuiKey.ModAlt, alt);
        io.AddKeyEvent(ImGuiKey.ModCtrl, ctrl);
        io.AddKeyEvent(ImGuiKey.ModSuper, super);

        if (app.Input.Keyboard.Text.Length > 0)
        {
            for (int i = 0; i < app.Input.Keyboard.Text.Length; i++)
                io.AddInputCharacter(app.Input.Keyboard.Text[i]);
        }

        WantsTextInput = io.WantTextInput;
        WantsMouse = io.WantCaptureMouse;
        ImGui.NewFrame();
    }

    public void EndLayout()
    {
        Debug.Assert(ImGui.GetCurrentContext() == context);
        ApplyMouseCursor();
        ImGui.Render();
        ImGui.SetCurrentContext(nint.Zero);
    }

    // Apply ImGui's requested cursor (SetMouseCursor / hovered widgets) to the OS cursor.
    private readonly Dictionary<Cursor.SystemTypes, Cursor> cursorCache = new();
    private ImGuiMouseCursor lastCursor = ImGuiMouseCursor.Arrow;

    private void ApplyMouseCursor()
    {
        var want = ImGui.GetMouseCursor();
        if (want == lastCursor) return;
        lastCursor = want;
        var sys = want switch
        {
            ImGuiMouseCursor.TextInput => Cursor.SystemTypes.Text,
            ImGuiMouseCursor.ResizeAll => Cursor.SystemTypes.Move,
            ImGuiMouseCursor.ResizeNS => Cursor.SystemTypes.ResizeVertical,
            ImGuiMouseCursor.ResizeEW => Cursor.SystemTypes.ResizeHorizontal,
            ImGuiMouseCursor.ResizeNESW => Cursor.SystemTypes.ResizeNESW,
            ImGuiMouseCursor.ResizeNWSE => Cursor.SystemTypes.ResizeNWSE,
            ImGuiMouseCursor.Hand => Cursor.SystemTypes.Pointer,
            ImGuiMouseCursor.NotAllowed => Cursor.SystemTypes.NotAllowed,
            _ => Cursor.SystemTypes.Default,
        };
        if (!cursorCache.TryGetValue(sys, out var cur))
            cursorCache[sys] = cur = new Cursor(sys);
        app.Window.SetMouseCursor(cur);
    }

    public bool BeginBatch(out Batcher batch, out Rect bounds)
        => BeginBatch(ImGui.GetContentRegionAvail(), out batch, out bounds);

    public bool BeginBatch(Vector2 size, out Batcher batch, out Rect bounds)
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var screenspace = Rect.Between(min, max);
        var clip = Rect.Between(ImGui.GetWindowDrawList().GetClipRectMin(), ImGui.GetWindowDrawList().GetClipRectMax());
        var scissor = screenspace.GetIntersection(clip).Scale(Scale).Int();

        ImGui.Dummy(size);

        batch = batcherPool.Count > 0 ? batcherPool.Pop() : new Batcher(app.GraphicsDevice);
        batch.Clear();
        batchersUsed.Add(batch);
        batchersStack.Push(batch);

        ImGui.GetWindowDrawList().AddCallback(new IntPtr(batchersUsed.Count), new IntPtr(0));

        batch.PushScissor(scissor);
        batch.PushMatrix(Matrix3x2.CreateScale(Scale));
        batch.PushMatrix(screenspace.TopLeft);

        bounds = new Rect(0, 0, screenspace.Width, screenspace.Height);
        return scissor.Width > 0 && scissor.Height > 0;
    }

    public void EndBatch()
    {
        var batch = batchersStack.Pop();
        batch.PopMatrix();
        batch.PopMatrix();
        batch.PopScissor();
    }

    public unsafe void Render()
    {
        Debug.Assert(ImGui.GetCurrentContext() == nint.Zero);
        ImGui.SetCurrentContext(context);

        var data = ImGui.GetDrawData();
        if (data.NativePtr == null || data.TotalVtxCount <= 0)
        {
            ImGui.SetCurrentContext(nint.Zero);
            return;
        }

        int vertexCount = 0, indexCount = 0;
        for (int i = 0; i < data.CmdListsCount; i++)
        {
            vertexCount += data.CmdLists[i].VtxBuffer.Size;
            indexCount += data.CmdLists[i].IdxBuffer.Size;
        }
        if (vertexCount > vertices.Length) Array.Resize(ref vertices, vertexCount);
        if (indexCount > indices.Length) Array.Resize(ref indices, indexCount);

        vertexCount = indexCount = 0;
        for (int i = 0; i < data.CmdListsCount; i++)
        {
            var list = data.CmdLists[i];
            var vertexSrc = new Span<PosTexColVertex>((void*)list.VtxBuffer.Data, list.VtxBuffer.Size);
            var indexSrc = new Span<ushort>((void*)list.IdxBuffer.Data, list.IdxBuffer.Size);
            vertexSrc.CopyTo(vertices.AsSpan()[vertexCount..]);
            indexSrc.CopyTo(indices.AsSpan()[indexCount..]);
            vertexCount += vertexSrc.Length;
            indexCount += indexSrc.Length;
        }
        mesh.SetVertices(vertices.AsSpan(0, vertexCount));
        mesh.SetIndices(indices.AsSpan(0, indexCount));

        var size = new Point2(app.Window.WidthInPixels, app.Window.HeightInPixels);
        var pass = new DrawCommand(app.Window, mesh, material)
        {
            BlendMode = new BlendMode(BlendOp.Add, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha)
        };

        Matrix4x4 mat =
            Matrix4x4.CreateScale(data.FramebufferScale.X, data.FramebufferScale.Y, 1.0f) *
            Matrix4x4.CreateOrthographicOffCenter(0, size.X, size.Y, 0, 0.1f, 1000.0f);
        material.Vertex.SetUniformBuffer(mat);

        int globalVtxOffset = 0, globalIdxOffset = 0;
        for (int i = 0; i < data.CmdListsCount; i++)
        {
            var imList = data.CmdLists[i];
            var imCommands = (ImDrawCmd*)imList.CmdBuffer.Data;

            for (ImDrawCmd* cmd = imCommands; cmd < imCommands + imList.CmdBuffer.Size; cmd++)
            {
                var scissor = new Rect(
                    cmd->ClipRect.X,
                    cmd->ClipRect.Y,
                    cmd->ClipRect.Z - cmd->ClipRect.X,
                    cmd->ClipRect.W - cmd->ClipRect.Y).Scale(data.FramebufferScale).Int();

                if (scissor.Width <= 0 || scissor.Height <= 0)
                    continue;

                if (cmd->UserCallback != IntPtr.Zero)
                {
                    var batchIndex = cmd->UserCallback.ToInt32() - 1;
                    if (batchIndex >= 0 && batchIndex < batchersUsed.Count)
                        batchersUsed[batchIndex].Render(app.Window, viewport: null, scissor: scissor);
                }
                else
                {
                    int textureIndex = cmd->TextureId.ToInt32();
                    if (textureIndex < boundTextures.Count)
                    {
                        // Pixel-art textures (level canvas, tile sheets) must sample
                        // nearest-neighbor; only the font atlas keeps linear filtering.
                        var tex = boundTextures[textureIndex];
                        var filter = tex == fontTexture ? TextureFilter.Linear : TextureFilter.Nearest;
                        material.Fragment.Samplers[0] = new(tex, new TextureSampler(filter,
                            TextureWrap.Clamp, TextureWrap.Clamp));
                    }

                    pass.VertexOffset = (int)(cmd->VtxOffset + globalVtxOffset);
                    pass.IndexOffset = (int)(cmd->IdxOffset + globalIdxOffset);
                    pass.IndexCount = (int)cmd->ElemCount;
                    pass.Scissor = scissor;
                    app.GraphicsDevice.Draw(pass);
                }
            }

            globalVtxOffset += imList.VtxBuffer.Size;
            globalIdxOffset += imList.IdxBuffer.Size;
        }

        ImGui.SetCurrentContext(nint.Zero);
    }

    /// <summary>Bind a Foster texture for this frame and get an ImTextureID to pass to ImGui.Image().</summary>
    public IntPtr GetTextureID(Texture? texture)
    {
        var id = new IntPtr(boundTextures.Count);
        if (texture != null)
            boundTextures.Add(texture);
        return id;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var c in cursorCache.Values) c.Dispose();
        cursorCache.Clear();
        ImGui.DestroyContext(context);
        if (fontDataHandle.IsAllocated) fontDataHandle.Free();
    }
}
