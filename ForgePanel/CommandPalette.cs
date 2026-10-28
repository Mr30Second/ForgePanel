using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ForgePanel;

// Палитра быстрых команд: клик по строке поиска в шапке (или Ctrl+K) открывает список
// «куда перейти и что запустить». Все пункты вызывают те же методы, что и обычные кнопки
// страниц, поэтому палитра ничего не дублирует и не обещает действий, которых в приложении нет.
public partial class MainWindow
{
    private sealed record PaletteItem(string Section, string Title, string Note, string Right, Action Run);

    private TextBox? _paletteQuery;
    private StackPanel? _paletteList;
    private readonly List<PaletteItem> _paletteItems = new();
    private readonly List<Border> _paletteRows = new();
    private int _paletteIndex = -1;

    private bool PaletteOpen => PaletteHost.Visibility == Visibility.Visible;

    private void SearchBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Preview-событие доходит до Border раньше, чем TextBox успевает поставить каретку,
        // поэтому Handled снимает фокус с поля шапки: печатать нужно в поле палитры.
        e.Handled = true;
        if (PaletteOpen) ClosePalette();
        else OpenPalette();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (PaletteOpen) ClosePalette();
            else OpenPalette();
            e.Handled = true;
            return;
        }

        // Esc вне палитры не трогаем: он нужен меню плиток и полям ввода.
        if (e.Key == Key.Escape && PaletteOpen)
        {
            ClosePalette();
            e.Handled = true;
        }
    }

    private void OpenPalette()
    {
        TextBox? query = _paletteQuery;
        if (query == null)
        {
            BuildPalette();
            query = _paletteQuery;
        }
        if (query == null) return;

        // Текст поля шапки переносим сюда, а не чистим: список за фильтром должен совпадать
        // с тем, что уже показывает библиотека.
        query.Text = GlobalSearch.Text;
        RefreshPalette();
        PaletteHost.Visibility = Visibility.Visible;

        // Фокус ставим после того, как хост стал видимым: невидимый элемент фокуса не принимает.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            query.Focus();
            Keyboard.Focus(query);
        }));
    }

    private void ClosePalette()
    {
        PaletteHost.Visibility = Visibility.Collapsed;
    }

    // Каркас строим один раз: содержимое списка меняется, а подложка, поле и подсказка — нет.
    private void BuildPalette()
    {
        var scrim = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0))
        };
        // Клик по затемнению закрывает, клик по самой панели — нет: событие всплывает до
        // подложки, поэтому сравниваем источник.
        scrim.MouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.Source, scrim)) ClosePalette();
        };

        var panel = new StackPanel
        {
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 46, 0, 24)
        };

        var body = new Border
        {
            Background = (Brush)FindResource("Panel"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14)
        };

        var content = new DockPanel { LastChildFill = true };

        var head = new Grid { Margin = new Thickness(14, 11, 10, 11) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glass = new TextBlock
        {
            Text = "⌕",
            FontSize = 17,
            Margin = new Thickness(2, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("AccentSoft")
        };
        Grid.SetColumn(glass, 0);

        var query = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 15,
            MinHeight = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Text")
        };
        Placeholder.SetText(query, "Куда? — сборка, страница или действие");
        Grid.SetColumn(query, 1);

        var close = new TextBlock
        {
            Text = "✕",
            FontSize = 14,
            Margin = new Thickness(8, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Muted")
        };
        Grid.SetColumn(close, 2);

        head.Children.Add(glass);
        head.Children.Add(query);
        head.Children.Add(close);
        DockPanel.SetDock(head, Dock.Top);

        var hint = Txt("↑↓ навигация · Enter выбрать · Esc закрыть", 11, false, (Brush)FindResource("Muted"));
        hint.Margin = new Thickness(15, 9, 15, 11);
        DockPanel.SetDock(hint, Dock.Bottom);

        _paletteList = new StackPanel { Margin = new Thickness(8, 0, 8, 4) };
        var scroll = new ScrollViewer
        {
            Content = _paletteList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 420
        };

        content.Children.Add(head);
        content.Children.Add(hint);
        content.Children.Add(scroll);
        body.Child = content;
        panel.Children.Add(body);
        scrim.Child = panel;

        PaletteHost.Children.Clear();
        PaletteHost.Children.Add(scrim);

        _paletteQuery = query;

        query.TextChanged += (_, _) =>
        {
            // Поле шапки живёт своей жизнью только как фильтр библиотеки, поэтому текст
            // переносим туда и перечитываем список.
            if (GlobalSearch.Text != query.Text) GlobalSearch.Text = query.Text;
            RefreshPalette();
        };
        query.PreviewKeyDown += Palette_PreviewKeyDown;
        close.MouseLeftButtonDown += (_, _) => ClosePalette();
    }

    private void Palette_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up when _paletteRows.Count > 0:
                MovePalette(-1);
                e.Handled = true;
                break;
            case Key.Down when _paletteRows.Count > 0:
                MovePalette(1);
                e.Handled = true;
                break;
            // В перечислении Key значения Enter и Return совпадают (оба 6), поэтому случай один.
            case Key.Return:
                RunPalette();
                e.Handled = true;
                break;
            case Key.Escape:
                ClosePalette();
                e.Handled = true;
                break;
            case Key.Tab:
                // Tab уводил бы фокус в поле шапки, и палитра оставалась висеть без клавиатуры.
                e.Handled = true;
                break;
        }
    }

    private void RefreshPalette()
    {
        if (_paletteList == null || _paletteQuery == null) return;

        string filter = _paletteQuery.Text.Trim();
        _paletteItems.Clear();
        _paletteRows.Clear();
        _paletteList.Children.Clear();

        foreach (PaletteItem item in PaletteCommands()
                     .Where(i => filter.Length == 0 ||
                                 $"{i.Section} {i.Title} {i.Note}".Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            if (_paletteItems.Count == 0 || _paletteItems[^1].Section != item.Section)
            {
                _paletteList.Children.Add(new TextBlock
                {
                    Text = item.Section,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(8, 10, 0, 5),
                    Foreground = (Brush)FindResource("Muted")
                });
            }

            _paletteItems.Add(item);
            _paletteList.Children.Add(PaletteRow(item, _paletteItems.Count - 1));
        }

        if (_paletteItems.Count == 0)
        {
            _paletteList.Children.Add(Txt("Ничего не подошло под этот запрос.", 12, false, (Brush)FindResource("Muted")));
            _paletteIndex = -1;
            return;
        }

        // Первый пункт по умолчанию: с -1 стрелка вниз ушла бы по кругу в конец списка.
        SelectPalette(0);
    }

    private Border PaletteRow(PaletteItem item, int index)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Txt(item.Title, 13, true));
        if (item.Note.Length > 0)
            text.Children.Add(Txt(item.Note, 11, false, (Brush)FindResource("Muted")));

        var right = new TextBlock
        {
            Text = item.Right,
            FontSize = 11,
            Margin = new Thickness(10, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("AccentText")
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(text);
        grid.Children.Add(right);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Tag = index
        };
        row.MouseEnter += (_, _) => SelectPalette(index);
        row.MouseLeftButtonUp += (_, _) =>
        {
            _paletteIndex = index;
            RunPalette();
        };

        _paletteRows.Add(row);
        return row;
    }

    private void MovePalette(int delta)
    {
        if (_paletteRows.Count == 0) return;
        int next = _paletteIndex + delta;
        if (next < 0) next = _paletteRows.Count - 1;
        if (next >= _paletteRows.Count) next = 0;
        SelectPalette(next);
    }

    private void SelectPalette(int index)
    {
        if (index < 0 || index >= _paletteRows.Count) return;
        _paletteIndex = index;

        for (int i = 0; i < _paletteRows.Count; i++)
        {
            bool active = i == index;
            _paletteRows[i].Background = active ? (Brush)FindResource("Pressed") : Brushes.Transparent;
            _paletteRows[i].BorderBrush = active ? (Brush)FindResource("AccentLine") : Brushes.Transparent;
        }

        _paletteRows[index].BringIntoView();
    }

    private void RunPalette()
    {
        if (_paletteIndex < 0 || _paletteIndex >= _paletteItems.Count) return;
        PaletteItem item = _paletteItems[_paletteIndex];
        ClosePalette();
        item.Run();
    }

    // Только те действия, которые в приложении уже работают.
    private List<PaletteItem> PaletteCommands()
    {
        var items = new List<PaletteItem>();

        foreach (ServerProfile p in profiles)
        {
            ServerProfile build = p;
            string action = PaletteBuildAction(build, out Action run);
            items.Add(new PaletteItem("Сборки", build.Name,
                $"{build.Version} · {build.Core} · {build.Edition}", action, run));
        }

        items.Add(new PaletteItem("Перейти", "Главная", "Обзор и быстрый запуск", "→", () => ShowHome()));
        items.Add(new PaletteItem("Перейти", "Библиотека", "Все конфигурации и запуск", "→", () => ShowLibrary()));
        items.Add(new PaletteItem("Перейти", "Каталог", "Моды, датапаки и ядра", "→", () => ShowCatalog()));
        items.Add(new PaletteItem("Перейти", "Загрузки", "Очередь загрузок — пока пусто", "→", () => ShowDownloads()));
        items.Add(new PaletteItem("Перейти", "Файлы", "Папки конфигураций и бэкапы", "→", () => ShowFiles()));
        items.Add(new PaletteItem("Перейти", "Метрики", "Память и диски", "→", () => ShowMetrics()));
        items.Add(new PaletteItem("Перейти", "Настройки", "Ник, акцент, ключи API", "→", () => ShowSettings()));

        items.Add(new PaletteItem("Действия", "Создать новую сборку", "Версия, ядро и порт", "Enter", () => EditProfile(null)));
        items.Add(new PaletteItem("Действия", "Открыть папку данных", dataDir, "→", () => OpenUrl(dataDir)));

        return items;
    }

    // Главное действие сборки совпадает с кнопкой на её плитке: ядро, EULA, запуск или консоль.
    private string PaletteBuildAction(ServerProfile p, out Action run)
    {
        if (Running(p) != null)
        {
            run = () => ShowConsole(p);
            return "Консоль";
        }

        switch (BuildChecker.QuickState(p, dataDir))
        {
            case BuildState.NotInstalled:
                run = () => InstallCore(p);
                return "Установить ядро";
            case BuildState.NeedsAttention:
                run = () => AcceptEula(p);
                return "Принять EULA";
            default:
                run = () => StartServer(p);
                return "Играть";
        }
    }
}
