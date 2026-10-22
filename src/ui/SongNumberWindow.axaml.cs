using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>A song's number and section, asked for in a real window. <see cref="Result"/> is null when
/// cancelled; <see cref="Cleared"/> says Clear was pressed, so the song loses its number.</summary>
public partial class SongNumberWindow : Window
{
    public (int Number, bool Global)? Result { get; private set; }
    public bool Cleared { get; private set; }

    private TextBox entry = null!;
    private RadioButton globalButton = null!;

    public SongNumberWindow() => AvaloniaXamlLoader.Load(this);

    /// <summary>Opens on the song's current number; Clear is only there when it has one.</summary>
    public SongNumberWindow((int Number, bool Global)? current) : this()
    {
        entry = this.GetControl<TextBox>("Entry");
        globalButton = this.GetControl<RadioButton>("GlobalButton");
        this.GetControl<Button>("ClearButton").IsEnabled = current is not null;
        if (current is { } c) { entry.Text = c.Number.ToString("X2"); globalButton.IsChecked = c.Global; }
        Opened += (_, _) => { entry.Focus(); entry.SelectAll(); };
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(entry.Text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int n) || n is < 1 or > 0xFF)
        {
            var problem = this.GetControl<TextBlock>("Problem");
            problem.Text = "a song number is 01-FF";
            problem.IsVisible = true;
            return;
        }
        Result = (n, globalButton.IsChecked == true);
        Close();
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        Cleared = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
