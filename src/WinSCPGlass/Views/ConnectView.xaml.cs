using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinScpGlass.Models;
using WinScpGlass.ViewModels;

namespace WinScpGlass.Views;

public partial class ConnectView : UserControl
{
    public ConnectView()
    {
        InitializeComponent();
        Loaded += (_, _) => Keyboard.Focus(HostBox);
    }

    private void SitesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SiteProfile site && DataContext is ConnectViewModel vm)
        {
            vm.ConnectSiteCommand.Execute(site);
        }
    }
}
