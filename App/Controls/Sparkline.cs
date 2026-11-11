using Juniro.App.Services;

namespace Juniro.App.Controls;

/// <summary>
/// Mini-courbe (évolution de l'Elo sur les 10 derniers matchs).
/// Utilisation en XAML :
///   &lt;c:Sparkline Values="{Binding Points}" LineColor="{Binding DeltaColor}" WidthRequest="56" HeightRequest="22" /&gt;
/// </summary>
public sealed class Sparkline : GraphicsView
{
    public static readonly BindableProperty ValuesProperty =
        BindableProperty.Create(nameof(Values), typeof(IList<double>), typeof(Sparkline), null, propertyChanged: Redraw);
    public static readonly BindableProperty LineColorProperty =
        BindableProperty.Create(nameof(LineColor), typeof(Color), typeof(Sparkline), Palette.Accent, propertyChanged: Redraw);

    public IList<double>? Values { get => (IList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Color LineColor { get => (Color)GetValue(LineColorProperty); set => SetValue(LineColorProperty, value); }

    private readonly LineDrawable _drawable = new();

    public Sparkline()
    {
        Drawable = _drawable;
        BackgroundColor = Colors.Transparent;
    }

    private static void Redraw(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (Sparkline)bindable;
        view._drawable.Values = view.Values ?? Array.Empty<double>();
        view._drawable.Color = view.LineColor ?? Palette.Accent;
        view.Invalidate();
    }

    private sealed class LineDrawable : IDrawable
    {
        public IList<double> Values { get; set; } = Array.Empty<double>();
        public Color Color { get; set; } = Palette.Accent;

        public void Draw(ICanvas canvas, RectF rect)
        {
            if (Values.Count < 2) return;
            double min = Values.Min(), max = Values.Max();
            double span = Math.Max(max - min, 1);
            const float pad = 2;
            float w = rect.Width - 2 * pad, h = rect.Height - 2 * pad;
            var path = new PathF();
            for (int i = 0; i < Values.Count; i++)
            {
                float x = rect.X + pad + w * i / (Values.Count - 1);
                float y = rect.Y + pad + h * (float)(1 - (Values[i] - min) / span);
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            canvas.StrokeColor = Color;
            canvas.StrokeSize = 1.8f;
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.DrawPath(path);
        }
    }
}
