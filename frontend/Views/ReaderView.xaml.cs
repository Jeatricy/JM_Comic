using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JMComicClient.Models;

namespace JMComicClient.Views
{
    public partial class ReaderView : Page
    {
        private readonly string _photoId;
        private readonly string _albumTitle;
        private List<PageImage> _images;
        private double _zoomLevel = 1.0;
        private readonly List<Image> _imageElements = new();
        private readonly List<double> _originalWidths = new();
        private Task _loadTask;

        public ReaderView(string photoId, string albumTitle = "")
        {
            _photoId = photoId;
            _albumTitle = albumTitle ?? "";
            InitializeComponent();
            Loaded += ReaderView_Loaded;
        }

        private async void ReaderView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadTask != null)
            {
                await _loadTask;
                return;
            }

            _loadTask = LoadReaderAsync();
            await _loadTask;
        }

        private async Task LoadReaderAsync()
        {
            TitleText.Text = string.IsNullOrWhiteSpace(_albumTitle)
                ? $"章节 {_photoId}"
                : _albumTitle;
            try
            {
                LoadingDetail.Text = "正在获取章节信息...";
                var photo = await App.Api.GetPhotoDetailAsync(_photoId);
                if (photo?.Error != null) { LoadingDetail.Text = $"加载失败: {photo.Error}"; return; }
                if (photo?.Images == null || photo.Images.Count == 0) { LoadingDetail.Text = "该章节没有图片"; return; }
                _images = photo.Images;
                ProgressText.Text = $"0/{_images.Count}";

                // Trigger background chapter download
                LoadingDetail.Text = $"正在下载并解密 {_images.Count} 张图片...";
                var downloadResponse = await App.Api.DownloadChapterAsync(_photoId);
                if (downloadResponse == null ||
                    (downloadResponse.Status != "ready" && downloadResponse.Status != "downloading"))
                {
                    LoadingDetail.Text = downloadResponse?.Error ?? "章节下载未能启动";
                    return;
                }

                // Poll until ready
                int pollCount = 0;
                while (true)
                {
                    await Task.Delay(1500);
                    pollCount++;
                    LoadingDetail.Text = $"正在下载并解密... ({pollCount * 3 / 2}秒)";
                    var status = await App.Api.GetChapterStatusAsync(_photoId);
                    if (status?.Status == "ready") break;
                    if (status?.Status == "error") { LoadingDetail.Text = $"下载失败: {status.Error}"; return; }
                    if (pollCount > 120) { LoadingDetail.Text = "下载超时"; return; }
                }

                // Chapter ready — hide loading, start progressive display
                LoadingOverlay.Visibility = Visibility.Collapsed;

                // Load images sequentially: download + decode on bg thread, add to UI
                for (int i = 0; i < _images.Count; i++)
                {
                    var img = _images[i];

                    try
                    {
                        // Download + decode on background thread
                        var result = await Task.Run(async () =>
                        {
                            var bytes = await App.Api.GetImageBytesAsync(
                                img.ImgUrl, _photoId, img.Index);
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.StreamSource = new MemoryStream(bytes);
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.EndInit();
                            bmp.Freeze(); // Make cross-thread safe
                            var dpi = bmp.DpiX > 0 ? bmp.DpiX / 96.0 : 1.0;
                            return (bmp, bmp.PixelWidth / dpi);
                        });

                        // Add to UI on UI thread
                        var image = new Image
                        {
                            Source = result.Item1,
                            Stretch = Stretch.Uniform,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Width = result.Item2,
                        };
                        ImageStack.Children.Add(image);
                        _imageElements.Add(image);
                        _originalWidths.Add(result.Item2);

                        // Apply current zoom to new image
                        image.Width = result.Item2 * _zoomLevel;

                        ProgressText.Text = $"{i + 1}/{_images.Count}";

                        // Auto fit width on first image only
                        if (_imageElements.Count == 1)
                            _ = Dispatcher.InvokeAsync(() => FitWidth(), System.Windows.Threading.DispatcherPriority.Loaded);
                    }
                    catch (Exception ex)
                    {
                        ImageStack.Children.Add(new TextBlock
                        {
                            Text = $"[图片 {i + 1} 加载失败: {ex.Message}]",
                            Foreground = Brushes.Red, FontSize = 14,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 10, 0, 10),
                        });
                    }
                }

                ProgressText.Text = $"{_images.Count} 页";
            }
            catch (Exception ex) { LoadingDetail.Text = $"加载失败: {ex.Message}"; }
        }

        // ── Zoom ─────────────────────────────────────────────────

        private bool _suppressSlider = false;

        private void ApplyZoom()
        {
            for (int i = 0; i < _imageElements.Count; i++)
            {
                if (i < _originalWidths.Count)
                {
                    _imageElements[i].Width = _originalWidths[i] * _zoomLevel;
                    _imageElements[i].Height = double.NaN;
                }
            }
            _suppressSlider = true;
            if (ZoomSlider != null) ZoomSlider.Value = _zoomLevel * 100;
            _suppressSlider = false;
            if (ZoomText != null) ZoomText.Text = $"{(int)(_zoomLevel * 100)}%";
        }

        private void FitWidth()
        {
            if (_imageElements.Count == 0) return;
            var vw = Scroller.ViewportWidth;
            if (vw <= 0 || _originalWidths[0] <= 0) return;
            _zoomLevel = Math.Max(0.1, Math.Min(vw / _originalWidths[0], 3.0));
            ApplyZoom();
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSlider || !IsLoaded) return;
            _zoomLevel = ZoomSlider.Value / 100.0;
            if (_imageElements.Count > 0) ApplyZoom();
        }

        private void ZoomReset_Click(object sender, RoutedEventArgs e) { _zoomLevel = 1.0; ApplyZoom(); }
        private void FitWidth_Click(object sender, RoutedEventArgs e) { FitWidth(); }

        private void Scroller_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                _zoomLevel = Math.Max(0.1, Math.Min(_zoomLevel + (e.Delta > 0 ? 0.05 : -0.05), 3.0));
                ApplyZoom();
                e.Handled = true;
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            (Window.GetWindow(this) as MainView)?.GoBack();
        }
    }
}
