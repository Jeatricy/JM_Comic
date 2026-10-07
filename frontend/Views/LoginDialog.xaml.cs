using System;
using System.Windows;

namespace JMComicClient.Views
{
    public partial class LoginDialog : Window
    {
        public string LoggedInUsername { get; private set; }

        public LoginDialog()
        {
            InitializeComponent();
        }

        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            var username = UsernameBox.Text.Trim();
            var password = PasswordBox.Password;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                ErrorText.Text = "请输入用户名和密码";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            LoginBtn.IsEnabled = false;
            LoginBtn.Content = "登录中...";
            ErrorText.Visibility = Visibility.Collapsed;

            try
            {
                var resp = await App.Api.LoginAsync(username, password, RememberCheck.IsChecked == true);
                if (resp != null && resp.Success)
                {
                    LoggedInUsername = username;
                    DialogResult = true;
                    Close();
                }
                else
                {
                    ErrorText.Text = resp?.Error ?? resp?.Message ?? "登录失败";
                    ErrorText.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                ErrorText.Text = $"连接后端失败: {ex.Message}";
                ErrorText.Visibility = Visibility.Visible;
            }
            finally
            {
                LoginBtn.IsEnabled = true;
                LoginBtn.Content = "登录";
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
