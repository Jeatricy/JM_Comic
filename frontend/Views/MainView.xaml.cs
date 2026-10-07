using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JMComicClient.Views
{
    public partial class MainView : Window
    {
        private string _currentUsername;

        public MainView()
        {
            InitializeComponent();
            Loaded += MainView_Loaded;
        }

        private async void MainView_Loaded(object sender, RoutedEventArgs e)
        {
            if (App.BackendReady != null && !await App.BackendReady)
            {
                MessageBox.Show(
                    "后端启动失败，请检查 JMComicBackend.exe 和端口 5000。",
                    "启动失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            // Try auto-login
            try
            {
                var resp = await App.Api.AutoLoginAsync();
                if (resp != null && resp.Success)
                {
                    _currentUsername = resp.Username;
                    UpdateUserUI(true);
                }
            }
            catch { }
            NavigateTo<RankingView>();
            SetActiveNav(NavRanking);
        }

        private void UpdateUserUI(bool loggedIn)
        {
            if (loggedIn)
            {
                UsernameText.Text = $"👤 {_currentUsername}";
                UsernameText.Foreground = new SolidColorBrush(Color.FromRgb(255, 64, 129));
                LoginLogoutBtn.Content = "退出";
            }
            else
            {
                UsernameText.Text = "未登录";
                UsernameText.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170));
                LoginLogoutBtn.Content = "登录";
            }
        }

        public void SetLoggedInUser(string username)
        {
            _currentUsername = username;
            UpdateUserUI(!string.IsNullOrWhiteSpace(username));
        }

        private void SetActiveNav(Button active)
        {
            var transparent = new SolidColorBrush(Colors.Transparent);
            var activeBg = Color.FromRgb(0x38, 0x38, 0x38);
            foreach (var btn in new[] { NavSearch, NavFavorites, NavRanking, NavDownload, NavSettings })
            {
                btn.Background = btn == active ? new SolidColorBrush(activeBg) : transparent;
            }
        }

        private void NavigateTo<T>() where T : Page, new()
        {
            ContentFrame.Navigate(new T());
        }

        // ── Navigation handlers ──────────────────────────────────

        private void NavSearch_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo<SearchView>();
            SetActiveNav(NavSearch);
        }

        private void NavFavorites_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo<FavoritesView>();
            SetActiveNav(NavFavorites);
        }

        private void NavRanking_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo<RankingView>();
            SetActiveNav(NavRanking);
        }

        private void NavDownload_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo<DownloadView>();
            SetActiveNav(NavDownload);
        }

        private void NavSettings_Click(object sender, RoutedEventArgs e)
        {
            NavigateTo<SettingsView>();
            SetActiveNav(NavSettings);
        }

        // ── Login / Logout ───────────────────────────────────────

        private async void LoginLogout_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentUsername))
            {
                // Logout
                try
                {
                    await App.Api.LogoutAsync();
                    _currentUsername = null;
                    UpdateUserUI(false);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"退出登录失败: {ex.Message}", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                // Show login dialog
                var dlg = new LoginDialog { Owner = this };
                if (dlg.ShowDialog() == true)
                {
                    _currentUsername = dlg.LoggedInUsername;
                    UpdateUserUI(true);
                }
            }
        }

        // ── Public navigation helper (used by other pages) ───────
        public void NavigateToDetail(string albumId)
        {
            ContentFrame.Navigate(new DetailView(albumId));
        }

        public void NavigateToReader(string photoId, string albumTitle)
        {
            ContentFrame.Navigate(new ReaderView(photoId, albumTitle));
        }

        public void GoBack()
        {
            if (ContentFrame.CanGoBack)
                ContentFrame.GoBack();
        }
    }
}
