using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace RASharp.App.About;

public partial class AboutWindow : Window
{
    private const string ProjectUrl = "https://github.com/wonly211/RASharp";

    public AboutWindow(string applicationDirectory, string configurationDirectory)
    {
        InitializeComponent();
        VersionTextBlock.Text = $"版本 {GetApplicationVersion()}";
        ApplicationDirectoryTextBox.Text = applicationDirectory;
        ConfigurationDirectoryTextBox.Text = configurationDirectory;
    }

    private static string GetApplicationVersion()
    {
        var assembly = typeof(AboutWindow).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "未知";
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(ProjectUrl);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _ = System.Windows.MessageBox.Show(
                exception.Message,
                "无法打开项目地址",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
