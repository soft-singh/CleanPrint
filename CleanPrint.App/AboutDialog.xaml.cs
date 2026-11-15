using System.Reflection;
using System.Windows;

namespace CleanPrint.App;

/// <summary>Simple About box: app name, version (from the assembly's own Version, set in the
/// csproj), and the developer credit.</summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"Version {version.Major}.{version.Minor}.{version.Build}";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();
}
