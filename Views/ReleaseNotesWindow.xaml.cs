using System.Windows;
using System.Windows.Input;
using YTNotifier.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace YTNotifier.Views;

public partial class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow(Window owner, string tag, string markdownBody)
    {
        InitializeComponent();
        WindowScaleService.ApplyToNewWindow(this);
        WindowCornerHelper.ApplyOnLoaded(this);
        Owner  = owner;
        HeaderText.Text = $"リリースノート {tag}";
        NotesViewer.Markdown = markdownBody;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        => WindowTitleBarHelper.DragMoveOnPress(this, e);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ReleaseNotesWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
