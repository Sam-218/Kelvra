using System.Windows;
using System.Windows.Controls;

namespace Kelvra;

/// <summary>AIDA64-style summary: Windows, CPU, GPU, RAM sticks, board/BIOS, drives (with health) and network.</summary>
public partial class SystemPage : UserControl
{
    private List<InfoSection> _sections = new();
    private bool _loaded;
    private bool _loading;

    public SystemPage()
    {
        InitializeComponent();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && !_loaded) await LoadAsync();
        };
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        LoadingText.Visibility = Visibility.Visible;
        try
        {
            var sensors = App.Current.Sensors.All.ToList(); // snapshot for the worker thread
            _sections = await Task.Run(() => SystemInfo.Collect(sensors));
            Sections.ItemsSource = _sections;
            Subtitle.Text = $"A summary of your PC's hardware and Windows · collected {DateTime.Now:HH:mm}";
            _loaded = true;
        }
        finally
        {
            _loading = false;
            LoadingText.Visibility = Visibility.Collapsed;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_sections.Count == 0) return;
        Clipboard.SetText(SystemInfo.ToText(_sections));
        Subtitle.Text = "Copied to the clipboard.";
    }
}
