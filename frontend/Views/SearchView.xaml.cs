using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JMComicClient.Models;

namespace JMComicClient.Views
{
    public partial class SearchView : Page
    {
        private int _currentPage = 1;
        private int _pageCount = 1;
        private string _currentQuery = "";

        public SearchView()
        {
            InitializeComponent();
            Loaded += SearchView_Loaded;
        }

        private async void SearchView_Loaded(object sender, RoutedEventArgs e)
        {
            SearchBox.Focus();
        }

        private string GetSelectedOrderBy()
        {
            if (SortCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
            return "mr";
        }

        private async void DoSearch()
        {
            _currentQuery = SearchBox.Text.Trim();
            if (string.IsNullOrEmpty(_currentQuery)) return;
            _currentPage = 1;
            await SearchAsync();
        }

        private async System.Threading.Tasks.Task SearchAsync()
        {
            LoadingText.Visibility = Visibility.Visible;
            ResultsPanel.Children.Clear();

            try
            {
                var resp = await App.Api.SearchAsync(
                    _currentQuery, _currentPage,
                    "", GetSelectedOrderBy());

                if (resp?.Error != null)
                {
                    MessageBox.Show(resp.Error, "搜索失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _pageCount = resp.PageCount;

                if (resp.Results != null)
                {
                    foreach (var item in resp.Results)
                    {
                        ResultsPanel.Children.CreateSearchCard(item, OnCardClick);
                    }
                }

                PageText.Text = $"第 {_currentPage} / {_pageCount} 页  (共 {resp.Total} 结果)";
                PrevBtn.Visibility = _currentPage > 1 ? Visibility.Visible : Visibility.Collapsed;
                NextBtn.Visibility = _currentPage < _pageCount ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"搜索失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                LoadingText.Visibility = Visibility.Collapsed;
            }
        }

        private void OnCardClick(string albumId)
        {
            var mainWindow = Window.GetWindow(this) as MainView;
            mainWindow?.NavigateToDetail(albumId);
        }

        private void Search_Click(object sender, RoutedEventArgs e) => DoSearch();

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) DoSearch();
        }

        private async void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                await SearchAsync();
            }
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < _pageCount)
            {
                _currentPage++;
                await SearchAsync();
            }
        }
    }

    // ── Helper: create manga card UI elements ────────────────────
    internal static class SearchCardHelper
    {
        public static void CreateSearchCard(this UIElementCollection collection,
            SearchResult item, Action<string> onClick)
        {
            var border = new Border
            {
                Width = 200,
                Margin = new Thickness(6),
                Background = Brushes.White,
                CornerRadius = new CornerRadius(8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    Direction = 270,
                    ShadowDepth = 2,
                    Opacity = 0.1,
                    BlurRadius = 8
                },
                Cursor = Cursors.Hand,
            };

            var stack = new StackPanel();

            // Cover image
            var img = new Image
            {
                Height = 260,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            if (!string.IsNullOrEmpty(item.Id))
            {
                try
                {
                    var coverUrl = App.Api.GetCoverUrl(item.Id);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(coverUrl);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmp.DownloadFailed += (s, e2) => { /* silently ignore */ };
                    bmp.EndInit();
                    img.Source = bmp;
                }
                catch { }
            }
            stack.Children.Add(img);

            // Title
            var titleText = new TextBlock
            {
                Text = item.Title ?? "",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 36,
                Margin = new Thickness(8, 6, 8, 2),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            stack.Children.Add(titleText);

            // ID badge
            var idText = new TextBlock
            {
                Text = $"JM{item.Id}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                Margin = new Thickness(8, 0, 8, 6),
            };
            stack.Children.Add(idText);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 0, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var detailBtn = new Button
            {
                Content = "详情",
                FontSize = 12,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x40, 0x81)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Tag = item.Id,
            };
            detailBtn.Click += (s, e) => onClick(item.Id);
            btnPanel.Children.Add(detailBtn);

            var dlBtn = new Button
            {
                Content = "下载",
                FontSize = 12,
                Padding = new Thickness(12, 4, 12, 4),
                Background = new SolidColorBrush(Color.FromRgb(0x42, 0xA5, 0xF5)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Tag = item.Id,
            };
            dlBtn.Click += async (s, e) =>
            {
                var id = (string)((Button)s).Tag;
                try
                {
                    var resp = await App.Api.StartDownloadAsync(id);
                    if (resp?.JobId != null)
                        DownloadView.RegisterJob(resp.JobId);
                }
                catch { }
            };
            btnPanel.Children.Add(dlBtn);

            stack.Children.Add(btnPanel);
            border.Child = stack;
            border.MouseLeftButtonUp += (s, e) => onClick(item.Id);

            collection.Add(border);
        }
    }
}
