using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>Pick a number from a short list, shown in hex: an ExAnimation slot's free numbers, or
/// the events the Events tab can start. <see cref="Result"/> is null when cancelled.</summary>
public partial class SlotNumberWindow : Window
{
    public int? Result { get; private set; }

    private ComboBox numbers = null!;
    private IReadOnlyList<int> choices = [];

    public SlotNumberWindow() => AvaloniaXamlLoader.Load(this);

    public SlotNumberWindow(int current, IReadOnlyList<int> free)
        : this("Reassign slot", $"Move slot {current:X2} to which free slot number?", free) { }

    public SlotNumberWindow(string title, string prompt, IReadOnlyList<int> choices) : this()
    {
        Title = title;
        this.GetControl<TextBlock>("Prompt").Text = prompt;
        this.choices = choices;
        numbers = this.GetControl<ComboBox>("Numbers");
        numbers.ItemsSource = choices.Select(i => $"{i:X2}").ToList();
        numbers.SelectedIndex = 0;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (numbers.SelectedIndex >= 0) Result = choices[numbers.SelectedIndex];
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
