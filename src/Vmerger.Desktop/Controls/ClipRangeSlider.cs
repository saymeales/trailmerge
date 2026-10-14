using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Vmerger.Desktop.Controls;

public sealed class ClipRangeSlider : Control
{
    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<ClipRangeSlider, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<ClipRangeSlider, double>(nameof(Maximum), 1);

    public static readonly StyledProperty<double> RangeStartProperty =
        AvaloniaProperty.Register<ClipRangeSlider, double>(
            nameof(RangeStart),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> RangeEndProperty =
        AvaloniaProperty.Register<ClipRangeSlider, double>(
            nameof(RangeEnd),
            1,
            defaultBindingMode: BindingMode.TwoWay);

    private const double ThumbRadius = 7;
    private const double TrackHeight = 4;
    private DragThumb? _dragThumb;

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ClipRangeSlider, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> SelectedRangeBrushProperty =
        AvaloniaProperty.Register<ClipRangeSlider, IBrush?>(nameof(SelectedRangeBrush));
    public static readonly StyledProperty<IBrush?> ThumbBrushProperty =
        AvaloniaProperty.Register<ClipRangeSlider, IBrush?>(nameof(ThumbBrush));
    public static readonly StyledProperty<IBrush?> ThumbBorderBrushProperty =
        AvaloniaProperty.Register<ClipRangeSlider, IBrush?>(nameof(ThumbBorderBrush));

    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? SelectedRangeBrush { get => GetValue(SelectedRangeBrushProperty); set => SetValue(SelectedRangeBrushProperty, value); }
    public IBrush? ThumbBrush { get => GetValue(ThumbBrushProperty); set => SetValue(ThumbBrushProperty, value); }
    public IBrush? ThumbBorderBrush { get => GetValue(ThumbBorderBrushProperty); set => SetValue(ThumbBorderBrushProperty, value); }

    static ClipRangeSlider()
    {
        AffectsRender<ClipRangeSlider>(
            MinimumProperty,
            MaximumProperty,
            RangeStartProperty,
            RangeEndProperty,
            TrackBrushProperty,
            SelectedRangeBrushProperty,
            ThumbBrushProperty,
            ThumbBorderBrushProperty);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double RangeStart
    {
        get => GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    public double RangeEnd
    {
        get => GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var y = Bounds.Height / 2;
        var left = ThumbRadius;
        var right = Math.Max(left, Bounds.Width - ThumbRadius);
        var startX = ValueToX(RangeStart, left, right);
        var endX = ValueToX(RangeEnd, left, right);

        var trackPen = new Pen(TrackBrush, TrackHeight, lineCap: PenLineCap.Round);
        var selectedPen = new Pen(SelectedRangeBrush, TrackHeight + 1, lineCap: PenLineCap.Round);
        var thumbPen = new Pen(ThumbBorderBrush, 2);

        context.DrawLine(trackPen, new Point(left, y), new Point(right, y));
        context.DrawLine(selectedPen, new Point(Math.Min(startX, endX), y), new Point(Math.Max(startX, endX), y));
        context.DrawEllipse(ThumbBrush, thumbPen, new Point(startX, y), ThumbRadius, ThumbRadius);
        context.DrawEllipse(ThumbBrush, thumbPen, new Point(endX, y), ThumbRadius, ThumbRadius);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var position = e.GetPosition(this);
        var left = ThumbRadius;
        var right = Math.Max(left, Bounds.Width - ThumbRadius);
        var startDistance = Math.Abs(position.X - ValueToX(RangeStart, left, right));
        var endDistance = Math.Abs(position.X - ValueToX(RangeEnd, left, right));

        _dragThumb = startDistance <= endDistance ? DragThumb.Start : DragThumb.End;
        UpdateDraggedThumb(position.X);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragThumb is null)
        {
            return;
        }

        UpdateDraggedThumb(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ReleasePointer(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragThumb = null;
    }

    private void ReleasePointer(IPointer pointer)
    {
        _dragThumb = null;
        pointer.Capture(null);
    }

    private void UpdateDraggedThumb(double x)
    {
        var value = XToValue(x, ThumbRadius, Math.Max(ThumbRadius, Bounds.Width - ThumbRadius));
        if (_dragThumb == DragThumb.Start)
        {
            RangeStart = Math.Min(value, RangeEnd);
        }
        else
        {
            RangeEnd = Math.Max(value, RangeStart);
        }
    }

    private double ValueToX(double value, double left, double right)
    {
        var range = Maximum - Minimum;
        if (range <= 0)
        {
            return left;
        }

        var normalized = (Math.Clamp(value, Minimum, Maximum) - Minimum) / range;
        return left + normalized * (right - left);
    }

    private double XToValue(double x, double left, double right)
    {
        if (right <= left)
        {
            return Minimum;
        }

        var normalized = Math.Clamp((x - left) / (right - left), 0, 1);
        return Minimum + normalized * (Maximum - Minimum);
    }

    private enum DragThumb
    {
        Start,
        End
    }
}
