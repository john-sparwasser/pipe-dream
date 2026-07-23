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
        const float uiFontSize = 12f; // virtual units — same space as the style metrics
        io.FontGlobalScale = 1f / Scale;

        // Prefer an explicit override, then Segoe UI (Windows' default UI font, what
        // VS Code renders as); fall back to embedded Roboto on Mac/Linux.
        string segoe = @"C:\Windows\Fonts\segoeui.ttf";
        byte[] fontBytes =
            customFontPath != null && File.Exists(customFontPath) ? File.ReadAllBytes(customFontPath)
            : OperatingSystem.IsWindows() && File.Exists(segoe) ? File.ReadAllBytes(segoe)
            : LoadEmbeddedFont();
        fontDataHandle = GCHandle.Alloc(fontBytes, GCHandleType.Pinned);
        unsafe
        {
            var cfg = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
            cfg.FontDataOwnedByAtlas = false; // we own the pinned bytes; freed in Dispose
            io.Fonts.AddFontFromMemoryTTF(fontDataHandle.AddrOfPinnedObject(),
                fontBytes.Length, uiFontSize * Scale, cfg);
            cfg.Destroy();
        }

        unsafe
        {
            io.Fonts.GetTexDataAsRGBA32(out byte* pixelData, out int width, out int height, out int _);
            fontTexture = new Texture(app.GraphicsDevice, width, height, new ReadOnlySpan<byte>(pixelData, width * height * 4));
        }

        mesh = new Mesh<PosTexColVertex, ushort>(app.GraphicsDevice);
        material = app.GraphicsDevice.Defaults.TexturedMaterial.Clone();
        ApplyDarkPastelStyle();
        ImGui.SetCurrentContext(nint.Zero);
    }

    // Dark pastel theme. Rounding/padding are in ImGui's virtual units, so they
    // scale with Scale automatically — no manual DPI multiply needed here.
    private static void ApplyDarkPastelStyle()
    {
        var style = ImGui.GetStyle();
        var c = style.Colors;

        // Backgrounds
        c[(int)ImGuiCol.WindowBg]            = new Vector4(0.12f, 0.13f, 0.15f, 1.00f);
        c[(int)ImGuiCol.ChildBg]             = new Vector4(0.14f, 0.15f, 0.17f, 1.00f);
        c[(int)ImGuiCol.PopupBg]             = new Vector4(0.10f, 0.10f, 0.12f, 0.95f);
        c[(int)ImGuiCol.Border]              = new Vector4(0.30f, 0.33f, 0.42f, 0.40f);

        // Text
        c[(int)ImGuiCol.Text]                = new Vector4(0.90f, 0.93f, 0.95f, 1.00f);
        c[(int)ImGuiCol.TextDisabled]        = new Vector4(0.60f, 0.65f, 0.70f, 1.00f);

        // Headers
        c[(int)ImGuiCol.Header]              = new Vector4(0.36f, 0.42f, 0.55f, 0.60f);
        c[(int)ImGuiCol.HeaderHovered]       = new Vector4(0.44f, 0.50f, 0.68f, 0.80f);
        c[(int)ImGuiCol.HeaderActive]        = new Vector4(0.46f, 0.55f, 0.75f, 1.00f);

        // Buttons
        c[(int)ImGuiCol.Button]              = new Vector4(0.28f, 0.34f, 0.48f, 0.70f);
        c[(int)ImGuiCol.ButtonHovered]       = new Vector4(0.36f, 0.45f, 0.65f, 0.85f);
        c[(int)ImGuiCol.ButtonActive]        = new Vector4(0.40f, 0.50f, 0.70f, 1.00f);

        // Frames
        c[(int)ImGuiCol.FrameBg]             = new Vector4(0.20f, 0.22f, 0.28f, 1.00f);
        c[(int)ImGuiCol.FrameBgHovered]      = new Vector4(0.28f, 0.32f, 0.42f, 1.00f);
        c[(int)ImGuiCol.FrameBgActive]       = new Vector4(0.32f, 0.38f, 0.50f, 1.00f);

        // Tabs
        c[(int)ImGuiCol.Tab]                 = new Vector4(0.26f, 0.30f, 0.42f, 0.80f);
        c[(int)ImGuiCol.TabHovered]          = new Vector4(0.36f, 0.42f, 0.58f, 1.00f);
        c[(int)ImGuiCol.TabActive]           = new Vector4(0.42f, 0.50f, 0.68f, 1.00f);
        c[(int)ImGuiCol.TabUnfocused]        = new Vector4(0.20f, 0.24f, 0.32f, 0.80f);
        c[(int)ImGuiCol.TabUnfocusedActive]  = new Vector4(0.30f, 0.36f, 0.50f, 1.00f);

        // Titles — same blue family as tabs/buttons (was a stray teal)
        c[(int)ImGuiCol.TitleBg]             = new Vector4(0.16f, 0.18f, 0.24f, 1.00f);
        c[(int)ImGuiCol.TitleBgActive]       = new Vector4(0.26f, 0.30f, 0.42f, 1.00f);
        c[(int)ImGuiCol.TitleBgCollapsed]    = new Vector4(0.12f, 0.13f, 0.16f, 0.75f);

        // Scrollbars
        c[(int)ImGuiCol.ScrollbarBg]         = new Vector4(0.13f, 0.14f, 0.18f, 1.00f);
        c[(int)ImGuiCol.ScrollbarGrab]       = new Vector4(0.25f, 0.30f, 0.38f, 0.60f);
        c[(int)ImGuiCol.ScrollbarGrabHovered]= new Vector4(0.35f, 0.40f, 0.50f, 0.80f);
        c[(int)ImGuiCol.ScrollbarGrabActive] = new Vector4(0.45f, 0.50f, 0.65f, 1.00f);

        // Checkmark
        c[(int)ImGuiCol.CheckMark]           = new Vector4(0.80f, 0.85f, 1.00f, 1.00f);

        // Sliders
        c[(int)ImGuiCol.SliderGrab]          = new Vector4(0.50f, 0.65f, 0.90f, 1.00f);
        c[(int)ImGuiCol.SliderGrabActive]    = new Vector4(0.60f, 0.75f, 1.00f, 1.00f);

        // Resize grip
        c[(int)ImGuiCol.ResizeGrip]          = new Vector4(0.30f, 0.40f, 0.50f, 0.60f);
        c[(int)ImGuiCol.ResizeGripHovered]   = new Vector4(0.40f, 0.50f, 0.60f, 0.80f);
        c[(int)ImGuiCol.ResizeGripActive]    = new Vector4(0.50f, 0.60f, 0.80f, 1.00f);

        // Separators
        c[(int)ImGuiCol.Separator]           = new Vector4(0.35f, 0.40f, 0.48f, 0.70f);
        c[(int)ImGuiCol.SeparatorHovered]    = new Vector4(0.50f, 0.60f, 0.72f, 0.90f);
        c[(int)ImGuiCol.SeparatorActive]     = new Vector4(0.65f, 0.70f, 0.85f, 1.00f);

        // Menu bar / drag-drop
        c[(int)ImGuiCol.MenuBarBg]           = new Vector4(0.14f, 0.15f, 0.17f, 1.00f);
        c[(int)ImGuiCol.DragDropTarget]      = new Vector4(0.50f, 0.85f, 1.00f, 0.90f);

        // Metrics — tighter, and one consistent rounding family (4px, window 6px)
        style.WindowRounding    = 6.0f;
        style.ChildRounding     = 4.0f;
        style.FrameRounding     = 4.0f;
        style.PopupRounding     = 4.0f;
        style.ScrollbarRounding = 4.0f;
        style.GrabRounding      = 3.0f;
        style.TabRounding       = 4.0f;

        style.WindowBorderSize  = 0.0f;
        style.ChildBorderSize   = 0.0f;
        style.FrameBorderSize   = 0.0f;
        style.PopupBorderSize   = 1.0f;

        style.WindowPadding     = new Vector2(10, 8);
        style.FramePadding      = new Vector2(6, 4);
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
