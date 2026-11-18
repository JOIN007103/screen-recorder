using System;
using System.Windows;
using System.Windows.Input;

namespace ScreenRecorder.Views
{
    /// <summary>
    /// 基础回放窗口：使用 WPF MediaElement 播放 H.264/MP4。
    /// </summary>
    public partial class PlaybackWindow : Window
    {
        private readonly string _filePath;

        public PlaybackWindow(string filePath)
        {
            InitializeComponent();
            _filePath = filePath;
            TxtFile.Text = System.IO.Path.GetFileName(filePath);
            Title = "回放 - " + System.IO.Path.GetFileName(filePath);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Player.Source = new Uri(_filePath, UriKind.Absolute);
                Player.Play();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"无法打开视频：{ex.Message}", "回放失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (Player.CanPause)
            {
                if (BtnPlay.Content.ToString() == "暂停") { Player.Pause(); BtnPlay.Content = "播放"; }
                else { Player.Play(); BtnPlay.Content = "暂停"; }
            }
        }

        private void BtnReplay_Click(object sender, RoutedEventArgs e)
        {
            Player.Position = TimeSpan.Zero;
            Player.Play();
            BtnPlay.Content = "暂停";
        }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            System.Windows.MessageBox.Show("播放器无法解码该视频，请确认系统已安装 H.264 解码组件。",
                "播放失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            Player.Stop();
            Player.Close();
        }
    }
}
