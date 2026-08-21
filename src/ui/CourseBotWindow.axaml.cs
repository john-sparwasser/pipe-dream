using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PipeDream.Ui;

/// <summary>
/// Manage Course Bot levels: named handles on entry-level slots, so courses are picked by name
/// instead of number. Create copies a chosen base level into a free slot, Open jumps the editor
/// there, Delete frees the slot back to the base ROM.
/// </summary>
public partial class CourseBotWindow : Window
{
    public sealed class Row(int level, string name)
    {
        public int Level => level;
        public string Name => name;
        public string Slot => $"${level:X3}";
    }

    /// <summary>The level to open in the editor, or null when the window was just closed.</summary>
    public int? Picked { get; private set; }

    private readonly EditorSession session = null!;
    private ListBox courses = null!;
    private TextBox newName = null!;
    private ComboBox baseLevel = null!;
    private Button deleteButton = null!;
    private TextBlock hint = null!;

    /// <summary>Parameterless for the XAML loader; the real entry point is the other one.</summary>
    public CourseBotWindow() => AvaloniaXamlLoader.Load(this);

    internal CourseBotWindow(EditorSession session) : this()
    {
        this.session = session;
        courses = this.GetControl<ListBox>("Courses");
        newName = this.GetControl<TextBox>("NewName");
        baseLevel = this.GetControl<ComboBox>("BaseLevel");
        deleteButton = this.GetControl<Button>("DeleteButton");
        hint = this.GetControl<TextBlock>("Hint");

        // Deleting is destructive, so the button arms on the first click and fires on the
        // second; moving the selection disarms it.
        courses.SelectionChanged += (_, _) => deleteButton.Content = "Delete";
        PopulateBases();
        Refresh();
    }

    /// <summary>Base picker: the enterable pool, labelled with course names where slots have
    /// them — a course can itself be the base, which is how one is duplicated.</summary>
    private void PopulateBases()
    {
        int keep = baseLevel.SelectedIndex;
        baseLevel.ItemsSource = EditorSession.EnterableLevels()
            .Select(l => session.CourseBotName(l) is { } n ? $"${l:X3} — {n}" : $"${l:X3}")
            .ToList();
        baseLevel.SelectedIndex = Math.Max(0, keep);
    }

    /// <summary>Refill the list. <paramref name="want"/> is the slot to land on — a freshly
    /// created course — otherwise the selection is kept where it was.</summary>
    private void Refresh(int? want = null)
    {
        int? keep = want ?? (courses.SelectedItem as Row)?.Level;
        var rows = session.CourseBotEntries.Select(e => new Row(e.Level, e.Name)).ToList();
        courses.ItemsSource = rows;
        courses.SelectedItem = rows.FirstOrDefault(r => r.Level == keep) ?? rows.FirstOrDefault();
        if (rows.Count == 0) hint.Text = "no courses yet — name one and Create";
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (baseLevel.SelectedIndex is not (>= 0 and var idx)) return;
        int slot = session.CreateCourseBotLevel(newName.Text ?? "",
                                                EditorSession.EnterableLevels().ElementAt(idx));
        hint.Text = session.Status;
        if (slot < 0) return;
        newName.Text = "";
        PopulateBases();          // the new slot's label now carries its course name
        Refresh(slot);
    }

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (courses.SelectedItem is not Row r) return;
        Picked = r.Level;
        Close();
    }

    /// <summary>Double-click a course to open it.</summary>
    private void OnOpen(object? sender, TappedEventArgs e) => OnOpen(sender, (RoutedEventArgs)e);

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (courses.SelectedItem is not Row r) return;
        if (deleteButton.Content as string != "Sure?")
        {
            deleteButton.Content = "Sure?";
            hint.Text = $"deletes \"{r.Name}\" and reverts ${r.Level:X3} to the base ROM";
            return;
        }
        deleteButton.Content = "Delete";
        hint.Text = session.DeleteCourseBotLevel(r.Level);
        PopulateBases();
        Refresh();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
