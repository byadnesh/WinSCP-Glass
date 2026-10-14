using System.Windows;
using System.Windows.Controls;

namespace WinScpGlass.Controls;

/// <summary>
/// Attached properties used by the glass control templates.
/// </summary>
public static class UiProps
{
    // Segoe Fluent Icons / MDL2 glyph shown by button and field templates.
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(UiProps), new FrameworkPropertyMetadata(null));
    public static string? GetIcon(DependencyObject o) => (string?)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, string? v) => o.SetValue(IconProperty, v);

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(UiProps), new FrameworkPropertyMetadata(null));
    public static string? GetPlaceholder(DependencyObject o) => (string?)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string? v) => o.SetValue(PlaceholderProperty, v);

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(UiProps), new FrameworkPropertyMetadata(new CornerRadius(12)));
    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius v) => o.SetValue(CornerRadiusProperty, v);

    // Specular sheen on button surfaces; borderless (ghost) buttons turn it off.
    public static readonly DependencyProperty ShowSheenProperty = DependencyProperty.RegisterAttached(
        "ShowSheen", typeof(bool), typeof(UiProps), new FrameworkPropertyMetadata(true));
    public static bool GetShowSheen(DependencyObject o) => (bool)o.GetValue(ShowSheenProperty);
    public static void SetShowSheen(DependencyObject o, bool v) => o.SetValue(ShowSheenProperty, v);

    // PasswordBox does not expose whether it has text, which the placeholder needs.
    public static readonly DependencyProperty HasTextProperty = DependencyProperty.RegisterAttached(
        "HasText", typeof(bool), typeof(UiProps), new FrameworkPropertyMetadata(false));
    public static bool GetHasText(DependencyObject o) => (bool)o.GetValue(HasTextProperty);
    public static void SetHasText(DependencyObject o, bool v) => o.SetValue(HasTextProperty, v);

    public static readonly DependencyProperty MonitorPasswordProperty = DependencyProperty.RegisterAttached(
        "MonitorPassword", typeof(bool), typeof(UiProps), new FrameworkPropertyMetadata(false, OnMonitorPasswordChanged));
    public static bool GetMonitorPassword(DependencyObject o) => (bool)o.GetValue(MonitorPasswordProperty);
    public static void SetMonitorPassword(DependencyObject o, bool v) => o.SetValue(MonitorPasswordProperty, v);

    private static void OnMonitorPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        box.PasswordChanged -= PasswordChanged;
        if ((bool)e.NewValue)
        {
            box.PasswordChanged += PasswordChanged;
            SetHasText(box, box.Password.Length > 0);
        }
    }

    private static void PasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        SetHasText(box, box.Password.Length > 0);
    }

    // Two-way password binding helper (PasswordBox.Password is not a dependency property).
    // The default is null on purpose: binding an empty string must still count as a change,
    // because that change is what hooks up PasswordChanged. With an "" default, typing was never seen.
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(UiProps),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));
    public static string? GetBoundPassword(DependencyObject o) => (string?)o.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject o, string? v) => o.SetValue(BoundPasswordProperty, v);

    private static readonly DependencyProperty IsUpdatingProperty = DependencyProperty.RegisterAttached(
        "IsUpdating", typeof(bool), typeof(UiProps));

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        box.PasswordChanged -= BoundPasswordBoxChanged;
        var value = (string?)e.NewValue ?? string.Empty;
        if (!(bool)box.GetValue(IsUpdatingProperty) && box.Password != value)
        {
            box.Password = value;
        }
        box.PasswordChanged += BoundPasswordBoxChanged;
    }

    private static void BoundPasswordBoxChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(IsUpdatingProperty, true);
        SetBoundPassword(box, box.Password);
        box.SetValue(IsUpdatingProperty, false);
    }
}
