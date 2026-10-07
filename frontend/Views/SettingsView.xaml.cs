using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JMComicClient.Models;

namespace JMComicClient.Views
{
    public partial class SettingsView : Page
    {
        public SettingsView()
        {
            InitializeComponent();
            Loaded += SettingsView_Loaded;
        }

        private async void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var settings = await App.Api.GetSettingsAsync();
                if (settings != null)
                {
                    DownloadDirBox.Text = settings.DownloadDir ?? "downloads";
                    SelectComboByTag(ImageFormatCombo, settings.ImageFormat ?? ".jpg");
                    SelectComboByTag(OutputFormatCombo, settings.OutputFormat ?? "image");
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"⚠ 加载设置失败: {ex.Message}";
                StatusText.Foreground = Brushes.Red;
                StatusText.Visibility = Visibility.Visible;
            }
        }

        private void SelectComboByTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (item.Tag?.ToString() == tag)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private string GetComboTag(ComboBox combo)
        {
            if (combo.SelectedItem is ComboBoxItem item)
                return item.Tag?.ToString();
            return "";
        }

        private void BrowseDir_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择下载目录",
                ShowNewFolderButton = true,
            };
            if (!string.IsNullOrEmpty(DownloadDirBox.Text))
                dialog.SelectedPath = DownloadDirBox.Text;

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                DownloadDirBox.Text = dialog.SelectedPath;
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            await DoSave();
        }

        private async System.Threading.Tasks.Task DoSave()
        {
            var settings = new AppSettings
            {
                DownloadDir = DownloadDirBox.Text,
                ImageFormat = GetComboTag(ImageFormatCombo),
                OutputFormat = GetComboTag(OutputFormatCombo),
            };

            try
            {
                var resp = await App.Api.UpdateSettingsAsync(settings);
                if (resp?.Success == true)
                {
                    StatusText.Text = "✅ 设置已保存！";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                }
                else
                {
                    StatusText.Text = $"❌ 保存失败: {resp?.Error}";
                    StatusText.Foreground = Brushes.Red;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"❌ 保存失败: {ex.Message}";
                StatusText.Foreground = Brushes.Red;
            }
            StatusText.Visibility = Visibility.Visible;
        }

        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            DownloadDirBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "禁漫天堂",
                "downloads");
            SelectComboByTag(ImageFormatCombo, ".jpg");
            SelectComboByTag(OutputFormatCombo, "image");
            await DoSave();
        }
    }
}
