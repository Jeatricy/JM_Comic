using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JMComicClient.Models;

namespace JMComicClient.Views
{
    public partial class DetailView : Page
    {
        private readonly string _albumId;
        private AlbumDetail _detail;

        public DetailView(string albumId)
        {
            _albumId = albumId;
            InitializeComponent();
            Loaded += DetailView_Loaded;
        }

        private async void DetailView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _detail = await App.Api.GetAlbumDetailAsync(_albumId);
                if (_detail?.Error != null)
                {
                    ErrorText.Text = _detail.Error;
                    ErrorText.Visibility = Visibility.Visible;
                    LoadingText.Visibility = Visibility.Collapsed;
                    return;
                }
                BindData();
            }
            catch (Exception ex)
            {
                ErrorText.Text = $"加载失败: {ex.Message}";
                ErrorText.Visibility = Visibility.Visible;
                LoadingText.Visibility = Visibility.Collapsed;
            }
        }

        private void BindData()
        {
            LoadingText.Visibility = Visibility.Collapsed;

            // Cover
            if (!string.IsNullOrEmpty(_albumId))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(App.Api.GetCoverUrl(_albumId));
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmp.DownloadFailed += (s, e2) => { };
                    bmp.EndInit();
                    CoverImage.Source = bmp;
                }
                catch { }
            }

            // Basic info
            TitleText.Text = _detail.Title ?? "";
            IdText.Text = $"JM{_detail.Id}";
            AuthorText.Text = _detail.Author ?? "未知";
            PageCountText.Text = _detail.PageCount.ToString();
            LikesText.Text = _detail.Likes ?? "0";

            // Tags
            if (_detail.Tags != null && _detail.Tags.Count > 0)
                TagsText.Text = "标签: " + string.Join("  |  ", _detail.Tags);

            // Description
            if (!string.IsNullOrWhiteSpace(_detail.Description))
            {
                DescText.Text = _detail.Description;
                DescPanel.Visibility = Visibility.Visible;
            }

            // Chapters
            if (_detail.Chapters != null && _detail.Chapters.Count > 0)
            {
                var chapterModels = new List<ChapterModel>();
                foreach (var ch in _detail.Chapters)
                {
                    chapterModels.Add(new ChapterModel
                    {
                        Id = ch.Id,
                        Title = string.IsNullOrWhiteSpace(ch.Title) ? $"章节 {ch.Id}" : ch.Title,
                    });
                }
                ChapterList.ItemsSource = chapterModels;
                ChapterPanel.Visibility = Visibility.Visible;

                // Show read button only if single chapter
                if (_detail.Chapters.Count == 1)
                    ReadBtn.Content = "📖 阅读";
            }
            else
            {
                // Album has photos directly, try to use album id as photo id
                ReadBtn.Content = "📖 阅读";
            }

            InfoPanel.Visibility = Visibility.Visible;
        }

        // ── Button handlers ──────────────────────────────────────

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = Window.GetWindow(this) as MainView;
            mainWindow?.GoBack();
        }

        private void ReadFirst_Click(object sender, RoutedEventArgs e)
        {
            if (_detail?.Chapters != null && _detail.Chapters.Count > 0)
            {
                var firstChapter = _detail.Chapters[0];
                var mainWindow = Window.GetWindow(this) as MainView;
                mainWindow?.NavigateToReader(firstChapter.Id, "");
            }
            else
            {
                var mainWindow = Window.GetWindow(this) as MainView;
                mainWindow?.NavigateToReader(_albumId, "");
            }
        }

        private void ChapterRead_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string photoId)
            {
                var mainWindow = Window.GetWindow(this) as MainView;
                mainWindow?.NavigateToReader(photoId, "");
            }
        }

        private async void Fav_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var resp = await App.Api.AddFavoriteAsync(_albumId);
                if (resp?.Success == true)
                    MessageBox.Show("已添加到收藏夹！", "收藏", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show(resp?.Error ?? "收藏失败", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"收藏失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var resp = await App.Api.StartDownloadAsync(_albumId);
                if (resp?.JobId != null)
                    DownloadView.RegisterJob(resp.JobId);
            }
            catch { }
        }
    }

    // ── Chapter display model ────────────────────────────────────
    public class ChapterModel
    {
        public string Id { get; set; }
        public string Title { get; set; }
    }
}
