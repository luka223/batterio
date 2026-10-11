using Microsoft.Maui.Controls.Shapes;

namespace Batterio.App;

/// <summary>The 42x18 ten-segment battery glyph used by every device row.</summary>
public partial class BatteryIconView : ContentView
{
    private const int SegmentCount = 10;

    public static readonly BindableProperty BatteryColorProperty = BindableProperty.Create(
        nameof(BatteryColor), typeof(Color), typeof(BatteryIconView), Colors.Gray,
        propertyChanged: (bindable, _, _) => ((BatteryIconView)bindable).UpdateSegments());

    public static readonly BindableProperty FilledSegmentsProperty = BindableProperty.Create(
        nameof(FilledSegments), typeof(int), typeof(BatteryIconView), 0,
        propertyChanged: (bindable, _, _) => ((BatteryIconView)bindable).UpdateSegments());

    public static readonly BindableProperty IsChargingProperty = BindableProperty.Create(
        nameof(IsCharging), typeof(bool), typeof(BatteryIconView), false);

    private readonly Border[] _segments;

    public BatteryIconView()
    {
        InitializeComponent();
        _segments = Enumerable.Range(0, SegmentCount)
            .Select(_ => new Border
            {
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                StrokeThickness = 0,
                IsVisible = false
            })
            .ToArray();
        for (var index = 0; index < _segments.Length; index++)
        {
            Segments.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Segments.Add(_segments[index], index, 0);
        }

        UpdateSegments();
    }

    public Color BatteryColor
    {
        get => (Color)GetValue(BatteryColorProperty);
        set => SetValue(BatteryColorProperty, value);
    }

    public int FilledSegments
    {
        get => (int)GetValue(FilledSegmentsProperty);
        set => SetValue(FilledSegmentsProperty, value);
    }

    public bool IsCharging
    {
        get => (bool)GetValue(IsChargingProperty);
        set => SetValue(IsChargingProperty, value);
    }

    private void UpdateSegments()
    {
        if (_segments is null)
        {
            return;
        }

        for (var index = 0; index < _segments.Length; index++)
        {
            _segments[index].BackgroundColor = BatteryColor;
            _segments[index].IsVisible = index < FilledSegments;
        }
    }
}
