using System.Diagnostics;

namespace OpenFinger.Control.Pages;

public partial class AboutPage : UserControl
{
    private const string BilibiliUrl = "https://space.bilibili.com/1965296126";
    private const string GitHubUrl = "https://github.com/TheD0ubleC/OpenFinger";
    private const string VRChatUrl = "https://vrchat.com/home/user/usr_f80f61e0-9018-4b43-8020-8bb2f9ff4a2d";

    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"版本 {OpenFingerVersion.Version}";
        ProtocolVersionText.Text = $"协议版本 v{OpenFingerVersion.ProtocolVersion}";
    }

    private void OnBilibiliClick(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(BilibiliUrl);
    }

    private void OnGitHubClick(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(GitHubUrl);
    }

    private void OnVRChatClick(object sender, RoutedEventArgs e)
    {
        OpenExternalLink(VRChatUrl);
    }

    private static void OpenExternalLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开链接：{ex.Message}", "OpenFinger", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
