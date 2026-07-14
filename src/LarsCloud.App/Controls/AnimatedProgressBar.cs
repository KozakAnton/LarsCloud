using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace LarsCloud.Controls;

public sealed class AnimatedProgressBar : System.Windows.Controls.ProgressBar
{
    public static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.Register(
        nameof(AnimatedValue), typeof(double), typeof(AnimatedProgressBar),
        new PropertyMetadata(0d, OnAnimatedValueChanged));

    public double AnimatedValue
    {
        get => (double)GetValue(AnimatedValueProperty);
        set => SetValue(AnimatedValueProperty, value);
    }

    private static void OnAnimatedValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var bar = (AnimatedProgressBar)dependencyObject;
        var target = Math.Clamp((double)args.NewValue, bar.Minimum, bar.Maximum);
        var animation = new DoubleAnimation(bar.Value, target, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        bar.BeginAnimation(ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
