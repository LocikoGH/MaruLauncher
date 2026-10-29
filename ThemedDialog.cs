using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MaruLauncher;

public static class ThemedDialog
{
    public static MessageBoxResult Show(
        Window owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None)
    {
        var dialog = new Window
        {
            Title = title,
            Owner = owner,
            Width = 540,
            MinWidth = 420,
            MaxWidth = 720,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)Application.Current.FindResource("SurfaceBrush"),
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            FontFamily = new FontFamily("Segoe UI")
        };
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource(
                image is MessageBoxImage.Error or MessageBoxImage.Warning
                    ? "DangerBrush"
                    : "AccentBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(heading);

        var content = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            Margin = new Thickness(1, 0, 1, 14)
        };
        var scroll = new ScrollViewer
        {
            Content = content,
            MaxHeight = 440,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        foreach (var (caption, result) in GetButtons(buttons))
        {
            var button = new Button
            {
                Content = caption,
                MinWidth = 84,
                Margin = new Thickness(5, 4, 0, 0),
                IsDefault = result == (buttons == MessageBoxButton.YesNo
                    ? MessageBoxResult.Yes
                    : MessageBoxResult.OK),
                IsCancel = result == (buttons == MessageBoxButton.YesNo
                    ? MessageBoxResult.No
                    : MessageBoxResult.Cancel)
            };
            if (button.IsDefault)
                button.Style = (Style)Application.Current.FindResource("PrimaryButton");
            button.Click += (_, _) =>
            {
                dialog.Tag = result;
                dialog.DialogResult = result != MessageBoxResult.Cancel;
            };
            buttonsPanel.Children.Add(button);
        }
        Grid.SetRow(buttonsPanel, 2);
        root.Children.Add(buttonsPanel);
        dialog.Content = root;
        dialog.ShowDialog();
        return dialog.Tag is MessageBoxResult selectedResult ? selectedResult : MessageBoxResult.Cancel;
    }

    private static IReadOnlyList<(string Caption, MessageBoxResult Result)> GetButtons(MessageBoxButton buttons) =>
        buttons switch
        {
            MessageBoxButton.OK => [("OK", MessageBoxResult.OK)],
            MessageBoxButton.OKCancel =>
                [("Отмена", MessageBoxResult.Cancel), ("OK", MessageBoxResult.OK)],
            MessageBoxButton.YesNo =>
                [("Нет", MessageBoxResult.No), ("Да", MessageBoxResult.Yes)],
            MessageBoxButton.YesNoCancel =>
                [("Отмена", MessageBoxResult.Cancel), ("Нет", MessageBoxResult.No), ("Да", MessageBoxResult.Yes)],
            _ => [("OK", MessageBoxResult.OK)]
        };
}
