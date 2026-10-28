using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace ForgePanel;

// Размер и место окна переживают перезапуск. Хранятся отдельным файлом, а не в settings.json:
// в том лежит персональный ключ CurseForge, и запись при каждом закрытии панели затирала бы его
// ровно в тот аварийный момент, когда испорченный файл ещё можно поправить текстовым редактором.
public sealed class WindowGeometry
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    private static string FileName(string dataDir) => Path.Combine(dataDir, "window.json");

    // Испорченный window.json — это «нет сохранённого размера», а не авария: чинить в нём нечего,
    // а на следующем закрытии файл всё равно пишется заново. Худший случай — окно по центру.
    public static WindowGeometry? Load(string dataDir)
    {
        try
        {
            string file = FileName(dataDir);
            return File.Exists(file) ? JsonSerializer.Deserialize<WindowGeometry>(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // RestoreBounds, а не Left/Width: в развёрнутом состоянии Left/Top уводят окно за край экрана
    // на толщину невидимой рамки WindowChrome, и такой сохранённый прямоугольник был бы битым.
    // За свёрнутого окна то же свойство отдаёт прежние границы, а не -32000.
    public static WindowGeometry Capture(Window window)
    {
        Rect bounds = window.RestoreBounds;
        return new WindowGeometry
        {
            Left = Math.Round(bounds.Left),
            Top = Math.Round(bounds.Top),
            Width = Math.Round(bounds.Width),
            Height = Math.Round(bounds.Height),
            Maximized = window.WindowState == WindowState.Maximized
        };
    }

    public void Save(string dataDir)
    {
        // Пишем в момент, когда панель уже закрывается: модалка из-за незаписанного размера
        // задержала бы закрытие ради того, что человек в этот запуск больше не увидит.
        try
        {
            SafeWrite.Json(FileName(dataDir), this, keepPrevious: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write(ex, "память размера окна");
        }
    }

    // Прошлый экран мог стать меньше, а монитор — исчезнуть вовсе, поэтому сначала проверяем,
    // что влезает в теперешний, и только потом решаем, куда окно ставить.
    public void Apply(Window window)
    {
        // WPF отдаёт границы всех мониторов в тех же DIP, в которых живут Left/Top/Width/Height окна,
        // поэтому сравнение без пересчёта на масштаб экрана.
        Rect desk = new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        double width = Math.Round(Math.Clamp(Width, window.MinWidth, desk.Width));
        double height = Math.Round(Math.Clamp(Height, window.MinHeight, desk.Height));

        double left = Left;
        double top = Top;

        // Кусок полосы заголовка в 160 DIP — то, за что окно можно схватить мышью и вытащить.
        // Не попадает ни на один живой монитор (монитор убрали, разрешение поменялось) — значит
        // ставим окно заново по центру рабочей области, но размером, который человек помнил.
        if (!desk.IntersectsWith(new Rect(left, top, Math.Min(width, 160), 42)))
        {
            Rect work = SystemParameters.WorkArea;
            left = work.Left + Math.Max(0, (work.Width - width) / 2);
            top = work.Top + Math.Max(0, (work.Height - height) / 2);
        }

        // «Центр экрана» из XAML применяется уже после конструктора и затёр бы координаты,
        // поэтому положение переводим в ручное до того, как зададим Left/Top.
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Math.Round(left);
        window.Top = Math.Round(top);
        window.Width = width;
        window.Height = height;

        // Развёрнутое окно Windows разворачивает на том мониторе, где лежит его обычный
        // (свёрнутый в нормальный размер) прямоугольник, так что память работает и для «во весь экран».
        if (Maximized) window.WindowState = WindowState.Maximized;
    }
}
