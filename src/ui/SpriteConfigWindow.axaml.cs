using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>
/// ASM ▸ Custom sprites ▸ Edit config: a library sprite's PIXI config as a form, in the place of
/// PIXI's CFG Editor (reference/PIXI_PORT.md phase 4). It edits a working copy; Save writes it as
/// the JSON PIXI reads, and JSON… shows that text with the edits in it, to edit by hand. The tabs:
/// Sprite (type, acts-like, tweakers, extra bytes) and List (Lunar Magic's sprite list entries) are
/// here; Look (displays and their tiles) is SpriteConfigWindow.Look.cs, Own tiles (the sprite's
/// own Map16 from 0x300) SpriteConfigWindow.OwnTiles.cs.
/// </summary>
public partial class SpriteConfigWindow : Window
{
    private readonly EditorSession session = null!;
    private readonly string name = "";
    private PixiConfig config = new();
    /// <summary>The config as last saved (or opened): it is edited while it reads otherwise, so a
    /// field put back, or a change event a control raises as it shows, leaves Save off.</summary>
    private string savedJson = "";
    /// <summary>Set while the window fills fields from the config, so their change events do not
    /// write back.</summary>
    private bool loading;

    /// <summary>Parameterless for the XAML loader; the real entry point is the other one.</summary>
    public SpriteConfigWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>Edit <paramref name="model"/>, the config of the library sprite <paramref name="name"/>.</summary>
    internal SpriteConfigWindow(EditorSession session, string name, PixiConfig model) : this()
    {
        this.session = session;
        this.name = name;
        config = model;
        savedJson = model.ToJson();
        Title = name + " config";
        WireSprite();
        WireLook();
        WireOwnTiles();
        WireList();
        ShowAll();
    }

    /// <summary>Fill every tab from the config: on opening, and after a JSON edit replaces it.</summary>
    private void ShowAll()
    {
        ShowSprite();
        ShowLook();
        ShowOwnTiles();
        ShowList();
    }

    private bool Dirty => config.ToJson() != savedJson;

    private void Touched()
    {
        bool dirty = Dirty;
        this.GetControl<Button>("SaveButton").IsEnabled = dirty;
        this.GetControl<TextBlock>("Status").Text = dirty ? "edited, unsaved" : "";
    }

    private static int? Hex(string? s)
        => int.TryParse(s?.Trim(), NumberStyles.HexNumber, null, out int v) ? v : null;

    private int Num(string control) => (int)(this.GetControl<NumericUpDown>(control).Value ?? 0);
    private bool Check(string control) => this.GetControl<CheckBox>(control).IsChecked == true;

    // ---- Sprite tab: what the sprite table gets ----

    /// <summary>Per tweaker byte, the controls showing each of its fields, to fill from the config.</summary>
    private readonly List<Action> showTweakers = [];

    private void WireSprite()
    {
        this.GetControl<ComboBox>("TypeBox").SelectionChanged += (_, _) =>
        {
            int t = this.GetControl<ComboBox>("TypeBox").SelectedIndex;
            if (!loading && t >= 0 && t != config.Type) { config.Type = t; Touched(); }
        };
        HexField("ActLike", 0xFF, v => config.ActLike = v);
        HexField("Prop1", 0xFF, v => config.Prop1 = (byte)v);
        HexField("Prop2", 0xFF, v => config.Prop2 = (byte)v);
        this.GetControl<NumericUpDown>("ExtraBytes").ValueChanged += (_, _) =>
        {
            if (!loading) { config.ExtraBytes = Num("ExtraBytes"); Touched(); }
        };
        this.GetControl<NumericUpDown>("ExtraBytesSet").ValueChanged += (_, _) =>
        {
            if (!loading) { config.ExtraBytesSet = Num("ExtraBytesSet"); Touched(); }
        };
        BuildTweakers();
    }

