using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ForgePanel;

// Одна точка истории: сколько ядер грел процесс в эту секунду и сколько памяти он держал.
public sealed record LoadSample(double Cores, double Bytes);

// Замер нагрузки сервера. Время ЦП у процесса — счётчик накопительный, поэтому загрузка
// считается как прирост между двумя опросами, а не по одному снимку: ровно так же, как это
// делает диспетчер задач. Память берётся рабочая (WorkingSet64) — то, что физически занято
// в RAM сейчас, а не всё, что JVM когда-либо резервировала.
public sealed class LoadHistory
{
    // Точка в секунду, две минуты на экране: чаще нет смысла — сам сервер считает TPS два раза
    // в секунду, а 120 точек ещё читаются с графика одним взглядом.
    public const int IntervalMs = 1000;
    public const int Capacity = 120;

    private readonly List<LoadSample> samples = new();

    private TimeSpan lastCpu;
    private DateTime lastAt;
    private bool primed;

    public LoadHistory(int coreCount) => CoreCount = Math.Max(1, coreCount);

    // Сколько логических ядер у машины — в них и считается шкала: «2,4 ядра» читается быстрее,
    // чем «30 % от восьми поточного процессора».
    public int CoreCount { get; }

    public IReadOnlyList<LoadSample> Samples => samples;
    public double LastCores => samples.Count == 0 ? 0 : samples[^1].Cores;
    public double LastBytes => samples.Count == 0 ? 0 : samples[^1].Bytes;
    public double PeakCores => samples.Count == 0 ? 0 : samples.Max(s => s.Cores);
    public double AverageCores => samples.Count == 0 ? 0 : samples.Average(s => s.Cores);
    public double PeakBytes => samples.Count == 0 ? 0 : samples.Max(s => s.Bytes);

    // Верхний процентиль по ядрам, а не максимум: десять секунд запуска, когда JVM прогревает все
    // шестнадцать ядер, прижимали бы рабочую линию к нулю на две минуты — ровно тогда, когда в
    // график и смотрят. Всплеск остаётся подписан числом в строке «пик».
    public double UpperCores(double fraction)
    {
        if (samples.Count == 0) return 0;

        var sorted = samples.Select(s => s.Cores).OrderBy(v => v).ToList();
        int index = (int)Math.Round(Math.Clamp(fraction, 0, 1) * (sorted.Count - 1));
        return sorted[index];
    }

    // Проценты от всей машины: 100 % — это все ядра, занятые сервером одновременно.
    public double PercentOfMachine(double cores) => cores / CoreCount * 100;

    public void Prime(ServerInstance instance)
    {
        samples.Clear();
        primed = false;
        Add(instance);
    }

    // null — процесс нельзя опросить (завершён), и странице больше не нужно повторять замер.
    // Первая точка после Prime не добавляется: без предыдущего замера дельты нет, а нулевая
    // точка на графике выглядела бы как «сервер ничего не ест».
    public LoadSample? Add(ServerInstance instance)
    {
        if (!instance.TrySampleLoad(out TimeSpan cpu, out long workingSet)) return null;

        DateTime now = DateTime.UtcNow;
        if (!primed)
        {
            lastCpu = cpu;
            lastAt = now;
            primed = true;
            return null;
        }

        double wallSeconds = (now - lastAt).TotalSeconds;
        double cpuSeconds = (cpu - lastCpu).TotalSeconds;
        lastCpu = cpu;
        lastAt = now;

        // Дельта меньше половины шага — это не замер, а шум: при мгновенной остановке таймера
        // крошечное время дало бы гигантские проценты. Такую точку просто пропускаем.
        if (wallSeconds * 1000 < IntervalMs / 2) return null;

        var sample = new LoadSample(Math.Max(0, cpuSeconds / wallSeconds), workingSet);
        samples.Add(sample);
        if (samples.Count > Capacity) samples.RemoveAt(0);
        return sample;
    }
}

// Линейный график. Готового Chart в WPF нет, а тянуть библиотеку ради двух полосок не хочется:
// в проекте NuGet появляется только там, где без него никак (WebP-иконки).
public sealed class Sparkline : FrameworkElement
{
    private IReadOnlyList<double> values = Array.Empty<double>();

    public IReadOnlyList<double> Values
    {
        get => values;
        set
        {
            values = value ?? Array.Empty<double>();
            InvalidateVisual();
        }
    }

    // Выше чего линия считается полной высотой шкалы. Задаётся стабильная величина — все ядра
    // для процессора, -Xmx для памяти, — чтобы график не прыгал от точки к точке.
    public double AxisMax { get; set; } = 1;

    // Пунктир на этой высоте: «граница, о которой договаривались» — 1 ядро для Minecraft,
    // потолок -Xmx для памяти.
    public double? MarkerValue { get; set; }

    public Brush LineBrush { get; set; } = Brushes.Transparent;
    public Brush AreaBrush { get; set; } = Brushes.Transparent;
    public Brush GridBrush { get; set; } = Brushes.Transparent;
    public Brush MarkerBrush { get; set; } = Brushes.Transparent;

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 56 : availableSize.Height);

    // Кривая считается по ActualWidth, поэтому при любой смене размера (окно, другая ширина
    // колонки) график перерисовываем: без этого линия осталась бы растянутой на старый край.
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width < 8 || height < 8) return;

        var grid = new Pen(GridBrush, 1);
        for (int step = 1; step < 4; step++)
        {
            double y = Math.Round(height * step / 4.0) + 0.5;
            dc.DrawLine(grid, new Point(0, y), new Point(width, y));
        }

        if (MarkerValue is double marker && AxisMax > 0 && marker > 0 && marker <= AxisMax)
        {
            double line = height - height * (marker / AxisMax);
            dc.DrawLine(new Pen(MarkerBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) },
                new Point(0, line), new Point(width, line));
        }

        if (values.Count < 2 || AxisMax <= 0) return;

        var points = new List<Point>(values.Count);
        double slot = width / (values.Count - 1);
        foreach (double value in values)
        {
            if (!double.IsFinite(value)) continue;
            double ratio = Math.Clamp(value / AxisMax, 0, 1);
            points.Add(new Point(slot * points.Count, height - 1 - ratio * (height - 2)));
        }
        if (points.Count < 2) return;

        dc.DrawGeometry(AreaBrush, null, Area(points, height));
        dc.DrawGeometry(null, new Pen(LineBrush, 1.6), Line(points));
    }

    private Geometry Line(List<Point> points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = false, IsFilled = false };
        figure.Segments.Add(new PolyLineSegment(points, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    // Заливка: та же кривая, только начинается и кончается на основании графика.
    private Geometry Area(List<Point> points, double height)
    {
        var figure = new PathFigure
        {
            StartPoint = new Point(points[0].X, height),
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new PolyLineSegment(points, true));
        figure.Segments.Add(new LineSegment(new Point(points[^1].X, height), true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
