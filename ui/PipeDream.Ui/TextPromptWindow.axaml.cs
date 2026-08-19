using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>One line of text, asked for in a real window. <see cref="Result"/> is null when
/// cancelled, which is why it is nullable rather than "" — an empty name is a valid answer.</summary>
public partial class TextPromptWindow : Window
{
    public string? Result { get; private set; }

    private TextBox entry = null!;

    public TextPromptWindow() => AvaloniaXamlLoader.Load(this);

    public TextPromptWindow(string prompt, string initial) : this()
    {
        Title = prompt;
        this.GetControl<TextBlock>("Prompt").Text = prompt;
        entry = this.GetControl<TextBox>("Entry");
        entry.Text = initial;
        Opened += (_, _) => { entry.Focus(); entry.SelectAll(); };
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        Result = entry.Text ?? "";
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
