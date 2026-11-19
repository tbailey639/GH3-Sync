using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GH3_Sync.Views;

public partial class AmpKnobControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(AmpKnobControl),
            new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChangedStatic));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(AmpKnobControl), new PropertyMetadata(0.0));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(AmpKnobControl), new PropertyMetadata(11.0));

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(AmpKnobControl),
            new PropertyMetadata("VOLUME", (d, e) => ((AmpKnobControl)d).TxtHeader.Text = (string)e.NewValue));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(AmpKnobControl), new PropertyMetadata(""));

    public event RoutedPropertyChangedEventHandler<double>? ValueChanged;

    private bool _isDragging = false;
    private System.Windows.Point _dragStart;
    private double _valueOnDragStart;

    public AmpKnobControl()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateRotation();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    private static void OnValueChangedStatic(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AmpKnobControl knob)
        {
            knob.UpdateRotation();
            knob.ValueChanged?.Invoke(knob, new RoutedPropertyChangedEventArgs<double>((double)e.OldValue, (double)e.NewValue));
        }
    }

    private void UpdateRotation()
    {
        double range = Maximum - Minimum;
        if (range <= 0) range = 1.0;
        double normalized = (Value - Minimum) / range;
        normalized = Math.Clamp(normalized, 0.0, 1.0);

        // Map 0..1 to -135deg .. +135deg (total 270 degree sweep)
        double angle = -135.0 + (normalized * 270.0);
        KnobRotation.Angle = angle;

        string unitSuffix = string.IsNullOrEmpty(Unit) ? "" : $" {Unit}";
        TxtValue.Text = $"{Value:F1}{unitSuffix}";
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isDragging = true;
            _dragStart = e.GetPosition(this);
            _valueOnDragStart = Value;
            CaptureMouse();
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
        }
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging) return;

        var current = e.GetPosition(this);
        double deltaY = _dragStart.Y - current.Y; // Drag up increases value
        double range = Maximum - Minimum;
        double step = range / 120.0; // 120 pixels for full sweep

        Value = Math.Clamp(_valueOnDragStart + (deltaY * step), Minimum, Maximum);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double range = Maximum - Minimum;
        double step = range / 22.0;
        if (e.Delta > 0)
            Value = Math.Clamp(Value + step, Minimum, Maximum);
        else
            Value = Math.Clamp(Value - step, Minimum, Maximum);

        e.Handled = true;
    }
}
