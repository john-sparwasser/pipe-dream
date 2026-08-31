using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>The level's graphics header: which FG/BG tileset (header "layer 1" setting) and
/// sprite set the level loads. Items are prebuilt strings — setting number plus the GFX files
/// it maps to — so the window stays a dumb picker. <see cref="Result"/> is null on cancel.</summary>
public partial class GfxHeaderWindow : Window
{
    public (int Tileset, int SpriteSet)? Result { get; private set; }

    private ComboBox layer1 = null!, sprites = null!;

    public GfxHeaderWindow() => AvaloniaXamlLoader.Load(this);

    public GfxHeaderWindow(IReadOnlyList<string> layer1Items, int tileset,
                           IReadOnlyList<string> spriteItems, int spriteSet) : this()
    {
        layer1 = this.GetControl<ComboBox>("Layer1Box");
        sprites = this.GetControl<ComboBox>("SpriteBox");
        layer1.ItemsSource = layer1Items;
        sprites.ItemsSource = spriteItems;
        layer1.SelectedIndex = tileset;
        sprites.SelectedIndex = spriteSet;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (layer1.SelectedIndex >= 0 && sprites.SelectedIndex >= 0)
            Result = (layer1.SelectedIndex, sprites.SelectedIndex);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
