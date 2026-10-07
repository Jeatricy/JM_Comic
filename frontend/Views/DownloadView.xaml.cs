using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace JMComicClient.Views
{
    public partial class DownloadView : Page
    {
        private static readonly List<string> _activeJobs = new();
        private DispatcherTimer _pollTimer;

        public DownloadView()
        {
            InitializeComponent();
            Loaded += DownloadView_Loaded;
            Unloaded += DownloadView_Unloaded;
        }

        private void DownloadView_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshJobs();
            // Auto-poll every 2 seconds
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pollTimer.Tick += (s, args) => RefreshJobs();
            _pollTimer.Start();
        }

        private void DownloadView_Unloaded(object sender, RoutedEventArgs e)
        {
            _pollTimer?.Stop();
        }

        private async void RefreshJobs()
        {
            if (_activeJobs.Count == 0)
            {
                EmptyText.Visibility = Visibility.Visible;
                return;
            }
            EmptyText.Visibility = Visibility.Collapsed;

            // Preserve scroll position
            var scrollViewer = DownloadList.Parent as ScrollViewer;
            var scrollPos = scrollViewer?.VerticalOffset ?? 0;

            DownloadList.Children.Clear();
            bool hasRunning = false;

            foreach (var jobId in _activeJobs.ToArray())
            {
                try
                {
                    var status = await App.Api.GetDownloadStatusAsync(jobId);
                    if (status == null) continue;

                    if (status.Status == "running" || status.Status == "downloading")
                        hasRunning = true;

                    DownloadList.Children.CreateJobCard(jobId, status, OnCancel);
                }
                catch { }
            }

            // Stop polling if nothing is running
            if (!hasRunning)
                _pollTimer?.Stop();

            // Restore scroll position
            scrollViewer?.ScrollToVerticalOffset(scrollPos);
        }

        private async void OnCancel(string jobId)
        {
            try
            {
                await App.Api.CancelDownloadAsync(jobId);
                RefreshJobs();
            }
            catch { }
        }

        private async void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settings = await App.Api.GetSettingsAsync();
                var dir = settings?.DownloadDir ?? "downloads";
                if (!System.IO.Path.IsPathRooted(dir))
                    dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dir);
                if (System.IO.Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                else
                    MessageBox.Show($"文件夹不存在: {dir}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearCompleted_Click(object sender, RoutedEventArgs e)
        {
            // Only remove non-running jobs
            _activeJobs.RemoveAll(jid => true);
            DownloadList.Children.Clear();
            EmptyText.Visibility = Visibility.Visible;
        }

        public static void RegisterJob(string jobId)
        {
            if (!_activeJobs.Contains(jobId))
                _activeJobs.Add(jobId);
        }
    }

    // ── Helper to create job cards ───────────────────────────────
    internal static class JobCardHelper
    {
        public static void CreateJobCard(this UIElementCollection collection,
            string jobId, Models.DownloadStatus status, Action<string> onCancel)
        {
            var border = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, Direction = 270,
                    ShadowDepth = 1, Opacity = 0.08, BlurRadius = 6,
                },
            };

            var mainStack = new StackPanel();

            // Title row
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition());
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleText = new TextBlock
            {
                Text = string.IsNullOrEmpty(status?.Title) ? $"JM{status?.AlbumId ?? "?"}" : status.Title,
                FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(titleText, 0);
            titleGrid.Children.Add(titleText);

            var statusColor = status?.Status switch
            {
                "completed" => Color.FromRgb(0x4C, 0xAF, 0x50),
                "running" or "downloading" => Color.FromRgb(0xFF, 0x98, 0x00),
                "error" => Color.FromRgb(0xF4, 0x43, 0x36),
                "cancelled" => Color.FromRgb(0x9E, 0x9E, 0x9E),
                _ => Colors.Black,
            };
            var statusLabel = status?.Status switch
            {
                "completed" => "✅ 已完成",
                "running" or "downloading" => "⏳ 下载中...",
                "error" => $"❌ {status?.Error}",
                "cancelled" => "🚫 已取消",
                _ => status?.Status ?? "未知",
            };
            var statusText = new TextBlock
            {
                Text = statusLabel, FontSize = 13,
                Foreground = new SolidColorBrush(statusColor),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(statusText, 1);
            titleGrid.Children.Add(statusText);
            mainStack.Children.Add(titleGrid);

            // Progress bar for running downloads
            if (status?.Status == "running" || status?.Status == "downloading")
            {
                var progressBar = new ProgressBar
                {
                    Height = 6, Margin = new Thickness(0, 8, 0, 0),
                    Minimum = 0, Maximum = 100,
                    Value = status.Progress > 0 ? status.Progress : 0,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x40, 0x81)),
                    Background = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                    BorderThickness = new Thickness(0),
                };
                mainStack.Children.Add(progressBar);

                // Cancel button
                var cancelBtn = new Button
                {
                    Content = "取消下载", FontSize = 12,
                    Padding = new Thickness(10, 2, 10, 2),
                    Margin = new Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Tag = jobId,
                    Style = (Style)Application.Current.FindResource("SecondaryButton"),
                };
                cancelBtn.Click += (s, e) => onCancel(jobId);
                mainStack.Children.Add(cancelBtn);
            }

            // Path for completed downloads
            if (status?.Status == "completed" && !string.IsNullOrEmpty(status?.Path))
            {
                mainStack.Children.Add(new TextBlock
                {
                    Text = $"📁 {status.Path}", FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            border.Child = mainStack;
            collection.Add(border);
        }
    }
}
