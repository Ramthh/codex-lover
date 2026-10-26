using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor.Controls;

public sealed class NewsTicker : Grid
{
    private readonly TextBlock _text = new() { Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly TranslateTransform _translation = new();
    private readonly DispatcherTimer _rotation = new() { Interval = TimeSpan.FromSeconds(35) };
    private IReadOnlyList<NewsArticle> _articles = [];
    private int _index;
    private bool _paused;
    public NewsTicker()
    {
        Height = 30; ClipToBounds = true; Cursor = Cursors.Hand; Focusable = true;
        Background = new SolidColorBrush(Color.FromRgb(18, 31, 39));
        _text.RenderTransform = _translation;
        Children.Add(_text);
        _rotation.Tick += (_, _) => { _index++; ShowCurrent(); };
        Loaded += (_, _) => { _rotation.Start(); ShowCurrent(); };
        Unloaded += (_, _) => { _rotation.Stop(); _translation.BeginAnimation(TranslateTransform.XProperty, null); };
        SizeChanged += (_, _) => ShowCurrent();
        MouseEnter += (_, _) => Pause();
        MouseLeave += (_, _) => Resume();
        GotKeyboardFocus += (_, _) => Pause();
        LostKeyboardFocus += (_, _) => Resume();
        MouseLeftButtonUp += (_, _) => OpenArticle();
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) OpenArticle();
            if (args.Key == Key.Space) { if (_paused) Resume(); else Pause(); args.Handled = true; }
        };
        ToolTip = "Codex Lover news · click or press Enter to read · hover or press Space to pause";
    }
    public void SetArticles(IReadOnlyList<NewsArticle> articles) { _articles = articles; _index = 0; ShowCurrent(); }
    private void ShowCurrent()
    {
        _translation.BeginAnimation(TranslateTransform.XProperty, null);
        _text.Text = _articles.Count == 0 ? "Codex Lover news · Updates will appear here" : _articles[_index % _articles.Count].Title;
        System.Windows.Automation.AutomationProperties.SetName(this, _text.Text);
        _text.Measure(new Size(double.PositiveInfinity, 30));
        if (_articles.Count == 0 || !SystemParameters.ClientAreaAnimation || _paused) { _translation.X = 8; return; }
        // One full, slow left-to-right pass per headline. Reduced Windows animation stays static.
        var animation = new DoubleAnimation(-_text.DesiredSize.Width, Math.Max(ActualWidth, 100), TimeSpan.FromSeconds(35));
        _translation.BeginAnimation(TranslateTransform.XProperty, animation);
    }
    private void Pause() { _paused = true; _rotation.Stop(); ShowCurrent(); }
    private void Resume() { _paused = false; _rotation.Start(); ShowCurrent(); }
    private void OpenArticle()
    {
        if (_articles.Count == 0) return;
        var article = _articles[_index % _articles.Count];
        if (MembershipService.IsWebsiteUri(article.Url, out var uri))
            try { MembershipService.OpenWebsite(uri!); } catch { }
    }
}
