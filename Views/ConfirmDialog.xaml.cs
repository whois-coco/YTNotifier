using System.Windows;

namespace YTNotifier.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(Window owner, string title, string message,
                         string okLabel = "OK", string cancelLabel = "キャンセル")
    {
        InitializeComponent();
        Owner          = owner;
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content     = okLabel;
        CancelButton.Content = cancelLabel;
    }

    // OK/キャンセルのみの場合（キャンセル不要）
    public static bool? Show(Window owner, string title, string message,
                              string okLabel = "OK", string cancelLabel = "キャンセル",
                              bool showCancel = true)
    {
        var dlg = new ConfirmDialog(owner, title, message, okLabel, cancelLabel);
        dlg.CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        return dlg.ShowDialog();
    }

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)     { DialogResult = true;  Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
