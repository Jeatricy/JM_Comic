using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JMComicClient.Views
{
    public partial class FavoritesView : Page
    {
        private int _currentPage = 1;

        public FavoritesView()
        {
            InitializeComponent();
            Loaded += FavoritesView_Loaded;
        }

        private async void FavoritesView_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadFavorites();
        }

        private string GetOrderBy()
        {
            if (SortCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
            return "mr";
        }

        private async System.Threading.Tasks.Task LoadFavorites()
        {
            LoadingText.Visibility = Visibility.Visible;
            ResultsPanel.Children.Clear();

            try
            {
                var resp = await App.Api.GetFavoritesAsync(_currentPage, "0", GetOrderBy());

                if (resp?.Error != null)
                {
                    ShowLoginPrompt(resp.Error);
                    return;
                }

                if (resp?.Results != null)
                {
                    foreach (var item in resp.Results)
                    {
                        ResultsPanel.Children.CreateSearchCard(item, OnCardClick);
                    }

                    if (resp.Results.Count == 0)
                    {
                        ResultsPanel.Children.Add(new TextBlock
                        {
                            Text = "收藏夹为空",
                            FontSize = 16,
                            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 40, 0, 0),
                        });
                    }
                }

                PrevBtn.Visibility = _currentPage > 1 ? Visibility.Visible : Visibility.Collapsed;
                NextBtn.Visibility = resp?.Results?.Count >= 20 ? Visibility.Visible : Visibility.Collapsed;
                PageText.Text = $"第 {_currentPage} 页";
            }
            catch (Exception ex)
            {
                ShowLoginPrompt(ex.Message);
            }
            finally
            {
                LoadingText.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowLoginPrompt(string error = "")
        {
            ResultsPanel.Children.Clear();
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 60, 0, 0),
            };
            panel.Children.Add(new TextBlock
            {
                Text = "🔐",
                FontSize = 48,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12),
            });
            panel.Children.Add(new TextBlock
            {
                Text = "请先登录查看收藏夹",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
            });
            var loginBtn = new Button
            {
                Content = "去登录",
                FontSize = 14,
                Padding = new Thickness(24, 8, 24, 8),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x40, 0x81)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            loginBtn.Click += (s, e2) =>
            {
                var mainWindow = Window.GetWindow(this) as MainView;
                if (mainWindow != null)
                {
                    var dlg = new LoginDialog { Owner = mainWindow };
                    if (dlg.ShowDialog() == true)
                    {
                        _currentPage = 1;
                        _ = LoadFavorites();
                    }
                }
            };
            panel.Children.Add(loginBtn);
            ResultsPanel.Children.Add(panel);
        }

        private void OnCardClick(string albumId)
        {
            var mainWindow = Window.GetWindow(this) as MainView;
            mainWindow?.NavigateToDetail(albumId);
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _currentPage = 1;
            await LoadFavorites();
        }

        private async void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                await LoadFavorites();
            }
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            _currentPage++;
            await LoadFavorites();
        }
    }
}
