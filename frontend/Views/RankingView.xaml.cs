using System;
using System.Windows;
using System.Windows.Controls;

namespace JMComicClient.Views
{
    public partial class RankingView : Page
    {
        private string _rankType = "day";
        private int _currentPage = 1;
        private int _pageCount = 1;

        public RankingView()
        {
            InitializeComponent();
            Loaded += RankingView_Loaded;
        }

        private async void RankingView_Loaded(object sender, RoutedEventArgs e)
        {
            SetActiveTab(DayTab);
            await LoadRanking();
        }

        private void SetActiveTab(Button active)
        {
            foreach (var tab in new[] { DayTab, WeekTab, MonthTab })
            {
                if (tab == active)
                {
                    tab.Style = (Style)FindResource("PrimaryButton");
                    tab.Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xFF, 0x40, 0x81));
                }
                else
                {
                    tab.Style = (Style)FindResource("SecondaryButton");
                    tab.Background = System.Windows.Media.Brushes.Transparent;
                }
            }
        }

        private async System.Threading.Tasks.Task LoadRanking()
        {
            LoadingText.Visibility = Visibility.Visible;
            ResultsPanel.Children.Clear();
            try
            {
                var resp = await App.Api.GetRankingAsync(_rankType, "0", _currentPage);
                if (resp?.Results != null)
                {
                    _pageCount = resp.PageCount;
                    foreach (var item in resp.Results)
                    {
                        ResultsPanel.Children.CreateSearchCard(item, OnCardClick);
                    }
                }
                PageText.Text = $"第 {_currentPage} / {_pageCount} 页";
                PrevBtn.Visibility = _currentPage > 1 ? Visibility.Visible : Visibility.Collapsed;
                NextBtn.Visibility = _currentPage < _pageCount ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载排行榜失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private async void DayTab_Click(object sender, RoutedEventArgs e)
        {
            _rankType = "day";
            _currentPage = 1;
            SetActiveTab(DayTab);
            await LoadRanking();
        }

        private async void WeekTab_Click(object sender, RoutedEventArgs e)
        {
            _rankType = "week";
            _currentPage = 1;
            SetActiveTab(WeekTab);
            await LoadRanking();
        }

        private async void MonthTab_Click(object sender, RoutedEventArgs e)
        {
            _rankType = "month";
            _currentPage = 1;
            SetActiveTab(MonthTab);
            await LoadRanking();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _currentPage = 1;
            await LoadRanking();
        }

        private async void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                await LoadRanking();
            }
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < _pageCount)
            {
                _currentPage++;
                await LoadRanking();
            }
        }
    }
}
