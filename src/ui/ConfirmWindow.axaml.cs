using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>A yes/no modal: <see cref="Confirmed"/> is true only when the action button was
/// pressed. Esc and closing the window are both a cancel, like every dialog here.</summary>
public partial class ConfirmWindow : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmWindow() => AvaloniaXamlLoader.Load(this);

    public ConfirmWindow(string title, string prompt, string action) : this()
    {
        Title = title;
        this.GetControl<TextBlock>("Prompt").Text = prompt;
        this.GetControl<Button>("Action").Content = action;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    private void OnConfirm(object? sender, RoutedEventArgs e) { Confirmed = true; Close(); }
}
