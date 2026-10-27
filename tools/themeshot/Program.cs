using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PipeDream;
using PipeDream.Services;
using PipeDream.Ui;

// themeshot: render every dialog, the menu-bar dropdowns and each main-window mode of the real
// editor to PNG, headlessly, with a project loaded so lists are populated. See README.md.
//
//   dotnet run --project tools/themeshot -c Release -- <outDir> <rom-or-pdp> [pieceName ...]

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: themeshot <outDir> <rom-or-pdp> [pieceName ...]");
    return 2;
}
string temp = Path.Combine(Path.GetTempPath(), "themeshot", Environment.ProcessId.ToString());
Shots.RedirectConfig(temp);           // before anything can load the user's config
try { return Shots.Run(args[0], args[1], args[2..], temp); }
finally { try { Directory.Delete(temp, recursive: true); } catch { /* a leftover temp folder is harmless */ } }

/// <summary>One thing to render: a window (or a set of popups) and the images it produced.</summary>
sealed class Piece(string name, string axaml, Action<Piece> render)
{
    public string Name => name;
    public string Axaml => axaml;
    public List<string> Images { get; } = [];

    public void Render() => render(this);

    /// <summary>Settle, capture, save as Name.png, then Name-2.png, Name-3.png … A crop (in the
    /// top level's pixels) keeps only that part of the frame.</summary>
    public void Snap(TopLevel top, PixelRect? crop = null)
    {
        Shots.Settle();
        string file = Images.Count == 0 ? $"{name}.png" : $"{name}-{Images.Count + 1}.png";
        var frame = top.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame rendered");
        if (crop is { } c)
        {
            c = c.Intersect(new PixelRect(frame.PixelSize));
            using var cut = new RenderTargetBitmap(c.Size);
            using (var ctx = cut.CreateDrawingContext())
                ctx.DrawImage(frame, new Rect(c.X, c.Y, c.Width, c.Height), new Rect(0, 0, c.Width, c.Height));
            cut.Save(Path.Combine(Shots.Out, file));
        }
        else frame.Save(Path.Combine(Shots.Out, file));
        Images.Add(file);
    }
}

static class Shots
{
    public static string Out = "";

    /// <summary>Stage the input, open it, seed it, render the wanted pieces, write pieces.json.
    /// The exit code: 0 all rendered, 1 a seed or a piece failed, 2 an unknown piece name.</summary>
    public static int Run(string outDir, string input, string[] names, string temp)
    {
        var clock = Stopwatch.StartNew();
        Out = Directory.CreateDirectory(outDir).FullName;
        string pdp = StageProject(Path.GetFullPath(input), temp);

        AppBuilder.Configure<App>().UseSkia()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                  .SetupWithoutStarting();
        var main = OpenMain(pdp);
        if (Seed(Field<EditorSession>(main, "session")) is [_, ..] seedErrors)
        {
            foreach (string e in seedErrors) Console.Error.WriteLine(e);
            return 1;                  // a screenshot of an empty list is worse than none
        }

        var pieces = Pieces(main, pdp).ToList();
        WarnUncovered(pieces);
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Except(pieces.Select(p => p.Name), StringComparer.OrdinalIgnoreCase).ToList() is [_, ..] unknown)
        {
            Console.Error.WriteLine("unknown piece(s): " + string.Join(", ", unknown));
            Console.Error.WriteLine("known: " + string.Join(", ", pieces.Select(p => p.Name)));
            return 2;
        }

