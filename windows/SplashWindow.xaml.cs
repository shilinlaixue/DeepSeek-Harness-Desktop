using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeepSeekDesktop;

/// <summary>
/// Startup animation: whale fades in, the two title lines type in, then the
/// status line reports real progress. On failure it stays open and offers
/// Retry / Quit instead of flashing a MessageBox and exiting.
/// </summary>
public partial class SplashWindow : Window
{
    private const string Line1Text = "DeepSeek Harness";
    private const string Line2Text = "for Windows";

    private readonly List<TextBlock> _line1Blocks = new();
    private readonly List<TextBlock> _line2Blocks = new();
    private readonly System.Windows.Threading.DispatcherTimer _letterTimer = new();
    private int _letterIndex;

    public event Action? RetryRequested;
    public event Action? QuitRequested;

    public SplashWindow()
    {
        InitializeComponent();

        BuildLetterLine(Line1Panel, Line1Text, 30, FontWeights.Bold, Colors.Black, _line1Blocks);
        BuildLetterLine(Line2Panel, Line2Text, 20, FontWeights.SemiBold, Color.FromRgb(0x40, 0x40, 0x40), _line2Blocks);

        Loaded += OnLoaded;
    }

    public void SetStatus(string text)
    {
        SubtitleText.Text = text;
    }

    public void ShowError(string message)
    {
        SubtitleText.Text = "启动失败：" + message;
        SubtitleText.Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x1E, 0x1E));
        ErrorActions.Visibility = Visibility.Visible;
    }

    public void ShowErrorActions(bool visible)
    {
        ErrorActions.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildLetterLine(StackPanel panel, string text, double size,
        FontWeight weight, Color color, List<TextBlock> blocks)
    {
        foreach (var ch in text)
        {
            var tb = new TextBlock
            {
                Text = ch.ToString(),
                FontSize = size,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                Opacity = 0,
            };
            panel.Children.Add(tb);
            blocks.Add(tb);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var whaleFade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.5))
        { BeginTime = TimeSpan.FromSeconds(0.15) };
        var whaleScale = new DoubleAnimation(0.92, 1.0, TimeSpan.FromSeconds(0.5))
        { BeginTime = TimeSpan.FromSeconds(0.15) };
        WhaleImage.BeginAnimation(OpacityProperty, whaleFade);
        WhaleScale.BeginAnimation(ScaleTransform.ScaleXProperty, whaleScale);
        WhaleScale.BeginAnimation(ScaleTransform.ScaleYProperty, whaleScale);

        _letterTimer.Interval = TimeSpan.FromMilliseconds(55);
        _letterTimer.Tick += (_, _) => ShowNextLetter();
        Dispatcher.BeginInvoke(() => _letterTimer.Start(), System.Windows.Threading.DispatcherPriority.Loaded,
            new object[] { });

        var authorFade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.4))
        { BeginTime = TimeSpan.FromSeconds(2.3) };
        AuthorPanel.BeginAnimation(OpacityProperty, authorFade);
    }

    private void ShowNextLetter()
    {
        if (_letterIndex >= _line1Blocks.Count + _line2Blocks.Count)
        {
            _letterTimer.Stop();
            return;
        }

        var block = _letterIndex < _line1Blocks.Count
            ? _line1Blocks[_letterIndex]
            : _line2Blocks[_letterIndex - _line1Blocks.Count];

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        block.BeginAnimation(OpacityProperty, fade);

        _letterIndex++;
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorActions.Visibility = Visibility.Collapsed;
        SubtitleText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        SubtitleText.Text = "正在重试…";
        RetryRequested?.Invoke();
    }

    private void QuitButton_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();
}