    /// <summary>A hex text field that writes its value, when it reads as hex up to max.</summary>
    private void HexField(string control, int max, Action<int> set)
    {
        var box = this.GetControl<TextBox>(control);
        box.TextChanged += (_, _) =>
        {
            if (loading || Hex(box.Text) is not { } v || v > max) return;
            set(v);
            Touched();
        };
    }

    /// <summary>A box per tweaker byte, a check box per flag and a number box per number field,
    /// named and laid out as PIXI's CFG Editor has them.</summary>
    private void BuildTweakers()
    {
        var wrap = this.GetControl<WrapPanel>("Tweakers");
        for (int i = 0; i < PixiConfig.TweakFields.Length; i++)
        {
            int b = i;
            var (key, fields) = PixiConfig.TweakFields[i];
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock { Text = key, Classes = { "head" } });
            foreach (var f in fields)
            {
                int bits = f.Mask << f.Shift;
                void Set(int v) { config.Tweak[b] = (byte)(config.Tweak[b] & ~bits | (v << f.Shift) & bits); Touched(); }
                if (f.Number)
                {
                    var n = new NumericUpDown { Minimum = 0, Maximum = f.Mask, FormatString = "0", Width = 110 };
                    n.ValueChanged += (_, _) => { if (!loading) Set((int)(n.Value ?? 0)); };
                    showTweakers.Add(() => n.Value = (config.Tweak[b] >> f.Shift) & f.Mask);
                    stack.Children.Add(new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                        Children = { n, new TextBlock { Text = f.Name, Classes = { "dim" } } },
                    });
                }
                else
                {
                    var c = new CheckBox { Content = f.Name };
                    c.IsCheckedChanged += (_, _) => { if (!loading) Set(c.IsChecked == true ? f.Mask : 0); };
                    showTweakers.Add(() => c.IsChecked = (config.Tweak[b] & bits) != 0);
                    stack.Children.Add(c);
                }
            }
            wrap.Children.Add(new Border { Classes = { "group" }, Child = stack });
        }
    }

    private void ShowSprite()
    {
        loading = true;
        this.GetControl<ComboBox>("TypeBox").SelectedIndex = Math.Clamp(config.Type, 0, 2);
        this.GetControl<TextBox>("ActLike").Text = $"{config.ActLike:X2}";
        this.GetControl<TextBox>("Prop1").Text = $"{config.Prop1:X2}";
        this.GetControl<TextBox>("Prop2").Text = $"{config.Prop2:X2}";
        this.GetControl<NumericUpDown>("ExtraBytes").Value = config.ExtraBytes;
        this.GetControl<NumericUpDown>("ExtraBytesSet").Value = config.ExtraBytesSet;
        foreach (var show in showTweakers) show();
        loading = false;
    }

    // ---- List tab: Lunar Magic's sprite list entries ----

    private ListBox EntryList => this.GetControl<ListBox>("Entries");
    private PixiConfig.CollectionEntry? Entry
        => EntryList.SelectedIndex is var i && i >= 0 && i < config.Collection.Count ? config.Collection[i] : null;

    private void WireList()
    {
        EntryList.SelectionChanged += (_, _) => { if (!loading) ShowEntryFields(); };
        this.GetControl<TextBox>("EntryName").TextChanged += (_, _) =>
        {
            if (loading || Entry is not { } e) return;
            e.Name = this.GetControl<TextBox>("EntryName").Text ?? "";
            Touched();
            Relist();                                  // not the fields: one is being typed in
        };
        this.GetControl<CheckBox>("EntryExtraBit").IsCheckedChanged += (_, _) =>
        {
            if (loading || Entry is not { } e) return;
            e.ExtraBit = Check("EntryExtraBit");
            Touched();
            ShowList(EntryList.SelectedIndex);
        };
        this.GetControl<TextBox>("EntryBytes").TextChanged += (_, _) =>
        {
            if (loading || Entry is not { } e || ParseBytes(this.GetControl<TextBox>("EntryBytes").Text) is not { } bytes) return;
            bytes.CopyTo(e.Bytes, 0);                  // the field shows only the bytes in use: the rest stay
            Touched();
        };
    }

    /// <summary>Up to twelve space-separated hex bytes; null when one is not.</summary>
    private static byte[]? ParseBytes(string? text)
    {
        var parts = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 12) return null;
        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (Hex(parts[i]) is { } v && v <= 0xFF) bytes[i] = (byte)v;
            else return null;
        return bytes;
    }

    private void ShowList(int select = 0)
    {
        loading = true;
        EntryList.ItemsSource = config.Collection.Select(EntryRow).ToList();
        EntryList.SelectedIndex = Math.Min(select, config.Collection.Count - 1);
        loading = false;
        ShowEntryFields();
    }

    /// <summary>Rewrite the entry lines, the same one picked.</summary>
    private void Relist()
    {
        int i = EntryList.SelectedIndex;
        loading = true;
        EntryList.ItemsSource = config.Collection.Select(EntryRow).ToList();
        EntryList.SelectedIndex = i;
        loading = false;
    }

    private static string EntryRow(PixiConfig.CollectionEntry e) => e.Name + (e.ExtraBit ? "  · extra bit" : "");

    private void ShowEntryFields()
    {
        var e = Entry;
        loading = true;
        foreach (var n in new[] { "EntryName", "EntryBytes" }) this.GetControl<TextBox>(n).IsEnabled = e is not null;
        this.GetControl<CheckBox>("EntryExtraBit").IsEnabled = e is not null;
        this.GetControl<TextBox>("EntryName").Text = e?.Name ?? "";
        this.GetControl<CheckBox>("EntryExtraBit").IsChecked = e?.ExtraBit == true;
        // Only as many bytes as a placement with this extra bit carries mean anything.
        int count = e is null ? 0 : e.ExtraBit ? config.ExtraBytesSet : config.ExtraBytes;
        this.GetControl<TextBox>("EntryBytes").Text = e is null ? "" : string.Join(" ", e.Bytes.Take(Math.Max(count, 1)).Select(b => $"{b:X2}"));
        loading = false;
    }

    private void OnAddEntry(object? sender, RoutedEventArgs e)
    {
        var from = Entry;
        config.Collection.Add(new PixiConfig.CollectionEntry
        {
            Name = from?.Name ?? name, ExtraBit = from?.ExtraBit ?? false, Bytes = [.. from?.Bytes ?? new byte[12]],
        });
        Touched();
        ShowList(config.Collection.Count - 1);
    }

    private void OnRemoveEntry(object? sender, RoutedEventArgs e)
    {
        int i = EntryList.SelectedIndex;
        if (i < 0 || i >= config.Collection.Count) return;
        config.Collection.RemoveAt(i);
        Touched();
        ShowList(i);
    }

    // ---- JSON, save, close ----

    /// <summary>The config as text, edits included; a hand edit PIXI can read replaces the form's.</summary>
    private async void OnJson(object? sender, RoutedEventArgs e)
    {
        string? problem = null;
        await new BlockEditWindow(name + " config", config.ToJson(), json =>
        {
            try { config = PixiConfig.Parse(json); }
            catch (FormatException ex) { problem = "not taken: " + ex.Message; return; }
            ShowAll();
            Touched();
        }).ShowDialog(this);
        if (problem is not null) this.GetControl<TextBlock>("Status").Text = problem;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        this.GetControl<TextBlock>("Status").Text = session.SetCustomSpriteConfig(name, config);
        savedJson = config.ToJson();
        this.GetControl<Button>("SaveButton").IsEnabled = false;
    }

    /// <summary>Unsaved edits are lost on close, so it asks first.</summary>
    private async void OnClose(object? sender, RoutedEventArgs e)
    {
        if (Dirty)
        {
            var ask = new ConfirmWindow("Unsaved config", $"Close without saving the edits to {name}?", "Discard");
            await ask.ShowDialog(this);
            if (!ask.Confirmed) return;
        }
        Close();
    }
}
