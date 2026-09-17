using System.Windows;
using System.Windows.Input;
using YTNotifier.Services;

namespace YTNotifier.Views;

public partial class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow(Window owner, string tag, string markdownBody)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowCornerHelper.Apply(this);
        Owner  = owner;
        HeaderText.Text = $"リリースノート {tag}";
        NotesViewer.Markdown = markdownBody;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
