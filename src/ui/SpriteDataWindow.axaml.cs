using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>
/// What one placed sprite IS: number, extra bits, and Lunar Magic's extra bytes — the fields of
/// LM's "insert sprite manually" dialog, for the selected sprite. Position stays the canvas's job.
/// </summary>
public partial class SpriteDataWindow : Window
{
    /// <summary>The data to write, when Apply was pressed.</summary>
    public (int Number, int Extra, byte[]? ExtraBytes)? Applied { get; private set; }

    private TextBox numberBox = null!, extraBox = null!, bytesBox = null!;
    private TextBlock bytes = null!;

    public SpriteDataWindow() => AvaloniaXamlLoader.Load(this);

    internal SpriteDataWindow(Sprite s) : this()
    {
        numberBox = this.GetControl<TextBox>("NumberBox");
        extraBox = this.GetControl<TextBox>("ExtraBox");
        bytesBox = this.GetControl<TextBox>("BytesBox");
        bytes = this.GetControl<TextBlock>("Bytes");
        numberBox.Text = s.Number.ToString("X2");
        extraBox.Text = s.Extra.ToString();
        bytesBox.Text = s.ExtraBytes is { } eb ? string.Join(' ', eb.Select(b => b.ToString("X2"))) : "";
        foreach (var box in new[] { numberBox, extraBox, bytesBox }) box.TextChanged += (_, _) => Show();
        Show();
    }

    /// <summary>Parse the fields; null when the extra bytes are not hex or exceed LM's cap (0xF
    /// bytes per record, 3 of them the base).</summary>
    internal static (int Number, int Extra, byte[]? ExtraBytes)? Parse(string? number, string? extra, string? hex)
    {
        if (!int.TryParse(number, System.Globalization.NumberStyles.HexNumber, null, out int n) || n is < 0 or > 0xFF) return null;
        if (!int.TryParse(extra, out int x) || x is < 0 or > 3) return null;
        var parts = (hex ?? "").Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 12) return null;
        var eb = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out eb[i])) return null;
        return (n, x, eb.Length > 0 ? eb : null);
    }

    private void Show()
    {
        var p = Parse(numberBox.Text, extraBox.Text, bytesBox.Text);
        bytes.Text = p is { } d ? $"record: {3 + (d.ExtraBytes?.Length ?? 0)} bytes" : "not a valid record";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (Parse(numberBox.Text, extraBox.Text, bytesBox.Text) is not { } d) return;
        Applied = d;
        Close();
    }
}
