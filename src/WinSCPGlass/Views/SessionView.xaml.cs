using System.Windows.Controls;

namespace WinScpGlass.Views;

public partial class SessionView : UserControl
{
    public SessionView()
    {
        InitializeComponent();
        Loaded += (_, _) => RemoteView.FocusList();
    }

    /// <summary>Tab jumps between the two panels, like a classic commander.</summary>
    public void FocusOther(FilePanelView current) => (current == LocalView ? RemoteView : LocalView).FocusList();
}
