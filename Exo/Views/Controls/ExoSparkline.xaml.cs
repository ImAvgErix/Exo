using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Exo.Views.Controls;

/// <summary>
/// Lightweight live sparkline (Path geometry only — no composition, no packages).
/// Bind <see cref="Values"/> to a rolling sample buffer (0–100 preferred).
/// </summary>
public sealed partial class ExoSparkline : UserControl
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(
            nameof(Values),
            typeof(object),
            typeof(ExoSparkline),
            new PropertyMetadata(null, OnValuesChanged));

    public static readonly DependencyProperty LineBrushProperty =
        DependencyProperty.Register(
            nameof(LineBrush),
            typeof(Brush),
            typeof(ExoSparkline),
            new PropertyMetadata(null, OnBrushChanged));

    public static readonly DependencyProperty FillBrushProperty =
        DependencyProperty.Register(
            nameof(FillBrush),
            typeof(Brush),
            typeof(ExoSparkline),
            new PropertyMetadata(null, OnBrushChanged));

    private INotifyCollectionChanged? _listening;

    public ExoSparkline()
    {
        InitializeComponent();
        Unloaded += (_, _) => Detach();
    }

    public object? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ExoSparkline s) return;
        s.Detach();
        if (e.NewValue is INotifyCollectionChanged ncc)
        {
            s._listening = ncc;
            ncc.CollectionChanged += s.OnCollectionChanged;
        }
        s.Rebuild();
    }

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ExoSparkline s) s.ApplyBrushes();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Detach()
    {
        if (_listening is null) return;
        try { _listening.CollectionChanged -= OnCollectionChanged; } catch { }
        _listening = null;
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => Rebuild();

    private void ApplyBrushes()
    {
        try
        {
            if (LineBrush is not null)
            {
                LinePath.Stroke = LineBrush;
                TipDot.Fill = LineBrush;
            }
            if (FillBrush is not null)
                AreaPath.Fill = FillBrush;
            else if (LineBrush is SolidColorBrush scb)
            {
                var c = scb.Color;
                AreaPath.Fill = new SolidColorBrush(Color.FromArgb(48, c.R, c.G, c.B));
            }
        }
        catch { }
    }

    private IReadOnlyList<double> ReadSamples()
    {
        if (Values is ObservableCollection<double> od)
            return od;
        if (Values is IList<double> list)
            return list is double[] arr ? arr : list.ToList();
        if (Values is IEnumerable<double> en)
            return en.ToList();
        return Array.Empty<double>();
    }

    public void Rebuild()
    {
        try
        {
            ApplyBrushes();
            var samples = ReadSamples();
            var w = Root.ActualWidth;
            var h = Root.ActualHeight;
            if (w < 4 || h < 4 || samples.Count < 2)
            {
                LinePath.Data = null;
                AreaPath.Data = null;
                TipDot.Opacity = 0;
                return;
            }

            const double padY = 2;
            var usableH = Math.Max(1, h - padY * 2);
            var n = samples.Count;
            var step = w / Math.Max(1, n - 1);

            // Mild smoothing: 3-tap average so the line feels intentional, not noisy.
            var smooth = new double[n];
            for (var i = 0; i < n; i++)
            {
                var a = samples[Math.Max(0, i - 1)];
                var b = samples[i];
                var c = samples[Math.Min(n - 1, i + 1)];
                smooth[i] = Math.Clamp((a + b + c) / 3.0, 0, 100);
            }

            Point Pt(int i)
            {
                var x = i * step;
                var y = padY + usableH * (1.0 - smooth[i] / 100.0);
                return new Point(x, y);
            }

            var line = new PathGeometry();
            var lineFig = new PathFigure { StartPoint = Pt(0), IsClosed = false, IsFilled = false };
            var area = new PathGeometry();
            var areaFig = new PathFigure { StartPoint = new Point(0, h), IsClosed = true, IsFilled = true };
            areaFig.Segments.Add(new LineSegment { Point = Pt(0) });

            for (var i = 1; i < n; i++)
            {
                var p = Pt(i);
                lineFig.Segments.Add(new LineSegment { Point = p });
                areaFig.Segments.Add(new LineSegment { Point = p });
            }
            areaFig.Segments.Add(new LineSegment { Point = new Point(w, h) });
            areaFig.Segments.Add(new LineSegment { Point = new Point(0, h) });

            line.Figures.Add(lineFig);
            area.Figures.Add(areaFig);
            LinePath.Data = line;
            AreaPath.Data = area;

            var tip = Pt(n - 1);
            TipDot.Margin = new Thickness(tip.X - 3, tip.Y - 3, 0, 0);
            TipDot.Opacity = 0.95;
        }
        catch
        {
            try
            {
                LinePath.Data = null;
                AreaPath.Data = null;
                TipDot.Opacity = 0;
            }
            catch { }
        }
    }
}
