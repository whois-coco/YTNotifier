using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;
using Brush   = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;

namespace YTNotifier.Views;

public partial class VideoListPopupWindow : Window
{
    private const string HeaderTextLive     = "配信中のライブ一覧";
    private const string HeaderTextPremiere = "公開中のプレミア一覧";
    private const double RowBottomMargin    = 10;

    private readonly ChannelInfo _channel;
    private readonly VideoKind   _kind;
    private readonly bool        _allowSummary;

    public VideoListPopupWindow(Window owner, ChannelInfo channel, VideoKind kind, List<PendingVideoEntry> entries, bool allowSummary = true)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowCornerHelper.Apply(this);
        Owner         = owner;
        _channel      = channel;
        _kind         = kind;
        _allowSummary = allowSummary;

        HeaderText.Text = kind == VideoKind.Premiere ? HeaderTextPremiere : HeaderTextLive;

        foreach (var entry in entries)
        {
            var titleText = new TextBlock
            {
                Text = entry.Title,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, RowBottomMargin),
                Foreground = (Brush)TryFindResource("TextPrimaryBrush")
            };
            titleText.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                new VideoSummaryPopupWindow(this, _channel, _kind, entry.Title, entry.VideoId, allowSummary: _allowSummary).Show();
            };
            EntriesPanel.Children.Add(titleText);
        }

        AppLogger.Log(LogMsg.VideoListPopupOpened, null, channel.ChannelName, entries.Count);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
