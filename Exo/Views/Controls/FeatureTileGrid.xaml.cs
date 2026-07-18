using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Exo.Views.Controls;

public sealed partial class FeatureTileGrid : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(object),
            typeof(FeatureTileGrid),
            new PropertyMetadata(null));

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ItemsRepeater TileRepeaterControl => TileRepeater;

    public FeatureTileGrid()
    {
        InitializeComponent();
    }

}
