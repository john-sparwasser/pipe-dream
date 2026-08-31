using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>Reassign an ExAnimation slot's number: a dropdown of the list's free slot numbers.
/// <see cref="Result"/> is null when cancelled.</summary>
public partial class SlotNumberWindow : Window
{
    public int? Result { get; private set; }

    private ComboBox numbers = null!;
    private IReadOnlyList<int> choices = [];

    public SlotNumberWindow() => AvaloniaXamlLoader.Load(this);

    public SlotNumberWindow(int current, IReadOnlyList<int> free) : this()
    {
        Title = "Reassign slot";
        this.GetControl<TextBlock>("Prompt").Text
            = $"Move slot {current:X2} to which free slot number?";
        choices = free;
        numbers = this.GetControl<ComboBox>("Numbers");
        numbers.ItemsSource = free.Select(i => $"{i:X2}").ToList();
        numbers.SelectedIndex = 0;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (numbers.SelectedIndex >= 0) Result = choices[numbers.SelectedIndex];
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
