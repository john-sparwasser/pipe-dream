using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>
/// The level's layer-3 settings — Lunar Magic's "Change Layer 3 Settings" cut down to what this
/// editor has decoded: the Layer 3 Options value, the priority flag, and the advanced bypass
/// group that overrides the tileset's own scroll and blend behaviour.
///
/// The three live in three different records — the option is the entrance byte $05F200's top two
/// bits, the priority is header byte 2 bit 7, the advanced group is spare nibbles in LM's
/// per-level GFX record — and the caller applies them separately. They are one dialog because
/// they are one question to the person asking it.
///
/// The advanced group is the answer to "I don't want the tileset's default": a tilemap bypass
/// swaps the picture, but LM's help is explicit that the original setting's behaviour and
/// scrolling stay until this is on.
///
/// <see cref="Result"/> is null on cancel.
/// </summary>
public partial class Layer3OptionsWindow : Window
{
    public (int Option, bool Priority, Layer3.Advanced? Advanced)? Result { get; private set; }

    private ComboBox option = null!, vscroll = null!, hscroll = null!, xpos = null!;
    private CheckBox priority = null!, advanced = null!, cgadsub = null!, subscreen = null!, sync = null!;
    private TextBox ybox = null!;

    public Layer3OptionsWindow() => AvaloniaXamlLoader.Load(this);

    /// <param name="hasTilemapFor">Whether an option value resolves to a tilemap for this
    /// level's mode — most do not for every mode, and the pane would otherwise be the first
    /// place that showed up.</param>
    /// <param name="adv">The level's advanced bypass, or null when it has none.</param>
    /// <param name="advSupported">Whether the base ROM carries LM's reader for it. When it does
    /// not, the settings still edit and still save — they just do not reach the game yet.</param>
    public Layer3OptionsWindow(IReadOnlyList<string> options, int selected, bool priorityOn,
                               Func<int, bool> hasTilemapFor,
                               Layer3.Advanced? adv = null, bool advSupported = true) : this()
    {
        option = this.GetControl<ComboBox>("OptionBox");
        priority = this.GetControl<CheckBox>("PriorityBox");
        advanced = this.GetControl<CheckBox>("AdvancedBox");
        cgadsub = this.GetControl<CheckBox>("CgAdSubBox");
        subscreen = this.GetControl<CheckBox>("SubscreenBox");
        sync = this.GetControl<CheckBox>("SyncBox");
        vscroll = this.GetControl<ComboBox>("VScrollBox");
        hscroll = this.GetControl<ComboBox>("HScrollBox");
        xpos = this.GetControl<ComboBox>("XBox");
        ybox = this.GetControl<TextBox>("YBox");
        var pane = this.GetControl<StackPanel>("AdvancedPane");
        var note = this.GetControl<TextBlock>("ModeNote");
        var advNote = this.GetControl<TextBlock>("AdvancedNote");

        option.ItemsSource = options;
        option.SelectedIndex = Math.Clamp(selected, 0, options.Count - 1);
        priority.IsChecked = priorityOn;

        vscroll.ItemsSource = Layer3.VScrollNames;
        hscroll.ItemsSource = Layer3.HScrollNames;
        xpos.ItemsSource = Layer3.XPositions.Select(x => $"{x:X2}").ToArray();

        var a = adv ?? new Layer3.Advanced(false, false, false, 0, 0, 0, 0);
        advanced.IsChecked = adv is not null;
        cgadsub.IsChecked = a.CgAdSub;
        subscreen.IsChecked = a.Subscreen;
        sync.IsChecked = a.FixScrollSync;
        vscroll.SelectedIndex = Math.Clamp(a.VScroll, 0, Layer3.VScrollNames.Length - 1);
        hscroll.SelectedIndex = Math.Clamp(a.HScroll, 0, Layer3.HScrollNames.Length - 1);
        xpos.SelectedIndex = Math.Clamp(a.XPos, 0, Layer3.XPositions.Length - 1);
        ybox.Text = Hex(a.Y);

        advNote.Text = advSupported
            ? "Off, the level scrolls and blends the way its Layer 3 option says — which for "
            + "Tileset Specific means the way this level's tileset does."
            : "This base ROM has no reader for these — they save and show here, but the game "
            + "will not use them until the base carries Lunar Magic's advanced layer-3 hack.";

        var blendNote = this.GetControl<TextBlock>("BlendNote");

        void Sync2()
        {
            pane.IsEnabled = advanced.IsChecked == true;
            // The two blend switches LOOK independent and are not: moving layer 3 to the
            // subscreen takes it off the main screen, and CGADSUB only blends what is ON the
            // main screen. Ticking both is the natural thing to do for translucent mist and it
            // gives you an opaque layer instead — LM's help says so in a sentence most people
            // never read, and this cost a real project two rounds of "it still isn't
            // translucent". So it is on screen, only when the combination is actually set.
            blendNote.Text = cgadsub.IsChecked == true && subscreen.IsChecked == true
                ? "Those two cancel out: CGADSUB blends layer 3 against the subscreen, but the "
                + "second box moves layer 3 onto the subscreen, where there is nothing to blend "
                + "it with. For translucent mist, leave \"Move layer 3 to the subscreen\" off."
                : "";
            int i = option.SelectedIndex;
            note.Text = i <= 0 ? "This level has no layer 3."
                      : hasTilemapFor(i) ? "This level's mode has a tilemap for that."
                      : "This level's mode has no tilemap for that — the layer would stay empty.";
        }
        option.SelectionChanged += (_, _) => Sync2();
        advanced.IsCheckedChanged += (_, _) => Sync2();
        cgadsub.IsCheckedChanged += (_, _) => Sync2();
        subscreen.IsCheckedChanged += (_, _) => Sync2();
        Sync2();
    }

    /// <summary>LM shows this field in hex and accepts a leading minus; so do we, and an
    /// unparseable box means 0 rather than a refusal to close.</summary>
    private static string Hex(int y) => y < 0 ? $"-{-y:X}" : $"{y:X}";

    private static int ParseHex(string? s)
    {
        s = (s ?? "").Trim();
        bool neg = s.StartsWith('-');
        return int.TryParse(neg ? s[1..] : s, System.Globalization.NumberStyles.HexNumber,
                            null, out int v) ? Math.Clamp(neg ? -v : v, Layer3.MinY, Layer3.MaxY) : 0;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Commit();
        Close();
    }

    /// <summary>What OK reads out of the controls, split from closing the window so a test can
    /// check the reading without a window that was never shown.</summary>
    private void Commit()
    {
        if (option.SelectedIndex >= 0)
            Result = (option.SelectedIndex, priority.IsChecked == true,
                      advanced.IsChecked == true
                        ? new Layer3.Advanced(cgadsub.IsChecked == true, subscreen.IsChecked == true,
                                              sync.IsChecked == true,
                                              Math.Max(0, vscroll.SelectedIndex),
                                              Math.Max(0, hscroll.SelectedIndex),
                                              Math.Max(0, xpos.SelectedIndex), ParseHex(ybox.Text))
                        : null);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
