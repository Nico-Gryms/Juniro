using Juniro.App.Services;

namespace Juniro.App.Controls;

/// <summary>
/// Jauge en segments de l'indice d'un pari : Score segments colorés sur Total.
/// Utilisation en XAML :
///   &lt;c:ScoreGauge Score="{Binding Score}" Total="{Binding Total}" Color="{Binding ScoreColor}" HeightRequest="8" /&gt;
/// </summary>
public sealed class ScoreGauge : GraphicsView
{
    public static readonly BindableProperty ScoreProperty =
        BindableProperty.Create(nameof(Score), typeof(int), typeof(ScoreGauge), 0, propertyChanged: Redraw);
    public static readonly BindableProperty TotalProperty =
        BindableProperty.Create(nameof(Total), typeof(int), typeof(ScoreGauge), 10, propertyChanged: Redraw);
    public static readonly BindableProperty ColorProperty =
        BindableProperty.Create(nameof(Color), typeof(Color), typeof(ScoreGauge), Palette.Accent, propertyChanged: Redraw);

    public int Score { get => (int)GetValue(ScoreProperty); set => SetValue(ScoreProperty, value); }
    public int Total { get => (int)GetValue(TotalProperty); set => SetValue(TotalProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    private readonly GaugeDrawable _drawable = new();

    public ScoreGauge()
    {
        Drawable = _drawable;
        BackgroundColor = Colors.Transparent;
        HeightRequest = 8;
    }

    private static void Redraw(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (ScoreGauge)bindable;
        view._drawable.Score = view.Score;
        view._drawable.Total = view.Total;
        view._drawable.Color = view.Color ?? Palette.Accent;
        view.Invalidate();
    }

    private sealed class GaugeDrawable : IDrawable
    {
        public int Score { get; set; }
        public int Total { get; set; } = 10;
        public Color Color { get; set; } = Palette.Accent;

        public void Draw(ICanvas canvas, RectF rect)
        {
            int n = Math.Max(Total, 1);
            const float gap = 3;
            float w = (rect.Width - gap * (n - 1)) / n;
            if (w <= 0) return;
            float radius = Math.Min(3, rect.Height / 2);
            for (int i = 0; i < n; i++)
            {
                canvas.FillColor = i < Score ? Color : Palette.Line;
                canvas.FillRoundedRectangle(rect.X + i * (w + gap), rect.Y, w, rect.Height, radius);
            }
        }
    }
}
