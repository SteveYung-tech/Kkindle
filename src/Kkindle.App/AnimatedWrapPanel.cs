using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kkindle;

/// <summary>
/// Smooths the discrete row changes produced by WrapPanel when its available
/// width changes.
/// </summary>
public sealed class AnimatedWrapPanel : WrapPanel
{
    private const double MovementThreshold = 0.5;
    private static readonly TimeSpan RepositionDuration = TimeSpan.FromMilliseconds(420);

    private readonly HashSet<Control> _arrangedChildren = [];
    private readonly Dictionary<Control, CancellationTokenSource> _animations = [];

    protected override Size ArrangeOverride(Size finalSize)
    {
        var previousPositions = new Dictionary<Control, Point>();
        var previousOffsets = new Dictionary<Control, Vector>();

        foreach (var child in Children)
        {
            if (!_arrangedChildren.Contains(child)) continue;

            previousPositions[child] = child.Bounds.Position;
            previousOffsets[child] = child.RenderTransform is TranslateTransform translate
                ? new Vector(translate.X, translate.Y)
                : default;
        }

        var arrangedSize = base.ArrangeOverride(finalSize);
        var liveChildren = Children.ToHashSet();

        foreach (var removedChild in _arrangedChildren.Where(child => !liveChildren.Contains(child)).ToList())
        {
            CancelAnimation(removedChild);
            _arrangedChildren.Remove(removedChild);
        }

        foreach (var child in Children)
        {
            if (previousPositions.TryGetValue(child, out var previousPosition))
            {
                var currentPosition = child.Bounds.Position;
                var targetChanged = Math.Abs(previousPosition.X - currentPosition.X) > MovementThreshold
                    || Math.Abs(previousPosition.Y - currentPosition.Y) > MovementThreshold;

                if (targetChanged)
                {
                    var previousOffset = previousOffsets[child];
                    StartRepositionAnimation(
                        child,
                        previousPosition.X + previousOffset.X - currentPosition.X,
                        previousPosition.Y + previousOffset.Y - currentPosition.Y);
                }
            }

            _arrangedChildren.Add(child);
        }

        return arrangedSize;
    }

    private void StartRepositionAnimation(Control child, double fromX, double fromY)
    {
        CancelAnimation(child);

        var cancellation = new CancellationTokenSource();
        _animations[child] = cancellation;
        var translate = new TranslateTransform(fromX, fromY);
        child.RenderTransform = translate;
        _ = AnimateToRestAsync(child, translate, cancellation);
    }

    private async Task AnimateToRestAsync(
        Control child,
        TranslateTransform translate,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.WhenAll(
                RunAnimationAsync(translate, TranslateTransform.XProperty, translate.X, cancellation.Token),
                RunAnimationAsync(translate, TranslateTransform.YProperty, translate.Y, cancellation.Token));
        }
        catch (OperationCanceledException)
        {
        }

        if (cancellation.IsCancellationRequested
            || !_animations.TryGetValue(child, out var active)
            || !ReferenceEquals(active, cancellation)) return;

        _animations.Remove(child);
        cancellation.Dispose();
        if (ReferenceEquals(child.RenderTransform, translate))
            child.RenderTransform = new TranslateTransform();
    }

    private static Task RunAnimationAsync(
        TranslateTransform target,
        AvaloniaProperty property,
        double from,
        CancellationToken token)
    {
        var animation = new Animation
        {
            Duration = RepositionDuration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward
        };
        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Avalonia.Styling.Setter(property, from) }
        });
        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Avalonia.Styling.Setter(property, 0d) }
        });
        return animation.RunAsync(target, token);
    }

    private void CancelAnimation(Control child)
    {
        if (!_animations.Remove(child, out var cancellation)) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
