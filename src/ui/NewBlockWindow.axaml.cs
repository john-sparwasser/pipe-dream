using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using PipeDream.Services;

namespace PipeDream.Ui;

/// <summary>Name a new library block and pick its icon. Okay adds it from the template (Edit then
/// opens that boilerplate); Cancel, Esc and closing add nothing. <see cref="Made"/> is the
/// block's name once added.</summary>
public partial class NewBlockWindow : Window
{
    /// <summary>One icon choice: a vanilla Map16 tile, as <see cref="EditorSession.BlockIconTiles"/> lists them.</summary>
    public sealed record IconRow(int Tile, Bitmap? Image)
    {
        public string Tip => $"Map16 tile {Tile:X3}";
    }

    public string? Made { get; private set; }

    private readonly EditorSession session = null!;
    private TextBox name = null!;
    private ListBox icons = null!;
    private TextBlock hint = null!;

    public NewBlockWindow() => AvaloniaXamlLoader.Load(this);

    internal NewBlockWindow(EditorSession session) : this()
    {
        this.session = session;
        name = this.GetControl<TextBox>("BlockName");
        icons = this.GetControl<ListBox>("Icons");
        hint = this.GetControl<TextBlock>("Hint");
        var rows = EditorSession.BlockIconTiles.Select(t => new IconRow(t, IconFor(session, t))).ToList();
        icons.ItemsSource = rows;
        icons.SelectedItem = rows[0];
        Opened += (_, _) => name.Focus();
    }

    /// <summary>A block icon as a bitmap, or null with no level open to draw it in.</summary>
    internal static Bitmap? IconFor(EditorSession session, int? tile)
        => tile is { } t && session.Map16TilePixels(t) is { } px ? LevelBitmap.FromPixels(px, 16, 16) : null;

    private void OnOkay(object? sender, RoutedEventArgs e)
    {
        hint.Text = session.NewBlock(name.Text ?? "", out var made);
        if (made is null) return;                                // the hint says why
        session.SetBlockIcon(made, (icons.SelectedItem as IconRow)?.Tile);
        Made = made;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
