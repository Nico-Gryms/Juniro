using Juniro.App.Services;

namespace Juniro.App.Controls;

/// <summary>
/// Barre horizontale en trois couleurs : victoire domicile / nul / victoire extérieure,
/// chaque part proportionnelle à sa probabilité. Utilisation en XAML :
///   &lt;controls:ProbabilityBar Home="{Binding PHome}" Draw="{Binding PDraw}" Away="{Binding PAway}" /&gt;
/// </summary>
public sealed class ProbabilityBar : ContentView
{
    public static readonly BindableProperty HomeProperty =
        BindableProperty.Create(nameof(Home), typeof(double), typeof(ProbabilityBar), 0.0, propertyChanged: Changed);
    public static readonly BindableProperty DrawProperty =
        BindableProperty.Create(nameof(Draw), typeof(double), typeof(ProbabilityBar), 0.0, propertyChanged: Changed);
    public static readonly BindableProperty AwayProperty =
        BindableProperty.Create(nameof(Away), typeof(double), typeof(ProbabilityBar), 0.0, propertyChanged: Changed);
    public static readonly BindableProperty BarHeightProperty =
        BindableProperty.Create(nameof(BarHeight), typeof(double), typeof(ProbabilityBar), 8.0, propertyChanged: Changed);

    public double Home { get => (double)GetValue(HomeProperty); set => SetValue(HomeProperty, value); }
    public double Draw { get => (double)GetValue(DrawProperty); set => SetValue(DrawProperty, value); }
    public double Away { get => (double)GetValue(AwayProperty); set => SetValue(AwayProperty, value); }
    public double BarHeight { get => (double)GetValue(BarHeightProperty); set => SetValue(BarHeightProperty, value); }

    private readonly Grid _grid;
    private readonly BoxView _h = new() { Color = Palette.Home, CornerRadius = new CornerRadius(4, 0, 4, 0) };
    private readonly BoxView _d = new() { Color = Palette.Draw };
    private readonly BoxView _a = new() { Color = Palette.Away, CornerRadius = new CornerRadius(0, 4, 0, 4) };

    public ProbabilityBar()
    {
        _grid = new Grid
        {
            ColumnSpacing = 2,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            HeightRequest = BarHeight
        };
        _grid.Add(_h, 0, 0);
        _grid.Add(_d, 1, 0);
        _grid.Add(_a, 2, 0);
        Content = _grid;
    }

    private static void Changed(BindableObject bindable, object oldValue, object newValue) =>
        ((ProbabilityBar)bindable).Update();

    private void Update()
    {
        double h = Math.Max(Home, 0), d = Math.Max(Draw, 0), a = Math.Max(Away, 0);
        if (h + d + a <= 0) { h = d = a = 1; }
        _grid.ColumnDefinitions[0].Width = new GridLength(Math.Max(h, 0.001), GridUnitType.Star);
        _grid.ColumnDefinitions[1].Width = new GridLength(Math.Max(d, 0.001), GridUnitType.Star);
        _grid.ColumnDefinitions[2].Width = new GridLength(Math.Max(a, 0.001), GridUnitType.Star);
        _grid.HeightRequest = BarHeight;
    }
}
