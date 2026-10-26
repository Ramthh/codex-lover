using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor.Controls;

public sealed class ResetCalendar : StackPanel
{
    private readonly TextBlock _summary = new() { Foreground = Brushes.LightGray, FontSize = 10, TextWrapping = TextWrapping.Wrap };
    private readonly UniformGrid _days = new() { Rows = 2, Columns = 15, Margin = new Thickness(0, 7, 0, 5) };
    public ResetCalendar()
    {
        Margin = new Thickness(2, 5, 2, 5);
        Children.Add(new TextBlock { Text = "Codex resets", FontSize = 11, Foreground = Brushes.LightGray });
        Children.Add(_days); Children.Add(_summary);
        SetDays(null);
    }
    public void SetDays(AppUpdates? feed)
    {
        _days.Children.Clear();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var marked = new List<DateOnly>();
        for (var i = 29; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            var record = feed?.Days.FirstOrDefault(d => d.Day == day.ToString("yyyy-MM-dd"));
            var active = record?.Marked == true;
            if (active) marked.Add(day);
            var label = $"{day:MMM d, yyyy} UTC · {(active ? "Reset marked by Codex Lover" : "No reset marked")}";
            if (active && !string.IsNullOrWhiteSpace(record?.Note)) label += " · " + record.Note;
            var item = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, ToolTip = label, Margin = new Thickness(0, 0, 0, 5) };
            System.Windows.Automation.AutomationProperties.SetName(item, label);
            item.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = active ? new SolidColorBrush(Color.FromRgb(117, 220, 177)) : new SolidColorBrush(Color.FromRgb(88, 98, 109)) });
            item.Children.Add(new TextBlock { Text = day.Day.ToString(), FontSize = 10, Foreground = active ? Brushes.White : Brushes.LightGray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) });
            _days.Children.Add(item);
        }
        _summary.Text = feed is null ? "Last 30 days · Waiting for the published calendar"
            : marked.Count > 0 ? $"Last reset marked: {marked[^1]:MMM d} · Last 30 days, UTC" : "No resets marked in the last 30 days · UTC";
        _summary.ToolTip = feed is null ? "Connect to load reset notices." : $"Published by Codex Lover, not a per-account quota reset. Last checked {feed.AsOf:u}.";
    }
}
