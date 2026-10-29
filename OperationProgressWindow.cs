using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MaruLauncher;

public sealed class OperationProgressWindow : Window
{
    private readonly DownloadOperation _operation;
    private readonly TextBlock _titleText;
    private readonly TextBlock _stageText;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _progressDetails;
    private readonly DispatcherTimer _timer;

    public OperationProgressWindow(DownloadOperation operation, Window owner)
    {
        _operation = operation;
        Owner = owner;
        Title = "Выполняется операция";
        Width = 480;
        MinWidth = 400;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("SurfaceBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");

        var content = new StackPanel { Margin = new Thickness(22) };
        _titleText = new TextBlock
        {
            Text = operation.Name,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("TextBrush")
        };
        _stageText = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("MutedBrush")
        };
        _progressBar = new ProgressBar
        {
            Height = 14,
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true,
            Background = (Brush)Application.Current.FindResource("PanelBrush"),
            Foreground = (Brush)Application.Current.FindResource("AccentBrush")
        };
        _progressDetails = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("MutedBrush")
        };

        content.Children.Add(_titleText);
        content.Children.Add(_stageText);
        content.Children.Add(_progressBar);
        content.Children.Add(_progressDetails);
        Content = content;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => RefreshProgress();
        Closed += (_, _) => _timer.Stop();
        RefreshProgress();
    }

    private void RefreshProgress()
    {
        _stageText.Text = $"Экземпляр: {_operation.InstanceName}\nЭтап: {_operation.Stage}";
        _progressDetails.Text = _operation.ProgressText;
        if (_operation.TotalBytes is long total && total > 0)
        {
            var received = Math.Clamp(_operation.BytesReceived ?? 0, 0, total);
            _progressBar.IsIndeterminate = false;
            _progressBar.Value = received * 100d / total;
            _progressDetails.Text += $" · {_progressBar.Value:0}%";
        }
        else
        {
            _progressBar.IsIndeterminate = !_operation.IsComplete;
            _progressBar.Value = _operation.IsComplete ? 100 : 0;
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _timer.Start();
    }
}
