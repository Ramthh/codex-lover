using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor;

public sealed class FeedbackWindow : Window
{
    public FeedbackWindow(MembershipService membership)
    {
        Title = "Share your thoughts · Codex Lover";
        Width = 480; Height = Math.Min(440, SystemParameters.WorkArea.Height - 30);
        MinWidth = 360; MinHeight = 300; ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(13, 21, 28)); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(25) };
        panel.Children.Add(new TextBlock { Text = "Help make Codex Lover better", FontSize = 21 });
        panel.Children.Add(new TextBlock { Text = "Ideas, problems, or something you love. Your feedback goes to our private admin inbox with your membership name.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16), Foreground = Brushes.LightGray });
        var input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 160, MaxLength = 4000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(23, 33, 42)), Foreground = Brushes.White,
            CaretBrush = Brushes.White, BorderBrush = Brushes.SlateGray, Padding = new Thickness(9) };
        System.Windows.Automation.AutomationProperties.SetName(input, "Your feedback");
        panel.Children.Add(input);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10), Foreground = Brushes.LightGray };
        panel.Children.Add(status);
        var send = new Button { Content = "Send feedback", Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new CancellationTokenSource();
        Closed += (_, _) => { cancel.Cancel(); cancel.Dispose(); };
        send.Click += async (_, _) =>
        {
            if (input.Text.Trim().Length < 10) { status.Text = "Please write at least 10 characters."; return; }
            send.IsEnabled = false; status.Text = "Sending…";
            try { await membership.SendFeedbackAsync(input.Text, cancel.Token); input.IsReadOnly = true; status.Text = "Thank you! Your feedback has reached the team."; send.Content = "Feedback sent"; }
            catch (OperationCanceledException) { }
            catch (MembershipRequiredException e) { status.Text = e.Message; send.IsEnabled = true; }
            catch { status.Text = "Could not send feedback. Your message is still here; please try again."; send.IsEnabled = true; }
        };
        panel.Children.Add(send); Content = new ScrollViewer { Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