        int failed = 0;
        foreach (var piece in pieces.Where(p => wanted.Count == 0 || wanted.Contains(p.Name)))
        {
            try { piece.Render(); Console.WriteLine($"{piece.Name}: {piece.Images.Count} image(s)"); }
            catch (Exception ex) { failed++; Console.Error.WriteLine($"{piece.Name}: FAILED {ex.GetBaseException().Message}"); }
        }
        File.WriteAllText(Path.Combine(Out, "pieces.json"), JsonSerializer.Serialize(
            pieces.Where(p => p.Images.Count > 0).Select(p => new { name = p.Name, axaml = p.Axaml, images = p.Images }),
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"done in {clock.Elapsed.TotalSeconds:F1}s, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ---- setup ----

    /// <summary>Config and the built-in patch cache go to a throwaway folder, exactly as the test
    /// suite's TestConfigDir does, so a run never touches the user's config.json.</summary>
    public static void RedirectConfig(string temp)
    {
        Environment.SetEnvironmentVariable("PIPEDREAM_CONFIG_DIR", Path.Combine(temp, "config"));
        Environment.SetEnvironmentVariable("PIPEDREAM_BUILTIN_DIR", Path.Combine(temp, "config", "builtin"));
    }

    /// <summary>
    /// A copy of the input to work on, as a .pdp path: a project's folder is copied whole (the
    /// base ROM sits beside the .pdp); a ROM becomes a fresh project made the way File ▸ New
    /// Project makes one. Either way the copy is also the configured vanilla ROM, so Preferences
    /// shows one and nothing asks for it.
    /// </summary>
    public static string StageProject(string input, string temp)
    {
        var s = new EditorSession();
        if (input.EndsWith(".pdp", StringComparison.OrdinalIgnoreCase))
        {
            string from = Path.GetDirectoryName(input)!, to = Path.Combine(temp, "project");
            foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(to, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest);
            }
            string pdp = Path.Combine(to, Path.GetFileName(input));
            if (Directory.EnumerateFiles(to, "*.smc").FirstOrDefault() is { } rom) s.SetVanillaRom(rom);
            return pdp;
        }
        string vanilla = Path.Combine(temp, "vanilla.smc");
        Directory.CreateDirectory(temp);
        File.Copy(input, vanilla);
        s.SetVanillaRom(vanilla);
        string folder = Path.Combine(temp, "My Hack");
        if (!s.NewProject(folder, vanilla)) throw new InvalidOperationException("could not create a project: " + s.Status);
        return Directory.EnumerateFiles(folder, "*.pdp").Single();
    }

    /// <summary>The main window at 1500x900 with the project open, through the same private
    /// OpenProjectPath the File menu and startup use.</summary>
    public static MainWindow OpenMain(string pdp)
    {
        var w = new MainWindow { Width = 1500, Height = 900 };
        w.Show();
        Settle();
        Wait((Task)Call(w, "OpenProjectPath", pdp)!);
        var s = Field<EditorSession>(w, "session");
        if (!s.HasProject) throw new InvalidOperationException("project did not open: " + s.Status);
        Settle(600);
        return w;
    }

    /// <summary>
    /// A fresh project has empty libraries, and an empty list is not what a user who has been
    /// working sees. Each one that is empty gets a few entries, made through the same session
    /// calls the dialogs' New buttons use. Those calls report a refusal in their status rather
    /// than throwing (a block or patch name with a space is refused), so every one is checked,
    /// and the libraries are checked again at the end. Returns what went wrong, empty when nothing.
    /// </summary>
    public static List<string> Seed(EditorSession s)
    {
        List<string> errors = [];
        void Check(string what, string status, bool ok) { if (!ok) errors.Add($"seed {what}: {status}"); }
        string? Make(string what, string status, string? made) { Check(what, status, made is not null); return made; }

        if (s.BlockNames.Count == 0)
            foreach (string n in new[] { "coin_door", "ice_block", "spring_block" }) Make(n, s.NewBlock(n, out var m), m);
        if (s.Patches.Count == 0)
        {
            Make("retry_system", s.NewPatch("retry_system", out var m1), m1);
            if (Make("fast_overworld", s.NewPatch("fast_overworld", out var off), off) is { } name)   // one off, as the list allows
                Check("disable fast_overworld", s.SetPatchEnabled(name, false), s.Patches.Any(p => p.Name == name && !p.Enabled));
            Make("no_more_sprite_tile_limits", s.NewPatch("no_more_sprite_tile_limits", out var m2), m2);
        }
        if (s.MusicSongs.Count == 0)
        {
            if (Make("Athletic Remix", s.NewSong("Athletic Remix", out var song), song) is { } path)
                Check("song number", s.SetSongNumber(path, 0x29, global: false), s.MusicSongs.First(x => x.Path == path).Number.Length > 0);
            Make("Castle Theme", s.NewSong("Castle Theme", out var m), m);
        }
        if (s.CustomSprites.Count == 0)
        {
            if (Make("Thwimp", s.NewCustomSprite("Thwimp", "sprite", out var spr), spr) is { } name)
                Check("Thwimp slots", s.SetCustomSpriteSlots(name, "36"), s.CustomSprites.First(c => c.Name == name).Slots.Count > 0);
            Make("Bullet Generator", s.NewCustomSprite("Bullet Generator", "generator", out var m), m);
        }
        if (s.UberFiles.Count == 0)
        {
            if (Make("level/water_physics.asm", s.NewUberFile("level/water_physics.asm", out var uber), uber) is { } file)
                Check("UberASM levels", s.RunUberFileIn(file, "105 106"), s.UberNumbers("level").Count > 0);
            Make("gamemode/hud.asm", s.NewUberFile("gamemode/hud.asm", out var m1), m1);
            Make("library/math.asm", s.NewUberFile("library/math.asm", out var m2), m2);
        }
        if (s.CourseBotEntries.Count == 0)
            Check("Course Bot level", s.Status, s.CreateCourseBotLevel("Grassland Gauntlet", 0x105) >= 0);
        if (s.ExAnimSlots(true).Count == 0)           // the global list: the one the Animations page opens on
            Check("ExAnimation slot", s.Status, s.AddExAnimSlot(true) is not null);

        foreach (var (what, count) in new[]
                 {
                     ("blocks", s.BlockNames.Count), ("patches", s.Patches.Count), ("songs", s.MusicSongs.Count),
                     ("custom sprites", s.CustomSprites.Count), ("UberASM files", s.UberFiles.Count),
                     ("Course Bot levels", s.CourseBotEntries.Count), ("global ExAnimation slots", s.ExAnimSlots(true).Count),
                 })
            Check(what, "still empty after seeding", count > 0);
        return errors;
    }

    // ---- the pieces ----

    public static IEnumerable<Piece> Pieces(MainWindow w, string pdp)
    {
        var s = Field<EditorSession>(w, "session");
        var rom = Prop<Rom>(s, "Rom");
        var header = s.Header!.Value;
        var entry = s.MainEntrance!.Value;

        yield return new Piece("Main", "src/ui/MainWindow.axaml", p => MainModes(p, w));
        yield return new Piece("Menus", "src/ui/MainWindow.axaml", p => MenuDropdowns(p, w));

        // Each dialog built with the arguments its real call site passes (grep `new XWindow(`).
        yield return Dialog(w, "BackgroundPicker", () => New<BackgroundPickerWindow>(s.Backgrounds(), (int?)null));
        yield return Dialog(w, "BlockEdit", () => New<BlockEditWindow>(s, s.BlockNames[0]));
        yield return Dialog(w, "BlockPicker", () => New<BlockPickerWindow>(s, s.BlockNames[0]));
        yield return Dialog(w, "Blocks", () => New<BlocksWindow>(s));
        yield return Dialog(w, "Confirm", () => new ConfirmWindow("Clear project edits",
            $"Discard every edit in '{s.ProjectName}'? Levels, Map16, GFX, palettes and entrances all "
            + "reset to the base ROM. This cannot be undone.", "Clear"));
        yield return Dialog(w, "CourseBot", () => New<CourseBotWindow>(s));
        yield return Dialog(w, "CustomSprites", () => New<CustomSpritesWindow>(s));
        yield return Dialog(w, "Entrance", () => new EntranceWindow(entry, EntranceKind.Main, rom.HasFreeMidwayPosition));
        yield return Dialog(w, "EventPiece", () => new EventPieceWindow(s, "Add event step",
            "Pick the piece event 01 lays next, then click where on the map it lands.", null));
        yield return Dialog(w, "ExAnimSlot", () => new ExAnimSlotWindow(s.ExAnimSlots(true)[0], s.ExAnimAltFile(true), true));
        yield return Dialog(w, "FirstRun", () => new FirstRunWindow());
        yield return Dialog(w, "GfxBrowser", () => New<GfxBrowserWindow>(s, $"Load into this level's {s.GfxBins[0].Name} bin"));
        yield return Dialog(w, "GfxHeader", () =>
        {
            var (layer1, sprites) = s.GfxHeaderChoices();
            return new GfxHeaderWindow(layer1, header.Tileset, sprites, header.SpriteSet);
        });
        yield return Dialog(w, "Layer3Options", () => new Layer3OptionsWindow(Layer3.OptionNames, entry.Layer3Option,
            header.Layer3Priority != 0, s.Layer3HasTilemap, s.Layer3Advanced, s.Layer3AdvancedSupported));
        yield return Dialog(w, "LevelExits", () => new LevelExitsWindow(s.Edit!.ReadExits()));
        yield return Dialog(w, "LevelProperties", () => new LevelPropertiesWindow(header, entry, s.HasHeaderOverride,
            s.LevelOptionChoices, (s.UberLine("level", s.LevelNum), s.UberLine("level", UberEntry.All))));
        yield return Dialog(w, "Licenses", () => new LicensesWindow());
        yield return Dialog(w, "LocateBase", () => LocateBase(pdp));
        yield return Dialog(w, "Music", () => New<MusicWindow>(s));
        yield return Dialog(w, "NewBlock", () => New<NewBlockWindow>(s));
        yield return Dialog(w, "OwLevelTile", () => new OwLevelTileWindow(OwTile(s)));
        yield return Dialog(w, "Patches", () => New<PatchesWindow>(s));
        yield return Dialog(w, "Preferences", () => New<PreferencesWindow>(s));
        // The file row would show this run's temp folder; a user's folder reads the same and is stable.
        string temp = Path.GetDirectoryName(Path.GetDirectoryName(pdp))!;
        yield return Dialog(w, "RomInfo", () => new RomInfoWindow(
            s.RomInfo().Select(r => (r.Label, r.Value.Replace(temp, "/Users/mario/Hacks")))));
        yield return Dialog(w, "SecondaryEntrance", () => New<SecondaryEntranceWindow>(
            s.Entrances().FirstOrDefault(e => e.Kind == EntranceKind.Secondary)?.Index ?? 0,
            (Func<int, SecondaryEntrance?>)s.ReadEntrance));
        yield return Dialog(w, "SlotNumber", () => new SlotNumberWindow("New event",
            "Start editing which event? These lay nothing yet.", s.OwEmptyEvents().ToList()));
        yield return Dialog(w, "SongEdit", () => New<SongEditWindow>(s, s.MusicSongs[0].Path));
        yield return Dialog(w, "SongNumber", () => new SongNumberWindow((0x29, false)));
        yield return Dialog(w, "SpriteConfig", () =>
        {
            string name = s.CustomSprites.Where(c => s.CustomSpriteModel(c.Name) is not null)
                            .OrderBy(c => c.Kind != "sprite").First().Name;
            return New<SpriteConfigWindow>(s, name, s.CustomSpriteModel(name)!);
        });
        yield return Dialog(w, "SpriteData", () => New<SpriteDataWindow>(s.Sprites!.Sprites.Sprites[0]));
        yield return Dialog(w, "SpriteTiles", () => new SpriteTilesWindow(s));
        // ponytail: stand-in recents; the real list is the temp copies this run made, under a temp path.
        yield return Dialog(w, "Start", () => new StartWindow([
            "/Users/mario/Hacks/Grassland Gauntlet/Grassland Gauntlet.pdp",
            "/Users/mario/Hacks/Cave Story/Cave Story.pdp",
            "/Users/mario/Hacks/Kaizo Practice/Kaizo Practice.pdp"]));
        yield return Dialog(w, "TextPrompt", () => new TextPromptWindow($"Name for this layer 2 tilemap",
            $"level {s.LevelNum:X3} layer 2"));
        // Both pickers the Animations page opens: a frame's source tiles, then the destination.
        // The source opens on the slot's own file 60, which a vanilla base leaves blank; AN1 is
        // the dropdown's next entry and has tiles in it.
        int palRow = Field<ComboBox>(w, "animPalRow").SelectedIndex;
        yield return Dialog(w, "TilePicker",
            () =>
            {
                var pick = new TilePickerWindow(s, [0], s.ExAnimAltFile(true), palRow, false, true);
                pick.GetControl<ComboBox>("Source").SelectedIndex = 1;
                return pick;
            },
            () => new TilePickerWindow(s, s.ExAnimSlots(true)[0], palRow));
        yield return Dialog(w, "UberAsm", () => New<UberAsmWindow>(s));
        yield return Dialog(w, "UberFile", () => new UberFileWindow("New UberASM file"));
        // ponytail: stand-in release, a real one needs the network; the window only reads these fields.
        yield return Dialog(w, "Update", () => new UpdateWindow(s, new UpdateInfo(new Version(1, 4, 0),
            "PipeDream-1.4.0-osx-arm64.zip", "https://example.invalid/PipeDream-1.4.0.zip", 48_213_504,
            "- Overworld event editor\n- SA-1 builds\n- Faster Map16 redraws")));
    }

    /// <summary>A dialog piece: each factory is one state, shown modal over the main window the
    /// way the app shows it, captured, and closed.</summary>
    private static Piece Dialog(Window owner, string name, params Func<Window>[] states) =>
        new(name, $"src/ui/{name}Window.axaml", p =>
        {
            foreach (var make in states)
            {
                var dlg = make();
                _ = dlg.ShowDialog(owner);
                try { p.Snap(dlg); }
                finally { dlg.Close(); Settle(50); }
            }
        });

    /// <summary>The recovery prompt with what a real failed open says: the project copied alone,
    /// without the base ROM beside it, and opened.</summary>
    private static LocateBaseWindow LocateBase(string pdp)
    {
        // Same folder name: a project is named for its folder.
        string folder = Path.GetDirectoryName(pdp)!;
        string alone = Path.Combine(folder, "..", "shared", Path.GetFileName(folder), Path.GetFileName(pdp));
        Directory.CreateDirectory(Path.GetDirectoryName(alone)!);
        File.Copy(pdp, alone, overwrite: true);
        var other = new EditorSession();
        other.OpenProject(alone);
        return new LocateBaseWindow(other.PendingProjectName ?? "project", other.PendingBaseProblem ?? "", other.PendingBaseDescription);
    }

    /// <summary>A level tile off the main map: Yoshi's Island 1 when it is there, else the first.</summary>
    private static EditorSession.OwLevelTile OwTile(EditorSession s)
    {
        var tiles = (from y in Enumerable.Range(0, 64) from x in Enumerable.Range(0, 32)
                     select s.OwLevelTileAt(x, y)).OfType<EditorSession.OwLevelTile>().ToList();
        return tiles.FirstOrDefault(t => t.Level == 0x105, tiles[0]);
    }

    // ---- the main window ----

    private static readonly string[] modes = ["ModeLevel", "ModeMap16", "ModeGfx", "ModePalette", "ModeBg", "ModeAnim", "ModeOverworld"];

    /// <summary>One image per canvas mode, switched the way a click does: OnMode runs off Click.</summary>
    private static void MainModes(Piece p, MainWindow w)
    {
        foreach (string mode in modes.Append("ModeLevel"))
        {
            w.GetControl<ToggleButton>(mode).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(400);
            if (p.Images.Count < modes.Length) p.Snap(w);
        }
    }

    /// <summary>
    /// Each top-level menu opened, and each of its submenus that has items. Headless popups are
    /// overlay popups drawn into the window, so the window is captured and cut down to the
    /// dropdown plus the menu header it hangs off, with room for the shadow.
    /// </summary>
    private static void MenuDropdowns(Piece p, MainWindow w)
    {
        var menu = w.GetVisualDescendants().OfType<Menu>().First();
        foreach (var top in menu.Items.OfType<MenuItem>().Where(i => i.IsVisible))
        {
            top.IsSubMenuOpen = true;
            Settle();
            p.Snap(w, Around(w, top, Dropdown(top)));
            foreach (var sub in top.Items.OfType<MenuItem>().Where(i => i.IsVisible && i.ItemCount > 0))
            {
                sub.IsSubMenuOpen = true;
                Settle();
                p.Snap(w, Around(w, top, Dropdown(top), Dropdown(sub)));
                sub.IsSubMenuOpen = false;
            }
            top.IsSubMenuOpen = false;
            Settle(50);
        }
    }

    private static Control Dropdown(MenuItem item) =>
        item.GetVisualDescendants().OfType<Popup>().First().Child
        ?? throw new InvalidOperationException($"{item.Header}: popup has no content");

    /// <summary>The union of the controls' bounds in the window, padded, in pixels.</summary>
    private static PixelRect Around(Window w, params Control[] parts)
    {
        const double pad = 12;
        var all = parts.Select(c => new Rect(c.TranslatePoint(default, w) ?? default, c.Bounds.Size))
                       .Aggregate((a, b) => a.Union(b)).Inflate(pad);
        return PixelRect.FromRect(all, w.RenderScaling);
    }

    /// <summary>Say which dialog types in the app have no piece, so a new window is not silently skipped.</summary>
    public static void WarnUncovered(IEnumerable<Piece> pieces)
    {
        var have = pieces.Select(p => p.Name + "Window").ToHashSet();
        foreach (var t in typeof(MainWindow).Assembly.GetTypes()
                     .Where(t => t.IsSubclassOf(typeof(Window)) && t.Name.EndsWith("Window") && t != typeof(MainWindow) && !have.Contains(t.Name)))
            Console.Error.WriteLine($"warning: no piece for {t.Name}");
    }

    // ---- the dispatcher ----

    /// <summary>Pump the UI thread for a while: layout, bindings, posted work and render ticks.</summary>
    public static void Settle(int ms = 250)
    {
        var until = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        } while (until.ElapsedMilliseconds < ms);
    }

    private static void Wait(Task t)
    {
        while (!t.IsCompleted) Settle(20);
        t.GetAwaiter().GetResult();
    }

    // ---- reflection: the app's internal constructors and private members ----

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static T New<T>(params object?[] args) where T : Window =>
        (T)Activator.CreateInstance(typeof(T), Any, null, args, null)!;

    public static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Any)!.GetValue(o)!;

    private static T Prop<T>(object o, string name) => (T)o.GetType().GetProperty(name, Any)!.GetValue(o)!;

    private static object? Call(object o, string name, params object?[] args) =>
        o.GetType().GetMethods(Any).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);
}
