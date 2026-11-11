using Juniro.App.Models;
using Juniro.App.Services;
using MauiHAlign = Microsoft.Maui.Graphics.HorizontalAlignment;
using MauiVAlign = Microsoft.Maui.Graphics.VerticalAlignment;

namespace Juniro.App.Controls;

/// <summary>
/// Courbe dessinée à la main (sans bibliothèque) : évolution du ROI ou du bénéfice cumulé.
/// Ligne verte si la dernière valeur est positive, rouge sinon, zone colorée jusqu'à zéro.
/// Utilisation en XAML :
///   &lt;controls:LineChartView Points="{Binding ChartPoints}" Suffix=" %" HeightRequest="220" /&gt;
/// </summary>
public sealed class LineChartView : GraphicsView
{
    public static readonly BindableProperty PointsProperty =
        BindableProperty.Create(nameof(Points), typeof(IList<ChartPoint>), typeof(LineChartView), null, propertyChanged: Redraw);
    public static readonly BindableProperty SuffixProperty =
        BindableProperty.Create(nameof(Suffix), typeof(string), typeof(LineChartView), "", propertyChanged: Redraw);

    public IList<ChartPoint>? Points { get => (IList<ChartPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }

    private readonly ChartDrawable _drawable = new();

    public LineChartView()
    {
        Drawable = _drawable;
        BackgroundColor = Colors.Transparent;
    }

    private static void Redraw(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (LineChartView)bindable;
        view._drawable.Points = view.Points ?? Array.Empty<ChartPoint>();
        view._drawable.Suffix = view.Suffix ?? "";
        view.Invalidate();
    }

    private sealed class ChartDrawable : IDrawable
    {
        public IList<ChartPoint> Points { get; set; } = Array.Empty<ChartPoint>();
        public string Suffix { get; set; } = "";

        public void Draw(ICanvas canvas, RectF rect)
        {
            canvas.FontColor = Palette.TextMuted;
            canvas.FontSize = 11;
            if (Points.Count < 2)
            {
                canvas.DrawString("Pas encore assez de paris réglés pour tracer la courbe.",
                    rect.X, rect.Y, rect.Width, rect.Height, MauiHAlign.Center, MauiVAlign.Center);
                return;
            }

            const float left = 52, right = 12, top = 14, bottom = 24;
            var plot = new RectF(rect.X + left, rect.Y + top, rect.Width - left - right, rect.Height - top - bottom);
            if (plot.Width <= 10 || plot.Height <= 10) return;

            double min = Math.Min(0, Points.Min(p => p.Y));
            double max = Math.Max(0, Points.Max(p => p.Y));
            if (Math.Abs(max - min) < 1e-9) { max += 1; min -= 1; }
            double pad = (max - min) * 0.08;
            min -= pad; max += pad;

            float X(int i) => plot.X + plot.Width * i / (Points.Count - 1);
            float Y(double v) => plot.Bottom - (float)((v - min) / (max - min)) * plot.Height;

            // Grille horizontale et graduations
            canvas.StrokeColor = Palette.Line;
            canvas.StrokeSize = 1;
            for (int g = 0; g <= 4; g++)
            {
                double v = min + (max - min) * g / 4;
                float y = Y(v);
                canvas.DrawLine(plot.X, y, plot.Right, y);
                canvas.DrawString($"{v.ToString("F1", Fmt.Fr)}{Suffix}", rect.X, y - 7, left - 6, 14, MauiHAlign.Right, MauiVAlign.Center);
            }

            // Ligne du zéro, en pointillés
            canvas.StrokeColor = Palette.TextDim;
            canvas.StrokeDashPattern = new float[] { 4, 4 };
            canvas.DrawLine(plot.X, Y(0), plot.Right, Y(0));
            canvas.StrokeDashPattern = null;

            double last = Points[^1].Y;
            var color = last >= 0 ? Palette.Win : Palette.Lose;

            // Zone colorée entre la courbe et zéro
            var area = new PathF();
            area.MoveTo(X(0), Y(0));
            for (int i = 0; i < Points.Count; i++) area.LineTo(X(i), Y(Points[i].Y));
            area.LineTo(X(Points.Count - 1), Y(0));
            area.Close();
            canvas.FillColor = color.WithAlpha(0.15f);
            canvas.FillPath(area);

            // Courbe
            var line = new PathF();
            line.MoveTo(X(0), Y(Points[0].Y));
            for (int i = 1; i < Points.Count; i++) line.LineTo(X(i), Y(Points[i].Y));
            canvas.StrokeColor = color;
            canvas.StrokeSize = 2.5f;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.DrawPath(line);

            // Dernier point et sa valeur
            canvas.FillColor = color;
            canvas.FillCircle(X(Points.Count - 1), Y(last), 4);
            canvas.FontColor = color;
            canvas.FontSize = 12;
            canvas.DrawString($"{(last >= 0 ? "+" : "")}{last.ToString("F1", Fmt.Fr)}{Suffix}",
                plot.Right - 120, Y(last) - 22, 120, 16, MauiHAlign.Right, MauiVAlign.Center);

            // Dates de début et de fin
            canvas.FontColor = Palette.TextDim;
            canvas.FontSize = 10;
            canvas.DrawString(Points[0].X.ToString("dd/MM/yy"), plot.X, plot.Bottom + 6, 80, 14, MauiHAlign.Left, MauiVAlign.Top);
            canvas.DrawString(Points[^1].X.ToString("dd/MM/yy"), plot.Right - 80, plot.Bottom + 6, 80, 14, MauiHAlign.Right, MauiVAlign.Top);
        }
    }
}
