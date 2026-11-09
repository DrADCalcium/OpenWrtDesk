using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace OpenWrtDesk;

public sealed class TrendChart : Grid
{
    private readonly Canvas canvas = new(); private readonly double[] first, second; private readonly bool percent;
    public TrendChart(IEnumerable<double> a, IEnumerable<double> b, bool isPercent = false)
    {
        first = a.TakeLast(120).ToArray(); second = b.TakeLast(120).ToArray(); percent = isPercent;
        Height = 160; MinWidth = 200; Children.Add(canvas); SizeChanged += (_, _) => Draw();
    }
    private void Draw()
    {
        canvas.Children.Clear(); var width = ActualWidth; if (width < 1) return;
        var max = percent ? 100 : Math.Max(1, first.Concat(second).Where(double.IsFinite).DefaultIfEmpty(0).Max() * 1.12);
        for (var i = 0; i <= 3; i++) { var y = 12 + i * 40; canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 128, 128, 128)), StrokeThickness = 1 }); }
        Add(first, Windows.UI.Color.FromArgb(255, 0, 164, 147)); Add(second, Windows.UI.Color.FromArgb(255, 86, 127, 234));
        if (first.Length + second.Length < 4) canvas.Children.Add(new TextBlock { Text = "正在采集趋势数据…", Margin = new Thickness(0, 64, 0, 0), FontSize = 13 });
        void Add(double[] values, Windows.UI.Color color)
        {
            if (values.Length < 2) return; var points = new PointCollection();
            void Flush() { if (points.Count > 1) canvas.Children.Add(new Polyline { Points = points, Stroke = new SolidColorBrush(color), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round }); points = new PointCollection(); }
            for (var i = 0; i < values.Length; i++)
            {
                if (!double.IsFinite(values[i])) { Flush(); continue; }
                points.Add(new Point(i * (width - 2) / Math.Max(1, values.Length - 1), 136 - Math.Clamp(values[i] / max, 0, 1) * 120));
            }
            Flush();
        }
    }
}
