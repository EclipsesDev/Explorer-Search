using ExplorerSearch.Core.Models;
using ExplorerSearch.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.System;
using WinRT.Interop;

namespace ExplorerSearch.App;

public sealed partial class MainWindow : Window
{
    private readonly IFileSearchService _searchService = new FileSystemSearchService();
    private CancellationTokenSource? _searchCts;
    private bool _isIndexing;
    private ProgressBar? _indexProgressBar;
    private TextBlock? _indexEtaTextBlock;

    public ObservableCollection<FileSearchResult> Results { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        ResolveOptionalUiElements();
        SetWindowIcon();

        Title = "Explorer Search (v1.0.0)";
        RootPathTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ResultsListView.ItemsSource = Results;
        _ = WarmupIndexAsync();
    }

    private async Task WarmupIndexAsync()
    {
        if (_searchService is not FileSystemSearchService indexedService)
        {
            return;
        }

        var rootPath = (RootPathTextBox.Text ?? string.Empty).Trim();
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        try
        {
            SetIndexingState(true);
            StatusTextBlock.Text = "Building search index...";

            var progress = new Progress<IndexBuildProgress>(p =>
            {
                if (p.TotalFiles > 0)
                {
                    if (_indexProgressBar is not null)
                    {
                        _indexProgressBar.IsIndeterminate = false;
                        _indexProgressBar.Maximum = p.TotalFiles;
                        _indexProgressBar.Value = Math.Min(p.ProcessedFiles, p.TotalFiles);
                    }

                    if (_indexEtaTextBlock is not null)
                    {
                        _indexEtaTextBlock.Text = $"{p.ProcessedFiles:N0}/{p.TotalFiles:N0} • ETA {FormatEta(p.EstimatedRemaining)}";
                    }
                }
                else
                {
                    if (_indexProgressBar is not null)
                    {
                        _indexProgressBar.IsIndeterminate = true;
                    }

                    if (_indexEtaTextBlock is not null)
                    {
                        _indexEtaTextBlock.Text = "Counting files...";
                    }
                }
            });

            await indexedService.WarmupIndexAsync(new[] { rootPath }, progress);
            if (!BusyRing.IsActive)
            {
                StatusTextBlock.Text = "Index ready.";
            }
        }
        catch
        {
            if (!BusyRing.IsActive)
            {
                StatusTextBlock.Text = "Index warmup skipped.";
            }
        }
        finally
        {
            SetIndexingState(false);
            if (_indexProgressBar is not null)
            {
                _indexProgressBar.Value = 0;
            }

            if (_indexEtaTextBlock is not null)
            {
                _indexEtaTextBlock.Text = string.Empty;
            }
        }
    }

    private void ResolveOptionalUiElements()
    {
        if (Content is not FrameworkElement root)
        {
            return;
        }

        _indexProgressBar = root.FindName("IndexProgressBar") as ProgressBar;
        _indexEtaTextBlock = root.FindName("IndexEtaTextBlock") as TextBlock;
    }

    private void SetWindowIcon()
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "favicon.ico");
        appWindow.SetIcon(iconPath);
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await RunSearchAsync();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _searchCts?.Cancel();
    }

    private async void QueryTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        var query = QueryTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            StatusTextBlock.Text = "Type a file name query first.";
            return;
        }

        var rootPath = (RootPathTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            rootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (!Directory.Exists(rootPath))
        {
            StatusTextBlock.Text = "Root folder does not exist.";
            return;
        }

        if (!int.TryParse(MaxResultsTextBox.Text, out var maxResults) || maxResults <= 0)
        {
            maxResults = 200;
            MaxResultsTextBox.Text = maxResults.ToString();
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        BusyRing.IsActive = true;
        SearchButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusTextBlock.Text = "Searching...";

        try
        {
            var options = new SearchOptions
            {
                RootDirectories = new[] { rootPath },
                IncludeSubdirectories = IncludeSubfoldersCheckBox.IsChecked ?? true,
                MaxResults = maxResults
            };

            var foundItems = await _searchService.SearchAsync(query, options, _searchCts.Token);

            Results.Clear();
            foreach (var item in foundItems.OrderBy(x => x.Name))
            {
                Results.Add(item);
            }

            StatusTextBlock.Text = $"Found {Results.Count} item(s).";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Search cancelled.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Search error: {ex.Message}";
        }
        finally
        {
            BusyRing.IsActive = false;
            SearchButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void SetIndexingState(bool isIndexing)
    {
        _isIndexing = isIndexing;
        SearchButton.IsEnabled = !isIndexing;

        if (_indexProgressBar is not null)
        {
            _indexProgressBar.Visibility = isIndexing ? Visibility.Visible : Visibility.Collapsed;
            _indexProgressBar.IsIndeterminate = isIndexing;
        }

        if (_indexEtaTextBlock is not null)
        {
            _indexEtaTextBlock.Visibility = isIndexing ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static string FormatEta(TimeSpan? eta)
    {
        if (!eta.HasValue)
        {
            return "calculating...";
        }

        var value = eta.Value;
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";
        }

        return $"{value.Minutes:D2}:{value.Seconds:D2}";
    }

    private void ResultsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileSearchResult selected)
        {
            OpenInExplorer(selected.FullPath);
        }
    }

    private static void OpenInExplorer(string fullPath)
    {
        var escapedPath = fullPath.Replace("\"", "\"\"");

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{escapedPath}\"",
            UseShellExecute = true
        });
    }
}