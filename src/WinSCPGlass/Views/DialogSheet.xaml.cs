using System.Windows.Controls;
using System.Windows.Input;
using WinScpGlass.ViewModels;

namespace WinScpGlass.Views;

public partial class DialogSheet : UserControl
{
    public DialogSheet()
    {
        InitializeComponent();
    }

    /// <summary>Puts the caret in the input field, if the dialog has one.</summary>
    public void FocusInitial()
    {
        if (DataContext is not DialogRequest { HasInput: true } request) return;
        if (request.IsPassword)
        {
            Keyboard.Focus(InputPassword);
        }
        else
        {
            Keyboard.Focus(InputText);
            // Select the name without its extension, like Explorer's rename.
            int dot = InputText.Text.LastIndexOf('.');
            InputText.Select(0, dot > 0 ? dot : InputText.Text.Length);
        }
    }
}
