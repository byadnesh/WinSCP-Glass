using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinScpGlass.Controls;

/// <summary>ContentControl that floats new content in: fade, rise and settle.</summary>
public sealed class FluidContent : ContentControl
{
    private readonly TranslateTransform _translate = new();
    private readonly ScaleTransform _scale = new(1, 1);

    public FluidContent()
    {
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new TransformGroup { Children = { _scale, _translate } };
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (oldContent == null) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(520);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
        _translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(28, 0, duration) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.975, 1, duration) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.975, 1, duration) { EasingFunction = ease });
    }
}
