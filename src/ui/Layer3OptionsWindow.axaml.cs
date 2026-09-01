using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>
/// The level's layer-3 settings, which is Lunar Magic's "Change Layer 3 Settings" cut down to
/// what this editor has actually decoded: the Layer 3 Options value and the priority flag.
///
/// The two live in different records — the option is the entrance byte $05F200's top two bits,
/// the priority is header byte 2 bit 7 — and the caller applies them separately. They are one
/// dialog because they are one question to the person asking it.
///
/// <see cref="Result"/> is null on cancel.
/// </summary>
public partial class Layer3OptionsWindow : Window
{
    public (int Option, bool Priority)? Result { get; private set; }

    private ComboBox option = null!;
    private CheckBox priority = null!;

    public Layer3OptionsWindow() => AvaloniaXamlLoader.Load(this);

    /// <param name="hasTilemapFor">Whether an option value resolves to a tilemap for this
    /// level's mode — most do not for every mode, and the pane would otherwise be the first
    /// place that showed up.</param>
    public Layer3OptionsWindow(IReadOnlyList<string> options, int selected, bool priorityOn,
                               Func<int, bool> hasTilemapFor) : this()
    {
        option = this.GetControl<ComboBox>("OptionBox");
        priority = this.GetControl<CheckBox>("PriorityBox");
        var note = this.GetControl<TextBlock>("ModeNote");

        option.ItemsSource = options;
        option.SelectedIndex = Math.Clamp(selected, 0, options.Count - 1);
        priority.IsChecked = priorityOn;

        void Describe()
        {
            int i = option.SelectedIndex;
            note.Text = i <= 0 ? "This level has no layer 3."
                      : hasTilemapFor(i) ? "This level's mode has a tilemap for that."
                      : "This level's mode has no tilemap for that — the layer would stay empty.";
        }
        option.SelectionChanged += (_, _) => Describe();
        Describe();
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (option.SelectedIndex >= 0) Result = (option.SelectedIndex, priority.IsChecked == true);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
