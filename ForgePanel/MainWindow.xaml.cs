using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ForgePanel;

public partial class MainWindow : Window
{
    private const double TileGap = 16;

    // Цвета берутся из палитры приложения, а не из констант: Theme.Apply меняет сами кисти,
    // и переключённая тема доходит до всех элементов, включая уже отрисованные страницы.
    private Brush TileForeground => (Brush)FindResource("Text");
    private Brush OkBrush => (Brush)FindResource("Ok");
    private Brush WarnBrush => (Brush)FindResource("Warn");

    // Ядра разные для платформ: у Bedrock своё семейство серверных программ.
    // Порядок в этом списке — порядок в выпадающем списке и в каталоге: сначала те, что панель
    // ставит сама, ниже — те, где файл ядра человек берёт сам и кладёт в папку сборки.
    // Note — короткая строка для плитки: в неё не влезает длинная причина из CoreInstaller,
    // поэтому здесь только суть, а подробности показывает окно «Это ядро ставится вручную».
    private static readonly (string Name, string Edition, string Url, string Note)[] CoreList =
    {
        ("Paper", "Java", "https://papermc.io/downloads/paper", "панель устанавливает сама"),
        ("Folia", "Java", "https://papermc.io/downloads/folia", "панель устанавливает сама"),
        ("Leaf", "Java", "https://github.com/Winds-Studio/Leaf/releases", "панель устанавливает сама"),
        ("Vanilla", "Java", "https://www.minecraft.net/download/server", "панель устанавливает сама"),
        ("Fabric", "Java", "https://fabricmc.net/use/server/", "панель устанавливает сама"),
        ("Forge", "Java", "https://files.minecraftforge.net/", "панель запускает установщик"),
        ("NeoForge", "Java", "https://neoforged.net/", "панель запускает установщик"),
        ("Quilt", "Java", "https://quiltmc.org/install/server/",
         "сервис не отдаёт готовый server jar · вручную"),
        ("Pufferfish", "Java", "https://github.com/pufferfish-gg/Pufferfish",
         "релизов на GitHub нет, CI не отвечает · вручную"),
        ("Mohist", "Java", "https://mohistmc.com/download",
         "сервис сборок не отвечает · jar с сайта вручную"),
        ("Magma", "Java", "https://magmafoundation.org/download",
         "API недоступен (522) · jar с сайта вручную"),
        ("Bedrock Dedicated Server", "Bedrock", "https://www.minecraft.net/en-us/download/server/bedrock",
         "архив .zip: распакуй его в папку сборки"),
        ("Nukkit", "Bedrock", "https://github.com/CloudburstMC/Nukkit/releases",
         "свежие релизы без jar · ставится вручную"),
        ("PowerNukkit", "Bedrock", "https://github.com/PowerNukkit/PowerNukkit/releases",
         "готовый jar ищешь сам на странице релизов")
    };

    // Ядро в списке подписано, а в конфигурации хранится голое имя: по нему панель решает,
    // чем устанавливать и что показывать в проверке сборки.
    private sealed class CoreOption
    {
        public CoreOption(string name, bool manual)
        {
            Name = name;
            Manual = manual;
        }

        public string Name { get; }
        public bool Manual { get; }

        public override string ToString() => Manual ? Name + " · вручную" : Name;
    }

    private static bool CoreIsManual(string? core) => !CoreInstaller.IsAuto(core);

    // Публичного JSON-манифеста версий Bedrock нет, поэтому список построить нельзя — только ручной ввод.
    private const string BedrockVersionHint =
        "Bedrock: официального списка версий нет — введи номер вручную, например 1.21.40.03.";

    // Подсказка под списком ядер, когда выбрано «Своё ядро»: где взять файл и что панель с ним сделает.
    private const string CustomCoreHint =
        "Такое ядро панель не качает — файл берёшь ты. Положи .jar в папку сборки: проверка найдёт его " +
        "и покажет, что запускаться будет именно он. Версию Minecraft укажи ту, под которую ядро собрано: " +
        "по ней панель подбирает Java.";

    // Ссылка на Discord: панель только открывает её в браузере и ничего туда не отправляет.
    private const string DiscordInvite = "https://discord.gg/WAJ58GdGhN";

    // Значения загрузчиков, которые понимает фильтр facets на Modrinth.
    private static readonly (string Label, string? Value)[] ModLoaders =
    {
        ("Любой загрузчик", null),
        ("Fabric", "fabric"),
        ("NeoForge", "neoforge"),
        ("Forge", "forge"),
        ("Quilt", "quilt"),
        ("Paper", "paper"),
        ("Spigot", "spigot"),
        ("Bukkit", "bukkit")
    };

    private readonly string dataDir = PanelPaths.DataDir;
    private List<ServerProfile> profiles = new();
    private string currentPage = "home";

    // Что панель должна рассказать о библиотеке сразу после запуска: аварию чтения нельзя
    // показывать модалкой из конструктора — окно ещё не открыто.
    private string startupNotice = "";

    private double _viewportWidth;
    private double _tileMinWidth = 320;
    private int _lastColumns = 2;
    private bool _reflowing;
    private bool _editing;
    private string _pageToken = "";
    private static readonly Duration PageTransition = new(TimeSpan.FromMilliseconds(190));
    private string? _libraryChip;

    // Плитки или строчки, всё или избранное — помнится между запусками в view.json.
    private LibraryView libraryView = new();

    // Состояние каталога модов: перестройка сетки при изменении размера окна не должна его сбрасывать.
    private string _catalogMode = "mods";
    private string _catalogSource = "modrinth";
    private string _modQuery = "";
    private string? _modLoader;
    private string? _modVersion;
    private string? _modTarget;
    // «mod» или «datapack»: по Modrinth ищем разное, и файл кладём в разную папку.
    private string _modKind = "mod";
    private string _modStatus = "Поиск по публичному API Modrinth. Файл сохраняется в папку mods выбранной сборки.";
    private List<CatalogCard> _cards = new();
    private List<string> _modVersionIds = new();
    private AppSettings settings = new();

    // Одинаковая карточка для обоих источников: дальше всё зависит только от Source.
    private sealed record CatalogCard(
        string Source, string Id, string Title, string Author, string Description,
        long Downloads, string PageUrl, string IconUrl, string Kind = "mod");

    public MainWindow()
    {
        InitializeComponent();

        Directory.CreateDirectory(dataDir);
        (settings, string settingsError) = AppSettings.Load(dataDir);
        if (settingsError.Length > 0)
        {
            StartupNotice("settings.json не прочитался: " + settingsError + "\n\nПанель открылась со значениями " +
                "по умолчанию — акцент, контакт Modrinth и ключ CurseForge теперь пустые, их нужно ввести заново. " +
                "Сам файл остался на месте и ничего поверх не записано, пока ты не нажмёшь «Сохранить».");
        }

        // Размер и место прошлого запуска. Проверка, налезает ли сохранённое окно на теперешние
        // экраны, живёт внутри Apply: монитор могли убрать, а масштаб — поменять.
        WindowGeometry? geometry = WindowGeometry.Load(dataDir);
        geometry?.Apply(this);

        // 1255 в XAML — пожелание, а не гарантия: при масштабировании экрана больше 100 % столько
        // же DIP уже не влезают в рабочую область, и нижняя подсказка уходила бы за край. Берём
        // меньшее из запрошенного и доступного, поэтому окно всегда открывается целым.
        // Этот расчёт касается только первого запуска: размер, который человек задал сам и который
        // панель запомнила, важнее чужого пожелания.
        if (geometry == null)
        {
            Height = Math.Max(MinHeight, Math.Min(Height, Math.Round(SystemParameters.WorkArea.Height - 46)));
        }

        Theme.Apply(settings.Accent);
        Modrinth.ApplyUserAgent(settings.ModrinthContact);
        CoreInstaller.ApplyUserAgent(settings.ModrinthContact);
        ApplyAccount();
        libraryView = LibraryView.Load(dataDir);
        LoadProfiles();
        StartRestartTimer();
        ShowHome();

        // Про испорченный файл говорим сразу, но не из конструктора: окно ещё не показано, и
        // модалка в этот момент встала бы поверх несуществующей панели. Очередь диспетчера отдаёт её,
        // когда интерфейс уже на экране.
        if (startupNotice.Length > 0)
        {
            string notice = startupNotice;
            Dispatcher.BeginInvoke(new Action(() => Dialog.Info(notice, "Панель нашла испорченный файл")),
                DispatcherPriority.Background);
        }
    }

    // Обложка и бейдж плитки берут цвета из палитры, а не из констант: после смены темы страница
    // перестраивается, и градиент сам становится светлым. Java оттеняем акцентом, Bedrock — нейтрально.
    private Brush CoverBrush(bool bedrock)
    {
        var brush = new LinearGradientBrush(
            ColorOf(bedrock ? "SearchBg" : "AccentSoftBg"), ColorOf("Bg"), 90);
        brush.Freeze();
        return brush;
    }

    private Brush BadgeBrush(bool bedrock) =>
        new SolidColorBrush(ColorOf(bedrock ? "Panel2" : "Pressed"));

    private Color ColorOf(string key) => ((SolidColorBrush)FindResource(key)).Color;

    // Ядра платформы в том порядке, в каком их показывает список. Пустой список недопустим:
    // поле «Ядро» обязано иметь что выбрать, поэтому неизвестная платформа (её приносит
    // импортированный JSON) получает Java-набор.
    // «Своё ядро» — последний пункт у обеих платформ: файла нет ни в одном списке, его берёт человек.
    private static IEnumerable<string> CoresFor(string edition)
    {
        var names = CoreList.Where(c => c.Edition == edition).Select(c => c.Name).ToList();
        if (names.Count == 0) names.AddRange(CoreList.Where(c => c.Edition == "Java").Select(c => c.Name));
        names.Add(CoreInstaller.CustomCore);
        return names;
    }

    // Принадлежит ли ядро платформе — с учётом того, что «Своё ядро» есть у обеих.
    private static bool CoreBelongs(string edition, string? core) =>
        !string.IsNullOrWhiteSpace(core) &&
        (string.Equals(core, CoreInstaller.CustomCore, StringComparison.OrdinalIgnoreCase) ||
         CoreList.Any(c => c.Edition == edition && string.Equals(c.Name, core, StringComparison.OrdinalIgnoreCase)));

    private string DataFile => Path.Combine(dataDir, "servers.json");

    // Копия, которую SafeWrite ведёт при каждой записи библиотеки. Единственный способ пережить
    // испорченный servers.json, поэтому она и только она решает, будет что восстанавливать.
    private string BackupFile => DataFile + ".bak";

    private void LoadProfiles()
    {
        (profiles, bool recovered) = ReadLibrary();

        if (profiles.Count == 0)
        {
            profiles.Add(new ServerProfile { Name = "Ванилла", Edition = "Java", Version = "1.21.1", Core = "Vanilla", Port = 25565, MemoryGb = 4 });
            SaveProfiles();
            return;
        }

        // Спасённую копию возвращаем на место сразу: иначе servers.json лежит пустой до первой
        // правки сборки, и панель, закрытая и открытая заново, теряет записи во второй раз.
        if (recovered) SaveProfiles();
    }

    // Чтение библиотеки с правом на ошибку. Целый, но пустой файл — это пустая библиотека;
    // нечитаемый файл — авария. Раньше авария значила молчаливую потерю: панель заводила
    // демо-профиль и тут же писала его поверх битого файла, доубивая то, что ещё можно было
    // вытащить руками. Теперь битый файл уходит в карантин, а библиотека продолжается с копии.
    // Второй признак — «взято из копии» — нужен LoadProfiles: сохранять здесь нельзя, потому что
    // во время чтения поле profiles ещё прежнее, пустое, и запись уложила бы пустоту поверх
    // только что спасённой библиотеки. Именно так и случилось на первом же тесте восстановления.
    private (List<ServerProfile> Profiles, bool Recovered) ReadLibrary()
    {
        if (!File.Exists(DataFile)) return (new(), false);

        (List<ServerProfile>? current, string error) = TryReadProfiles(DataFile);
        if (current != null) return (current, false);

        string aside = MoveBrokenAside(DataFile);
        static string Describe(string file) => file.Length == 0
            ? "переименовать битый файл не удалось — он остался на месте под прежним именем"
            : $"битый файл не удалён, он лежит рядом как «{Path.GetFileName(file)}»";

        (List<ServerProfile>? backup, string backupError) = TryReadProfiles(BackupFile);

        if (backup is { Count: > 0 })
        {
            StartupNotice($"servers.json не прочитался: {error}\n\nПанель взяла копию servers.json.bak — " +
                $"записей в ней: {backup.Count}. {Describe(aside)}. Если в старом файле было важное, его " +
                "ещё можно поправить текстовым редактором и вернуть на место.");
            return (backup, true);
        }

        StartupNotice(backup == null
            ? $"servers.json не прочитался: {error}\n\nКопия servers.json.bak тоже не читается: {backupError}. " +
              $"Библиотека сейчас пуста, и панель ничего не пишет поверх битого файла — {Describe(aside)}."
            : $"servers.json не прочитался: {error}\n\nКопия servers.json.bak целая, но пустая — " +
              $"восстанавливать нечего. {Describe(aside)}.");
        return (new(), false);
    }

    // Очередь сообщений на первый запуск. Их может быть два сразу — испорченный файл сборок и
    // испорченный файл настроек, и второй не должен затирать первый.
    private void StartupNotice(string text) =>
        startupNotice = startupNotice.Length == 0 ? text : startupNotice + "\n\n" + text;

    // Разбор файла намеренно широкий: тут встречается и битый JSON, и оборванная строка, и файл,
    // который держит антивирус. Любой из этих случаев — одно и то же «не читается», а не повод
    // ронять панель на старте.
    private static (List<ServerProfile>?, string) TryReadProfiles(string file)
    {
        if (!File.Exists(file)) return (null, "файла нет на диске");

        try
        {
            return (JsonSerializer.Deserialize<List<ServerProfile>>(File.ReadAllText(file)) ?? new(), "");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    // Битый файл не удаляется ни при каких условиях: в нём имена сборок, пути и порты, которые
    // человек вполне может поправить сам.
    private string MoveBrokenAside(string file)
    {
        string aside = file + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        try
        {
            File.Move(file, aside);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    // Ошибка записи — не молчание: если библиотека не легла на диск, человек обязан знать, что
    // всё изменённое в этом окне живёт до закрытия панели.
    private void SaveProfiles()
    {
        try
        {
            SafeWrite.Json(DataFile, profiles, keepPrevious: true);
        }
        catch (Exception ex)
        {
            string text = $"Библиотека не записана: {ex.Message}\n\nФайл {DataFile} остался прежним — то, что " +
                "изменилось в этом окне, пропадёт при перезапуске панели.";

            // Пока окно не показано, модалку показывать нельзя (см. конструктор), и ошибка ждёт
            // своей очереди вместе с остальными сообщениями запуска.
            if (IsLoaded) Dialog.Info(text, "Не сохранено");
            else StartupNotice(text);
        }
    }

    private void SetHeader(string eyebrow, string title, string subtitle)
    {
        Eyebrow.Text = eyebrow;
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }

    // Контент страницы плюс мягкое появление. Оживляем только смену страницы: ShowLibrary
    // вызывается на каждое нажатие клавиши в поиске и на каждый ресайз, и анимировать это
    // значит получить мерцание вместо перехода.
    private void SetPage(UIElement content, string token)
    {
        // Консоль — единственная страница, которой нужно занять всю высоту: журнал растянется на
        // всё свободное место только тогда, когда родитель перестаёт давать ему бесконечность.
        // Остальные страницы прокручиваются, как привыкли.
        PageScroll.VerticalScrollBarVisibility = token == "console"
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;

        PageContent.Content = content;
        if (token == _pageToken) return;
        _pageToken = token;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var story = new Storyboard();
        // Один TranslateTransform на заголовок и контент: страница въезжает как целое,
        // а не двумя слоями с разным сдвигом.
        var slide = new TranslateTransform(12, 0);

        foreach (UIElement part in new UIElement[] { PageHeader, PageContent })
        {
            part.RenderTransform = slide;

            var fade = new DoubleAnimation(0, 1, PageTransition) { EasingFunction = ease };
            Storyboard.SetTarget(fade, part);
            Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));

            story.Children.Add(fade);
        }

        var move = new DoubleAnimation(12, 0, PageTransition) { EasingFunction = ease };
        Storyboard.SetTarget(move, slide);
        Storyboard.SetTargetProperty(move, new PropertyPath(TranslateTransform.XProperty));
        story.Children.Add(move);

        story.Begin();
    }

    private void SetActiveNav(string page)
    {
        _editing = false;

        // Любая страница забирает содержимое PageContent себе, поэтому ссылки на элементы консоли
        // обнуляем здесь: ShowConsole вызывает этот метод первым и только потом подписывается.
        // Сам журнал не теряется — он лежит в ServerInstance.
        DetachConsole();

        var navButtons = new Dictionary<string, Button>
        {
            ["Home"] = NavHome,
            ["Library"] = NavLibrary,
            ["Catalog"] = NavCatalog,
            ["Downloads"] = NavDownloads,
            ["Files"] = NavFiles,
            ["Metrics"] = NavMetrics,
            ["Settings"] = NavSettings
        };

        foreach (var item in navButtons)
        {
            bool active = item.Key == page;
            // Активный пункт выделяется самим акцентом, как в макете, а не приглушённой подложкой:
            // иначе выбранный цвет почти не видно и переключение акцента кажется «никаким».
            item.Value.Background = active
                ? (Brush)FindResource("Accent")
                : Brushes.Transparent;
            item.Value.BorderBrush = active
                ? (Brush)FindResource("AccentLine")
                : Brushes.Transparent;
            item.Value.BorderThickness = new Thickness(1);
            item.Value.Foreground = active
                ? (Brush)FindResource("OnAccent")
                : TileForeground;
        }
    }

    // Плитки: одинаковые ячейки, одинаковые промежутки, край сетки совпадает с краем страницы.
    private UniformGrid TileGrid(double minTileWidth, double bottomSpacing = 0)
    {
        _tileMinWidth = minTileWidth;
        int columns = ColumnsFor(minTileWidth);
        _lastColumns = columns;
        return new UniformGrid
        {
            Columns = columns,
            Margin = new Thickness(0, 0, -TileGap, bottomSpacing - TileGap)
        };
    }

    private int ColumnsFor(double minTileWidth)
    {
        if (_viewportWidth <= 0) return 2;
        return Math.Max(1, (int)Math.Floor((_viewportWidth + TileGap) / (minTileWidth + TileGap)));
    }

    private void PageRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        if (Math.Abs(width - _viewportWidth) < 1) return;
        _viewportWidth = width;
        if (_reflowing || _editing) return;
        if (ColumnsFor(_tileMinWidth) == _lastColumns) return;

        _reflowing = true;
        try
        {
            switch (currentPage)
            {
                case "home": ShowHome(); break;
                case "library": ShowLibrary(GlobalSearch.Text); break;
                case "catalog": ShowCatalog(); break;
                case "metrics": ShowMetrics(); break;
            }
        }
        finally
        {
            _reflowing = false;
        }
    }

    private Border Box(UIElement child, Thickness margin, double minHeight = 0) =>
        new()
        {
            Background = (Brush)FindResource("Panel"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(18),
            Margin = margin,
            MinHeight = minHeight,
            Child = child
        };

    private Border Card(UIElement child, Thickness? margin = null) =>
        Box(child, margin ?? new Thickness(0, 0, 0, 12));

    private Border Tile(UIElement child, double minHeight = 0) =>
        Box(child, new Thickness(0, 0, TileGap, TileGap), minHeight);

    private TextBlock Txt(string text, double size = 13, bool bold = false, Brush? brush = null) =>
        new()
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = brush ?? TileForeground,
            TextWrapping = TextWrapping.Wrap
        };

    // Серая пояснительная строка — самый частый текст в панели, и он везде одного цвета.
    private TextBlock MutedLine(string text, double size = 12) => Txt(text, size, false, (Brush)FindResource("Muted"));

    private TextBlock WarnLine(string text) => Txt(text, 12, false, (Brush)FindResource("Warn"));

    // «1 раз», «2 раза», «5 раз»: счётчик повторов в карточке разбора без этого читается как
    // ошибка, а показывать его надо — три строки про один порт это одна причина, а не три.
    private static string TimesWord(int n)
    {
        int last = n % 100;
        if (last is >= 11 and <= 14) return "раз";
        return (n % 10) switch { 1 => "раз", 2 or 3 or 4 => "раза", _ => "раз" };
    }

    // Длинное название не растягивает плитку: одна строка с многоточием.
    private TextBlock TileTitle(string text) =>
        new()
        {
            Text = text,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = TileForeground,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

    // Описание занимает ровно две строки. LineHeight задан явно, потому что MaxHeight без него
    // обрезал вторую строку по центру букв: высота строки у шрифта больше самого кегля.
    private TextBlock TileText(string text) =>
        new()
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 32
        };

    // Небольшая плашка внутри плитки: версия, статус сборки.
    private Border ChipBox(string text, Brush foreground, Brush background) =>
        new()
        {
            Background = background,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 3, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = foreground,
                TextWrapping = TextWrapping.NoWrap
            }
        };

    private UniformGrid TileActions(params Button[] buttons)
    {
        var grid = new UniformGrid
        {
            Columns = buttons.Length == 1 ? 1 : 2,
            Margin = new Thickness(0, 8, -8, -8)
        };
        foreach (var b in buttons)
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            grid.Children.Add(b);
        }
        return grid;
    }

    private Button Btn(string text, RoutedEventHandler click, bool accent = false)
    {
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 8, 0),
            Background = accent ? (Brush)FindResource("Accent") : (Brush)FindResource("Panel2"),
            BorderBrush = accent ? (Brush)FindResource("Accent") : (Brush)FindResource("Line"),
            Padding = new Thickness(13, 9, 13, 9),
            MinWidth = 80
        };
        b.Click += click;
        return b;
    }

    // Фильтр по ядру над сеткой библиотеки.
    private Button Chip(string label, string? value)
    {
        bool active = _libraryChip == value;
        var b = new Button
        {
            Content = label,
            MinWidth = 0,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Background = active ? (Brush)FindResource("Pressed") : (Brush)FindResource("Panel2"),
            BorderBrush = active ? (Brush)FindResource("AccentLine") : (Brush)FindResource("Line")
        };
        b.Click += (_, _) =>
        {
            _libraryChip = value;
            ShowLibrary(GlobalSearch.Text);
        };
        return b;
    }

    // Чип-переключатель: выглядит как фильтр, но клик меняет состояние настройки, а не выборку.
    private Button ToggleChip(string label, bool on, Action onClick)
    {
        var b = new Button
        {
            Content = (on ? "✓  " : "○  ") + label,
            MinWidth = 0,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Background = on ? (Brush)FindResource("Pressed") : (Brush)FindResource("Panel2"),
            BorderBrush = on ? (Brush)FindResource("AccentLine") : (Brush)FindResource("Line")
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // Строка состояния: читаем фактические файлы в папке сборки, предположений не делаем.
    private DockPanel StatusLine(ServerProfile p)
    {
        var state = BuildChecker.QuickState(p, dataDir);
        bool ok = state == BuildState.Ready;

        var icon = new TextBlock
        {
            Text = ok ? "✓" : "!",
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = ok ? OkBrush : WarnBrush,
            Margin = new Thickness(0, 0, 7, 0)
        };

        // DockPanel, а не горизонтальный StackPanel: только в нём текст ограничен шириной плитки
        // и обрезается многоточием вместо того, чтобы вылезать за карточку.
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock
        {
            Text = BuildChecker.StateLabel(state),
            FontSize = 12,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return row;
    }

    // Выпадающее меню плитки. Popup лежит в том же Grid, что и кнопка, чтобы он оказался в визуальном дереве.
    private FrameworkElement MoreMenu(params MenuEntry[] entries)
    {
        var host = new Grid { Width = 30, Height = 28 };

        var button = new Button
        {
            Content = "⋯",
            MinWidth = 0,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("Muted")
        };

        var list = new StackPanel();
        var border = new Border
        {
            Background = (Brush)FindResource("Panel2"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Width = 190,
            Padding = new Thickness(5)
        };

        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            HorizontalOffset = -160,
            VerticalOffset = 6,
            Child = border
        };

        foreach (var entry in entries)
        {
            var item = new Button
            {
                Content = entry.Label,
                MinWidth = 0,
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 2),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            item.Click += (_, _) =>
            {
                popup.IsOpen = false;
                entry.Run();
            };
            list.Children.Add(item);
        }

        border.Child = list;
        host.Children.Add(button);
        host.Children.Add(popup);
        button.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
        return host;
    }

    private StackPanel Stack(params UIElement[] children)
    {
        var p = new StackPanel();
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    private void ShowHome()
    {
        SetActiveNav("Home");
        currentPage = "home";
        // Ник берём из настроек. Если он не задан, обращаемся по «ты», но не выдумываем имя.
        string nick = settings.AccountName.Trim();
        SetHeader("С ВОЗВРАЩЕНИЕМ", nick.Length > 0 ? $"Добро пожаловать, {nick}" : "Добро пожаловать",
            "Управляйте игровыми серверами и конфигурациями в одном месте.");

        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(Btn("＋ Новая конфигурация", (_, _) => EditProfile(null), true));
        actions.Children.Add(Btn("Открыть библиотеку  →", (_, _) => ShowLibrary()));
        actions.Children.Add(Btn("Открыть каталог  →", (_, _) => ShowCatalog()));

        var hero = new StackPanel();
        hero.Children.Add(Txt("FORGEPANEL", 13, true, (Brush)FindResource("Accent")));
        hero.Children.Add(Txt("Серверы · каталог ядер · настройки и метрики", 13, false, (Brush)FindResource("Muted")));
        hero.Children.Add(actions);

        var (total, free) = SystemInfo.Memory();
        var metrics = TileGrid(300, 18);
        metrics.Children.Add(Tile(Stack(Txt("КОНФИГУРАЦИИ", 10, true, (Brush)FindResource("Muted")), Txt(profiles.Count.ToString(), 28, true)), 104));
        metrics.Children.Add(Tile(Stack(Txt("ОЗУ СВОБОДНО", 10, true, (Brush)FindResource("Muted")), Txt(SystemInfo.FormatGb(free), 24, true), Txt($"из {SystemInfo.FormatGb(total)}", 11, false, (Brush)FindResource("Muted"))), 104));
        metrics.Children.Add(Tile(Stack(Txt("ПЛАТФОРМЫ", 10, true, (Brush)FindResource("Muted")), Txt("Java / Bedrock", 20, true)), 104));

        var recent = new StackPanel();
        recent.Children.Add(Txt("Ваши конфигурации", 20, true));
        recent.Children.Add(Txt("Быстрый доступ к настройкам серверов", 12, false, (Brush)FindResource("Muted")));

        foreach (var p in profiles.Take(4))
        {
            var row = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(Btn("Настроить", (_, _) => EditProfile(p)));
            buttons.Children.Add(Btn("Проверить", (_, _) => ShowCheckResults(p)));
            buttons.Children.Add(Btn("Папка", (_, _) => OpenFolder(p)));
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(Stack(
                Txt(p.Name, 17, true),
                Txt($"{p.Edition} · {p.Core} · Minecraft {p.Version} · порт {p.Port}", 12, false, (Brush)FindResource("Muted")),
                StatusLine(p)));
            recent.Children.Add(Card(row, new Thickness(0, 12, 0, 0)));
        }

        SetPage(Stack(Card(hero), metrics, Card(recent, new Thickness(0, 0, 0, 12))), "home");
    }

    private void ShowLibrary(string? filter = null)
    {
        SetActiveNav("Library");
        currentPage = "library";
        SetHeader("ВАШИ МИРЫ", "Библиотека", $"{profiles.Count} конфигураций · всё в одном месте");

        var root = new StackPanel();
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 15) };
        actions.Children.Add(Btn("＋ Создать конфигурацию", (_, _) => EditProfile(null), true));
        actions.Children.Add(Btn("Модпак Modrinth", (_, _) => InstallModpack()));
        actions.Children.Add(Btn("Модпак CurseForge", (_, _) => InstallCursePack()));
        actions.Children.Add(Btn("Импорт JSON", (_, _) => ImportProfile()));
        actions.Children.Add(Btn("Экспорт JSON", (_, _) => ExportProfiles()));
        root.Children.Add(actions);

        // Чипы фильтра: только те ядра, которые реально используются в сборках.
        var used = profiles.Select(p => p.Core).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        var ordered = CoresFor("Java").Concat(CoresFor("Bedrock")).Distinct().Where(used.Contains)
            .Concat(used.Except(CoresFor("Java").Concat(CoresFor("Bedrock"))))
            .ToList();

        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
        chips.Children.Add(Chip("Все", null));
        foreach (var core in ordered) chips.Children.Add(Chip(core, core));
        chips.Children.Add(ToggleChip("Списком", libraryView.AsList, () => SwitchLibraryView(asList: !libraryView.AsList)));
        chips.Children.Add(ToggleChip("Избранное", libraryView.OnlyFavorites, () => SwitchLibraryView(favorites: !libraryView.OnlyFavorites)));
        root.Children.Add(chips);

        var visible = profiles
            .Where(p => _libraryChip == null || p.Core == _libraryChip)
            .Where(p => !libraryView.OnlyFavorites || p.Favorite)
            .Where(p => string.IsNullOrWhiteSpace(filter) || $"{p.Name} {p.Core} {p.Version}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (visible.Count == 0)
        {
            // Пустое избранное — не «ничего не найдено»: человек фильтром просит собрать нужное,
            // и ему надо сказать, где ставится звёздочка, а не предлагать менять запрос.
            root.Children.Add(Card(Stack(
                Txt(libraryView.OnlyFavorites ? "В избранном пока пусто" : "Ничего не найдено", 20, true),
                Txt(libraryView.OnlyFavorites
                    ? "Отметь сборку звёздочкой слева в списке или пунктом «В избранное» в меню плитки — и она останется здесь."
                    : "Измени запрос в поиске, сбрось фильтр по ядру или создай новую конфигурацию.",
                    13, false, (Brush)FindResource("Muted"))), new Thickness(0)));
        }
        else if (libraryView.AsList)
        {
            var list = new StackPanel();
            foreach (var p in visible) list.Children.Add(ServerRow(p));
            root.Children.Add(list);
        }
        else
        {
            var grid = TileGrid(320);
            foreach (var p in visible) grid.Children.Add(ServerTile(p));
            root.Children.Add(grid);
        }

        SetPage(root, "library");
    }

    // Переключение вида библиотеки. Пишем файл сразу, а не при закрытии панели: вид должен
    // пережить и падение, и Alt+F4 без вопроса.
    private void SwitchLibraryView(bool? asList = null, bool? favorites = null)
    {
        if (asList != null) libraryView.AsList = asList.Value;
        if (favorites != null) libraryView.OnlyFavorites = favorites.Value;
        libraryView.Save(dataDir);
        ShowLibrary(GlobalSearch.Text);
    }

    // Меню сборки: общий для плитки и для строчки списка, чтобы второй режим не оказался
    // урезанной версией первого.
    private MenuEntry[] MenuFor(ServerProfile p)
    {
        var entries = new List<MenuEntry>
        {
            new("Настроить", () => EditProfile(p)),
            new(p.Favorite ? "Убрать из избранного" : "В избранное", () => ToggleFavorite(p)),
            new("Установить ядро", () => InstallCore(p)),
            new("Принять EULA", () => AcceptEula(p)),
            new("Открыть консоль", () => ShowConsole(p)),
            new("Проверить сборку", () => ShowCheckResults(p)),
            new("Как позвать друга", () => ShowInviteHelp(p)),
            new("Игроки и права", () => ShowPlayers(p)),
            new("Резервные копии мира", () => ShowBackups(p)),
            new("Обновления модов", () => ShowModUpdates(p)),
            new("Открыть папку", () => OpenFolder(p)),
            new("Дублировать", () => Duplicate(p)),
            new("Удалить", () => DeleteProfile(p))
        };

        // Одна кнопка на закрытие Энда — но только там, где её есть чем закрыть: у ядер без
        // bukkit.yml пункт не показываем вместо обещания, которое не выполняется.
        string folder = BuildChecker.ResolveFolder(p, dataDir);
        if (BukkitYaml.Supports(p.Core) && Directory.Exists(folder))
            entries.Insert(5, new MenuEntry(
                BukkitYaml.ReadEnd(folder) == false ? "Открыть Энд" : "Закрыть Энд",
                () => ToggleEnd(p)));

        return entries.ToArray();
    }

    private void ToggleFavorite(ServerProfile p)
    {
        p.Favorite = !p.Favorite;
        SaveProfiles();
        if (currentPage == "library") ShowLibrary(GlobalSearch.Text);
    }

    // Главное действие сборки меняется вместе с её состоянием: сначала ядро надо скачать,
    // потом принять EULA, потом запустить, а пока сервер работает — вернуться к его консоли.
    // Одна функция на плитку и на строку списка, чтобы режимы не разъезжались в поведении.
    private Button PrimaryAction(ServerProfile p, BuildState state) =>
        Running(p) != null
            ? Btn("Открыть консоль", (_, _) => ShowConsole(p), true)
            : state switch
            {
                BuildState.NotInstalled => Btn("Установить ядро", (_, _) => InstallCore(p), true),
                BuildState.NeedsAttention => Btn("Принять EULA", (_, _) => AcceptEula(p), true),
                _ => Btn("▶ Играть", (_, _) => StartServer(p), true)
            };

    // Плитка сборки: полоса обложки с бейджем, чипы версии и состояния, кнопки действий в свёрнутом меню.
    private Border ServerTile(ServerProfile p)
    {
        bool bedrock = p.Edition == "Bedrock";
        var state = BuildChecker.QuickState(p, dataDir);

        // Две строки: сверху обложка и текст, кнопка прижата к низу плитки. Без этой сетки
        // кнопка висела бы сразу под подписями, а под ней оставалась бы пустая полоса.
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var top = new StackPanel();
        Grid.SetRow(top, 0);

        var cover = new Grid
        {
            Height = 92,
            Margin = new Thickness(-18, -18, -18, 14)
        };
        cover.Children.Add(new Border
        {
            Background = CoverBrush(bedrock),
            CornerRadius = new CornerRadius(13, 13, 0, 0)
        });

        var badgeCell = new Grid
        {
            Width = 46,
            Height = 46,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        badgeCell.Children.Add(new Border
        {
            Background = BadgeBrush(bedrock),
            CornerRadius = new CornerRadius(13),
            // Без центра поворота ромб уехал бы влево-вверх от текста.
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(45)
        });
        badgeCell.Children.Add(new TextBlock
        {
            Text = BadgeInitials(p.Name, bedrock),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("AccentText"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        cover.Children.Add(badgeCell);

        var version = ChipBox(p.Version, (Brush)FindResource("AccentText"), (Brush)FindResource("Hover"));
        version.Margin = new Thickness(12, 0, 0, 10);
        cover.Children.Add(version);

        var status = ChipBox(StateBadge(state), state switch
        {
            BuildState.Ready => OkBrush,
            BuildState.NeedsAttention => WarnBrush,
            _ => (Brush)FindResource("Muted")
        }, (Brush)FindResource("Hover"));
        status.HorizontalAlignment = HorizontalAlignment.Right;
        status.VerticalAlignment = VerticalAlignment.Top;
        status.Margin = new Thickness(0, 10, 12, 0);
        cover.Children.Add(status);

        top.Children.Add(cover);

        var titleRow = new DockPanel();
        var menu = MoreMenu(MenuFor(p));
        DockPanel.SetDock(menu, Dock.Right);
        titleRow.Children.Add(menu);
        if (p.Favorite)
        {
            var star = StarMark(true);
            DockPanel.SetDock(star, Dock.Right);
            titleRow.Children.Add(star);
        }
        titleRow.Children.Add(TileTitle(p.Name));
        top.Children.Add(titleRow);

        top.Children.Add(Txt($"{p.Edition} · {p.Core}", 12, false, (Brush)FindResource("Muted")));
        top.Children.Add(Txt($"Порт {p.Port} · {p.MemoryGb} ГБ ОЗУ", 12, false, (Brush)FindResource("Muted")));
        // Пустые поля настроек означают «не трогать server.properties», поэтому на плитке
        // показываем именно это, а не выдуманное значение по умолчанию.
        string players = p.MaxPlayers == null ? "сколько в файле" : $"до {p.MaxPlayers}";
        string entry = p.OnlineMode == null ? "как в файле" : (p.OnlineMode.Value ? "только лицензия" : "без лицензии");
        string drawn = p.ViewDistance == null ? "" : $" · прорисовка {p.ViewDistance}";
        top.Children.Add(Txt($"Игроки: {players} · вход {entry}{drawn}", 12, false, (Brush)FindResource("Muted")));
        top.Children.Add(StatusLine(p));

        // Главное действие плитки меняется вместе с состоянием сборки: сначала ядро надо скачать,
        // потом принять EULA, потом запустить, а пока сервер работает — вернуться к его консоли.
        Button action = PrimaryAction(p, state);
        action.Margin = new Thickness(0, 12, 0, 0);
        action.MinHeight = 38;
        action.HorizontalAlignment = HorizontalAlignment.Stretch;
        action.MinWidth = 0;
        action.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetRow(action, 1);

        body.Children.Add(top);
        body.Children.Add(action);

        return Tile(body, 300);
    }

    // Звезда избранного. Глиф берём из Segoe UI Symbol: стандартный шрифт интерфейса рисует
    // вместо него квадрат, а цветной emoji WPF всё равно отдал бы монохромно.
    private TextBlock StarMark(bool favorite) => new()
    {
        Text = favorite ? "★" : "☆",
        FontFamily = new FontFamily("Segoe UI Symbol"),
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Foreground = favorite ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0)
    };

    // Строка списка: те же данные, что и на плитке, но в одну линию. Режим существует ровно
    // потому, что при двадцати сборках плитки не влезают в экран, а списком видно все сразу.
    private Border ServerRow(ServerProfile p)
    {
        bool bedrock = p.Edition == "Bedrock";
        var state = BuildChecker.QuickState(p, dataDir);

        var row = new DockPanel { LastChildFill = true };

        var star = new Button
        {
            Content = StarMark(p.Favorite),
            MinWidth = 0,
            Padding = new Thickness(2, 0, 2, 0),
            Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = p.Favorite ? "Убрать из избранного" : "Добавить в избранное"
        };
        star.Click += (_, _) => ToggleFavorite(p);
        DockPanel.SetDock(star, Dock.Left);
        row.Children.Add(star);

        var mark = new Grid { Width = 34, Height = 34, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(mark, Dock.Left);
        mark.Children.Add(new Border
        {
            Background = BadgeBrush(bedrock),
            CornerRadius = new CornerRadius(9),
            Child = new TextBlock
            {
                Text = BadgeInitials(p.Name, bedrock),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("AccentText"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        row.Children.Add(mark);

        var menu = MoreMenu(MenuFor(p));
        DockPanel.SetDock(menu, Dock.Right);
        row.Children.Add(menu);

        Button action = PrimaryAction(p, state);
        action.MinWidth = 150;
        action.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(action, Dock.Right);
        row.Children.Add(action);

        var chip = ChipBox(StateBadge(state), state switch
        {
            BuildState.Ready => OkBrush,
            BuildState.NeedsAttention => WarnBrush,
            _ => (Brush)FindResource("Muted")
        }, (Brush)FindResource("Hover"));
        chip.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(chip, Dock.Right);
        row.Children.Add(chip);

        // Последним идёт то, что занимает остаток ширины: название и короткая строка данных.
        // Краткость здесь важнее полноты — в списке как раз и смотрят на сборки разом.
        row.Children.Add(Stack(
            new TextBlock
            {
                Text = p.Name,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = TileForeground,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            },
            MutedLine($"{p.Edition} · {p.Core} {p.Version} · порт {p.Port} · {p.MemoryGb} ГБ")));

        return new Border
        {
            Background = (Brush)FindResource("Panel"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 8),
            Child = row
        };
    }

    // Ромбик на обложке: первые два знака названия сборки. «MC»/«BD» оставлены как заглушка —
    // у конфигурации без названия показывать букву не от чего.
    private static string BadgeInitials(string name, bool bedrock)
    {
        var picked = new List<char>(2);
        foreach (char ch in (name ?? "").ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(ch)) picked.Add(ch);
            if (picked.Count == 2) break;
        }

        if (picked.Count == 0) return bedrock ? "BD" : "MC";
        return new string(picked.ToArray());
    }

    private static string StateBadge(BuildState state) => state switch
    {
        BuildState.Ready => "Готов",
        BuildState.NeedsAttention => "EULA не принята",
        _ => "Не установлен"
    };

    // Подробный отчёт проверки: читаем файлы сборки, ничего не рисуем «для красоты».
    private void ShowCheckResults(ServerProfile p)
    {
        var items = BuildChecker.Check(p, dataDir);
        int problems = items.Count(i => !i.Ok);
        string folder = BuildChecker.ResolveFolder(p, dataDir);

        var win = new Window
        {
            Title = $"Проверка сборки — {p.Name}",
            Width = 620,
            Height = 560,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("Bg"),
            Foreground = TileForeground
        };
        Dialog.DarkCaption(win);

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        head.Children.Add(Txt($"Проверка сборки «{p.Name}»", 18, true));
        head.Children.Add(Txt(problems == 0
            ? "Замечаний нет: файлы, Java и место соответствуют конфигурации."
            : $"Найдено замечаний: {problems}. Раздел «Файлы» покажет папку сборки.", 12, false,
            problems == 0 ? OkBrush : WarnBrush));

        var list = new StackPanel();
        foreach (var item in items)
        {
            var icon = new TextBlock
            {
                Text = item.Ok ? "✓" : "!",
                Width = 22,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = item.Ok ? OkBrush : WarnBrush,
                VerticalAlignment = VerticalAlignment.Top
            };
            var text = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
            text.Children.Add(Txt(item.Title, 13, true));
            text.Children.Add(Txt(item.Detail, 12, false, (Brush)FindResource("Muted")));

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(text);
            list.Children.Add(row);
        }

        var bottom = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(Btn("Открыть папку сборки", (_, _) => OpenUrl(folder), true));
        bottom.Children.Add(Btn("Закрыть", (_, _) => win.Close()));

        var content = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        content.Children.Add(head);
        content.Children.Add(bottom);
        content.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        win.Content = content;
        win.ShowDialog();
    }

    // Карточка «Как позвать друга»: локальные данные — адреса этой машины, порт из server.properties
    // и то, слушается ли он. Сама карточка наружу не ходит: единственный запрос вовне делает кнопка
    // проверки CGNAT, только по нажатию и только «какой мой адрес».
    private void ShowInviteHelp(ServerProfile p)
    {
        string folder = BuildChecker.ResolveFolder(p, dataDir);
        bool hasProperties = File.Exists(Path.Combine(folder, "server.properties"));

        // Порт сервер читает из своего файла, а не из настроек панели: если файл уже создан,
        // конфигурация допишет его только при следующем запуске.
        string filePort = hasProperties ? ServerProperties.Read(folder, "server-port", "") : "";
        bool filePortKnown = int.TryParse(filePort, out int parsed) && parsed > 0;
        int port = filePortKnown ? parsed : p.Port;

        var win = new Window
        {
            Title = $"Как позвать друга — {p.Name}",
            Width = 700,
            Height = 760,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("Bg"),
            Foreground = TileForeground
        };
        Dialog.DarkCaption(win);

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(Txt($"Как позвать друга на «{p.Name}»", 18, true));
        head.Children.Add(Txt(
            "Здесь только то, что панель прочитала на этом компьютере: адреса сетевых адаптеров, порт и " +
            "файлы сборки. Сама карточка ничего не отправляет наружу и ни один файл не меняет — наружу " +
            "ходит только кнопка проверки CGNAT, и то после нажатия.", 12, false,
            (Brush)FindResource("Muted")));

        var body = new StackPanel();

        void Label(string text)
        {
            var t = Txt(text, 12, true, (Brush)FindResource("Muted"));
            t.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(t);
        }

        void Line(string text, Brush? brush = null)
        {
            var t = Txt(text, 12, false, brush ?? (Brush)FindResource("Muted"));
            t.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(t);
        }

        Label("Адрес для подключения");
        var addresses = LocalIPv4();
        if (addresses.Count == 0)
            Line("Активных адаптеров с IPv4 не найдено — похоже, сеть выключена. Другу нужен адрес машины, " +
                 "а не её имя: имя ищется только через DNS той же сети.", WarnBrush);
        foreach ((string address, string note) in addresses) body.Children.Add(AddressRow($"{address}:{port}", note));

        Label("Порт");
        Line(filePortKnown
            ? $"Берём {port} из server.properties" +
              (parsed == p.Port ? " — он совпадает с конфигурацией панели." :
               $"; в конфигурации панели {p.Port}, и запуск вернёт файл к нему при следующем старте.")
            : hasProperties
                ? "В server.properties порт не записан или не читается" +
                  (filePort.Length > 0 ? $" («{filePort}»)" : "") +
                  $": панель подставит {p.Port} при следующем запуске."
                : $"server.properties ещё не создан — порт {p.Port} из конфигурации попадёт в файл при первом запуске.");

        ServerInstance? running = Running(p);
        Line(running != null
            ? $"Сервер запущен панелью: порт {port} слушается, друг сможет зайти прямо сейчас."
            : PortFree(port)
                ? $"Порт {port} сейчас свободен — сервер не запущен, и подключиться к нему нельзя."
                : $"Порт {port} занят другой программой (или сервером вне панели): проверь, кто именно на нём стоит.",
            running != null ? OkBrush : WarnBrush);

        if (BuildChecker.IsBedrock(p))
            Line("Это Bedrock-сборка: у неё другой протокол (UDP 19132) и свой способ запуска, а панель сейчас " +
                 "поднимает только Java. Подсказки ниже — про Java-сервер.", WarnBrush);

        string bound = hasProperties ? ServerProperties.Read(folder, "server-ip", "") : "";
        if (bound.Length > 0)
            Line($"В server.properties прописан server-ip={bound}: сервер слушает только этот адрес, и с другого " +
                 "адаптера к нему не попасть. Обычно его оставляют пустым.", WarnBrush);

        string online = hasProperties ? ServerProperties.Read(folder, "online-mode", "") : "";
        if (online == "false")
            Line("online-mode=false: друг зайдёт без лицензии и с любым ником. Удобно, но белый список по нику " +
                 "перестаёт быть защитой — кто угодно может взять этот ник.");
        else if (online == "true")
            Line("online-mode=true: другу нужен лицензионный аккаунт Minecraft с тем ником, который ты внесёшь " +
                 "в белый список.");

        body.Children.Add(HintCard("Вариант 1 · вы в одной сети (дом, офис, Wi-Fi)",
            "Друг вводит адрес из списка выше — тот, что относится к Wi-Fi или Ethernet, обычно 192.168.x.x.",
            "Сервер должен быть запущен, а брандмауэр Windows — пускать входящие на этот порт (команда ниже).",
            "Из интернета по такому адресу не войти: 192.168.x.x действует только внутри вашей сети."));

        body.Children.Add(HintCard("Вариант 2 · проброс порта на роутере",
            "На роутере добавь правило: TCP-порт " + port + " → внутренний IP этого компьютера. Зафиксируй за " +
            "компьютером адрес в DHCP, иначе после перезагрузки роутера правило укажет не туда.",
            "Друг заходит по твоему публичному IP и тому же порту. Проверить, есть ли у тебя вообще " +
            "публичный адрес, — кнопка ниже.",
            "Не сработает, если провайдер даёт CGNAT: публичного адреса у тебя просто нет, и проброс бессилен. " +
            "Тот же случай, когда адрес в роутере отличается от «моего IP» на любом сайте-определителе.",
            "Публичный IP у провайдера обычно меняется: либо DDNS, либо каждый раз новый адрес."));

        // Единственный запрос наружу во всей карточке — и только по нажатию. Ответ касается одного
        // вопроса: стоит ли связываться с пробросом. 100.64.0.0/10 (RFC 6598) провайдеры отдают
        // именно под CGNAT, поэтому попасть в этот диапазон — уже диагноз, а не догадка.
        Label("Проверка перед пробросом · есть ли у тебя публичный адрес");
        Line("Кнопка отправляет один запрос «какой мой адрес» сервису определения IP и ничего больше: ни " +
             "имени сборки, ни порта, ни путей в нём нет. Без нажатия панель наружу не ходит вовсе.");

        var cgnatCts = new CancellationTokenSource();
        win.Closed += (_, _) => cgnatCts.Cancel();

        var cgnatResult = Txt("", 12, false, (Brush)FindResource("Muted"));
        cgnatResult.Margin = new Thickness(0, 6, 0, 0);

        var cgnatRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        cgnatRow.Children.Add(Btn("Проверить публичный адрес", async (object sender, RoutedEventArgs _) =>
        {
            Button me = (Button)sender;
            me.IsEnabled = false;
            cgnatResult.Foreground = (Brush)FindResource("Muted");
            cgnatResult.Text = "Спрашиваю у сервиса, какой адрес видит интернет…";

            try
            {
                CgnatProbeResult answer = await CgnatProbe.RunAsync(cgnatCts.Token);
                if (cgnatCts.IsCancellationRequested) return;

                if (!answer.Ok)
                {
                    cgnatResult.Foreground = WarnBrush;
                    cgnatResult.Text = "Адрес получить не удалось: " + answer.Error + " Это ни о чём плохом " +
                        "не говорит — скорее всего, сети просто не было. Проверить то же самое можно руками: " +
                        "сравни адрес WAN в роутере с «моим IP» на любом сайте-определителе.";
                    return;
                }

                if (IsCgnatRange(answer.Address))
                {
                    cgnatResult.Foreground = WarnBrush;
                    cgnatResult.Text = $"{answer.Address} от {answer.Host} — диапазон 100.64.0.0/10, общий адрес " +
                        "провайдера (CGNAT). Своего публичного адреса нет, и проброс порта (вариант 2) упрётся в " +
                        "чужой роутер. Работает вариант 3 — виртуальная сеть, или вариант 4 — туннель.";
                    return;
                }

                if (IsPrivateAddress(answer.Address))
                {
                    cgnatResult.Foreground = WarnBrush;
                    cgnatResult.Text = $"{answer.Address} от {answer.Host} — сервис вернул частный адрес вместо " +
                        "публичного: ответы подменены (блокировщик или hosts-файл). Вывода из этого сделать " +
                        "нельзя, и панель его не выдаёт.";
                    return;
                }

                cgnatResult.Foreground = OkBrush;
                cgnatResult.Text = LocalIPv4().Any(row => row.Address == answer.Address)
                    ? $"{answer.Address} от {answer.Host} — тот же адрес стоит на адаптере этой машины, значит " +
                      "NAT между тобой и интернетом нет: адрес настоящий, друг дойдёт прямо по нему."
                    : $"{answer.Address} от {answer.Host} — обычный публичный адрес, не CGNAT: проброс порта " +
                      "(вариант 2) имеет смысл. Учти, что при переподключении провайдер обычно выдаёт другой " +
                      "адрес, — тогда нужен DDNS.";
            }
            finally
            {
                me.IsEnabled = true;
            }
        }, true));
        body.Children.Add(cgnatRow);
        body.Children.Add(cgnatResult);

        body.Children.Add(HintCard("Вариант 3 · виртуальная сеть (Tailscale, ZeroTier, Radmin VPN)",
            "Оба ставите клиент и входите в одну сеть. Роутер и провайдер не нужны, CGNAT не мешает — самый " +
            "быстрый способ, когда доступа к роутеру нет.",
            "Друг заходит по адресу виртуального адаптера из списка выше и порту " + port + ".",
            "Минус: клиент нужен и у друга, а соединения координирует сторонний сервис."));

        body.Children.Add(HintCard("Вариант 4 · туннель-сервис (playit.gg, ngrok, Cloudflare Tunnel)",
            "На этой машине запускается программа-туннель, сама соединяется с сервисом, и друг подключается к " +
            "выданному адресу. Роутер не трогаем совсем.",
            "Для Minecraft удобнее всего playit.gg — он под игру и заточен; ngrok на бесплатном тарифе ограничен " +
            "по TCP, Cloudflare Tunnel требует ручной настройки.",
            "Трафик идёт через чужую машину: сервис видит соединения, добавляет задержку и может сменить адрес."));

        body.Children.Add(HintCard("Если друг на телефоне или консоли (Bedrock)",
            "Java-сервер его не пустит — протоколы разные. Нужен мост Geyser: плагин на Paper или Folia либо мод " +
            "на Fabric, плюс проброс UDP 19132.",
            "Geyser ставится сверху на уже собранную Java-сборку, это отдельная задача, а не переключатель в панели."));

        string ruleName = "ForgePanel " + p.Name.Replace("\"", "") + " " + port;
        string addRule = $"netsh advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port}";
        body.Children.Add(HintCard("Брандмауэр Windows",
            "Команду запускаешь сам в командной строке от имени администратора — панель права не поднимает и ничего " +
            "за тебя не выполняет.",
            "Отменить правило: netsh advfirewall firewall delete rule name=\"" + ruleName + "\"",
            "Ещё нужно, чтобы само подключение считалось частным: «Параметры → Сеть → это подключение» — иначе " +
            "правило для частной сети не сработает."));
        body.Children.Add(AddressRow(addRule, "Команда для копирования, а не для автоматического запуска."));

        var bottom = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(Btn("Открыть папку сборки", (_, _) => OpenUrl(folder), true));
        bottom.Children.Add(Btn("Закрыть", (_, _) => win.Close()));

        var content = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        content.Children.Add(head);
        content.Children.Add(bottom);
        content.Children.Add(new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        win.Content = content;
        win.ShowDialog();
    }

    // Строка с адресом: поле можно выделить руками, кнопка кладёт в буфер сразу «адрес:порт»,
    // чтобы другу не нужно было дописывать порт самому.
    private StackPanel AddressRow(string text, string note)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontSize = 13,
            MinHeight = 34,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var copy = Btn("Скопировать", (_, _) => CopyToClipboard(text));
        copy.MinWidth = 110;
        DockPanel.SetDock(copy, Dock.Right);

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(copy);
        row.Children.Add(box);

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(row);
        if (note.Length > 0)
            panel.Children.Add(Txt(note, 11, false, (Brush)FindResource("Muted")));
        return panel;
    }

    // Буфер обмена в Windows бывает занят другой программой: тогда не ругаемся, а показываем текст —
    // его можно скопировать из поля вручную.
    private void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Dialog.Info($"Буфер обмена не открылся: {ex.Message}\n\nСкопируй вручную:\n{text}", "Не скопировано");
        }
    }

    // Адаптеры, которые виртуальные по названию или описанию: адрес выглядит сетевым, но по нему
    // зайдёт только тот, кто состоит в этой же виртуальной сети.
    private static readonly string[] VirtualAdapterMarks =
    {
        "tailscale", "zerotier", "radmin", "hamachi", "wtunnel", "tap-windows", "tap adapter", "vpn", "virtual"
    };

    // IPv4-адреса активных адаптеров. 169.254.x.x отбрасываем: это «DHCP не ответил», подключиться
    // по нему нельзя, а показать — значит подложить под руку нерабочий вариант.
    private List<(string Address, string Note)> LocalIPv4()
    {
        var rows = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (UnicastIPAddressInformation ip in nic.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ip.Address)) continue;

                string address = ip.Address.ToString();
                if (address.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                if (!seen.Add(address)) continue;
                rows.Add((address, AdapterNote(nic, address)));
            }
        }

        return rows;
    }

    private static string AdapterNote(NetworkInterface nic, string address)
    {
        string text = nic.Name + " " + nic.Description;
        bool virtualNic = VirtualAdapterMarks.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

        string kind = virtualNic
            ? "виртуальный адаптер — зайдёт только тот, кто в этой же сети"
            : "обычная сеть";
        string scope = IsPrivateAddress(address) ? " · из интернета по нему не попасть" : "";
        string cgnat = IsCgnatRange(address) ? " · диапазон Tailscale или провайдерского CGNAT" : "";
        return $"{nic.Name} · {kind}{scope}{cgnat}";
    }

    // Частные диапазоны по RFC 1918: они действительны только внутри сети, и это главный подвох
    // для человека, который собирается дать другу адрес из списка.
    private static bool IsPrivateAddress(string address)
    {
        string[] parts = address.Split('.');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b)) return false;
        return a == 10 || (a == 192 && b == 168) || (a == 172 && b >= 16 && b <= 31);
    }

    // 100.64.0.0/10 — общий адрес провайдера (CGNAT) и диапазон Tailscale одновременно.
    private static bool IsCgnatRange(string address)
    {
        string[] parts = address.Split('.');
        if (parts.Length != 4) return false;
        return int.TryParse(parts[0], out int a) && int.TryParse(parts[1], out int b)
            && a == 100 && b >= 64 && b <= 127;
    }

    // Окно «Игроки и права»: белый список, операторы и блокировки этой сборки.
    // Пока сервер запущен панелью, каждое изменение уходит командой в консоль: только сам сервер
    // знает uuid ника, проверяет его и перезаписывает файлы. При остановленном сервере панель
    // правит файлы сама, но только удаления и добавление ника, uuid которого уже лежит в другом
    // файле этой сборки. Выдуманный uuid сервер прочитал бы как другого игрока, а запись о бане
    // без серверных дат и причин портит файл — поэтому такого пути нет вовсе.
    private void ShowPlayers(ServerProfile p)
    {
        string folder = BuildChecker.ResolveFolder(p, dataDir);

        if (BuildChecker.IsBedrock(p))
        {
            Dialog.Info("Это Bedrock-сборка: у неё свои файлы (allowlist.json и permissions.json), другой " +
                        "формат записей и свой способ выдавать права. Панель читает и правит пока только " +
                        "Java-сервер, поэтому список правят файлы сборки вручную или команды самого сервера.",
                        "Не Java-сборка");
            return;
        }

        if (!Directory.Exists(folder))
        {
            Dialog.Info($"Папки сборки ещё нет — {folder}. Файлы игроков появятся вместе с сервером: " +
                        "сначала установи ядро и запусти его хотя бы раз.", "Папки нет");
            return;
        }

        var muted = (Brush)FindResource("Muted");

        var win = new Window
        {
            Title = $"Игроки и права — {p.Name}",
            Width = 680,
            Height = 780,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("Bg"),
            Foreground = TileForeground
        };
        Dialog.DarkCaption(win);

        var state = Txt("", 12, false, muted);
        state.Margin = new Thickness(0, 4, 0, 0);
        var action = Txt("Действий пока не было.", 12, false, muted);
        action.Margin = new Thickness(0, 4, 0, 0);

        // Поля ввода живут вне списка: Refresh перестраивает list с нуля, и без этого ник
        // приходилось бы вбивать заново после каждого действия.
        var nick = new TextBox { MinHeight = 36, Margin = new Thickness(0, 6, 0, 7) };
        Placeholder.SetText(nick, "например Steve_2013");
        var ip = new TextBox { MinHeight = 36, Margin = new Thickness(0, 6, 0, 7) };
        Placeholder.SetText(ip, "203.0.113.7 или ник игрока");
        var list = new StackPanel();

        string Nick() => nick.Text.Trim();
        string Ip() => ip.Text.Trim();

        void Say(string text, Brush? brush = null)
        {
            action.Text = text;
            action.Foreground = brush ?? muted;
        }

        void Line(string text, Brush? brush = null)
        {
            var t = Txt(text, 12, false, brush ?? muted);
            t.Margin = new Thickness(0, 4, 0, 0);
            list.Children.Add(t);
        }

        void SectionTitle(string text)
        {
            var t = Txt(text, 12, true, muted);
            t.Margin = new Thickness(0, 18, 0, 2);
            list.Children.Add(t);
        }

        void EntryRow(string main, string secondary, string buttonLabel, Action onClick)
        {
            var button = Btn(buttonLabel, (_, _) => onClick());
            button.MinWidth = 118;
            DockPanel.SetDock(button, Dock.Right);

            var text = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            text.Children.Add(Txt(main, 13, true));
            if (secondary.Length > 0) text.Children.Add(Txt(secondary, 11, false, muted));

            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(button);
            row.Children.Add(text);
            list.Children.Add(row);
        }

        PlayerFile Load(string fileName) => PlayerFile.Load(folder, fileName);

        string Count(PlayerFile file) =>
            !file.Exists ? "файла нет" : file.Error != null ? "не читается" : file.Entries.Count.ToString();

        // false — перечислять нечего: файл не читается или его ещё нет. Пустой список — это норма,
        // и он показывается строкой, чтобы «нет файла» и «файл пуст» не выглядели одинаково.
        bool Note(PlayerFile file, string emptyText)
        {
            if (file.Error != null)
            {
                Line($"Файл {file.FileName} не читается: {file.Error}. Пока его не починишь, панель в него не пишет.", WarnBrush);
                return false;
            }

            if (!file.Exists)
            {
                Line($"Файла {file.FileName} пока нет: сервер создаёт его при первой команде этого рода.", muted);
                return false;
            }

            if (file.Entries.Count == 0) Line(emptyText, muted);
            return true;
        }

        // uuid берём только из файлов этой же сборки — единственный честный источник: запись
        // с неродным uuid сервер прочитает как совершенно другого игрока.
        string? KnownUuid(string name, params PlayerFile[] files)
        {
            foreach (PlayerFile file in files)
            {
                JsonObject? entry = file.FindByName(name);
                string? uuid = entry == null ? null : PlayerFile.UuidOf(entry);
                if (!string.IsNullOrWhiteSpace(uuid)) return uuid;
            }

            return null;
        }

        // Без запущенного сервера нового ника в списке не появится: uuid знает только сервер.
        void NoUuidDialog(string name, string target)
        {
            Dialog.Info($"Нечем добавить «{name}» в {target}: у этого ника нет записи ни в одном файле сборки, " +
                        "а uuid панель придумать не может.\n\nЗапусти сервер и нажми кнопку снова — панель " +
                        "отправит команду в консоль, и сервер внесёт игрока сам, с настоящим uuid.",
                        "Нужен запущенный сервер");
        }

        void SendToServer(string command, string doneText)
        {
            try
            {
                Running(p)!.Send(command);
                Say(doneText + " Сервер применит её сам, файлы обновятся через пару секунд — нажми «Обновить».", OkBrush);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Say($"Сервер команду не принял: {ex.Message}", WarnBrush);
            }
        }

        // Перезапись файла только после подтверждения: человек видит, сколько записей было и сколько
        // станет, вместо «готово» вслепую.
        bool Rewrite(PlayerFile file, IEnumerable<JsonObject> next, string what)
        {
            List<JsonObject> kept = next.ToList();
            int before = file.Entries.Count;

            string preview = $"{file.FileName}\n{file.FullPath}\n\n{what}\n\n" +
                             $"Записей было: {before}, станет: {kept.Count}.\n\nПерезаписать файл?";
            if (!Dialog.Confirm(preview, "Правка файла"))
            {
                Say("Отменено — файл не изменён.", muted);
                return false;
            }

            try
            {
                file.Rewrite(kept);
                Say($"{file.FileName}: было {before}, стало {kept.Count}. Изменения сработают при следующем запуске.", OkBrush);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say($"Записать {file.FileName} не удалось: {ex.Message}", WarnBrush);
                return false;
            }
        }

        void ToggleWhitelist(bool on)
        {
            if (Running(p) != null)
            {
                string command = "whitelist " + (on ? "on" : "off");
                SendToServer(command, $"Отправил команду {command}.");
                return;
            }

            if (!File.Exists(Path.Combine(folder, "server.properties")))
            {
                Dialog.Info("server.properties ещё не создан — сервер напишет его при первом запуске. " +
                            $"Белый список можно включить на запущенном сервере (команда whitelist {(on ? "on" : "off")}) " +
                            "или поставить white-list в файле руками после запуска.", "Файла нет");
                return;
            }

            string current = ServerProperties.Read(folder, "white-list", "");
            string wanted = on ? "true" : "false";
            if (current == wanted)
            {
                Say($"white-list уже {wanted}.", OkBrush);
                return;
            }

            string preview = $"server.properties: white-list = {(current.Length == 0 ? "ключа в файле нет" : current)} → {wanted}\n\n" +
                (on ? "С включённым списком сервер пустит только ники из whitelist.json."
                    : "С выключенным списком сервер пустит любого, кто знает адрес и порт.");
            if (!Dialog.Confirm(preview, "Белый список"))
            {
                Say("Отменено — файл не изменён.", muted);
                return;
            }

            try
            {
                ServerProperties.Set(folder, "white-list", wanted);
                Say($"white-list = {wanted}. Применится при следующем запуске.", OkBrush);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say($"server.properties не записан: {ex.Message}", WarnBrush);
            }

            Refresh();
        }

        void AddWhitelist()
        {
            string name = Nick();
            if (!IsSafeNick(name))
            {
                Say(NickComplaint(name), WarnBrush);
                return;
            }

            if (Running(p) != null)
            {
                SendToServer("whitelist add " + name, $"Отправил команду whitelist add {name}.");
                return;
            }

            PlayerFile whitelist = Load(PlayerFile.Whitelist);
            if (whitelist.Error != null)
            {
                Say($"whitelist.json не читается: {whitelist.Error}", WarnBrush);
                return;
            }

            if (whitelist.FindByName(name) != null)
            {
                Say($"«{name}» уже в белом списке.", OkBrush);
                return;
            }

            string? uuid = KnownUuid(name, Load(PlayerFile.Ops), Load(PlayerFile.BannedPlayers));
            if (uuid == null)
            {
                NoUuidDialog(name, "белый список");
                return;
            }

            var entries = whitelist.Entries.ToList();
            entries.Add(PlayerFile.NewWhitelistEntry(uuid, name));
            if (Rewrite(whitelist, entries, $"Добавляем «{name}» с uuid {uuid} — он взят из другого файла этой сборки."))
                Refresh();
        }

        void AddOp()
        {
            string name = Nick();
            if (!IsSafeNick(name))
            {
                Say(NickComplaint(name), WarnBrush);
                return;
            }

            if (Running(p) != null)
            {
                SendToServer("op " + name, $"Отправил команду op {name}.");
                return;
            }

            PlayerFile ops = Load(PlayerFile.Ops);
            if (ops.Error != null)
            {
                Say($"ops.json не читается: {ops.Error}", WarnBrush);
                return;
            }

            if (ops.FindByName(name) != null)
            {
                Say($"«{name}» уже оператор.", OkBrush);
                return;
            }

            string? uuid = KnownUuid(name, Load(PlayerFile.Whitelist), Load(PlayerFile.BannedPlayers));
            if (uuid == null)
            {
                NoUuidDialog(name, "список операторов");
                return;
            }

            var entries = ops.Entries.ToList();
            entries.Add(PlayerFile.NewOpEntry(uuid, name));
            if (Rewrite(ops, entries, $"Даём «{name}» права оператора (level 4) с uuid {uuid} из другого файла сборки."))
                Refresh();
        }

        void Ban()
        {
            string name = Nick();
            if (!IsSafeNick(name))
            {
                Say(NickComplaint(name), WarnBrush);
                return;
            }

            if (Running(p) != null)
            {
                SendToServer("ban " + name, $"Отправил команду ban {name}.");
                return;
            }

            Dialog.Info("Бан без запущенного сервера панель не делает: в banned-players.json сервер сам пишет " +
                        "дату, причину и автора бана, а формат этих полей панель не воспроизводит — такая запись " +
                        "может быть прочитана неверно.\n\nЗапусти сервер и нажми «Забанить» снова либо впиши " +
                        "ban " + name + " в консоль сам.",
                        "Нужен запущенный сервер");
        }

        void BanIp()
        {
            // ban-ip в Minecraft принимает и ник: сервер сам берёт адрес, с которого игрок
            // заходил последний раз. Спрашивать у человека цифры, когда у него есть ник, незачем.
            string target = Ip();
            if (!IsSafeIp(target) && !IsSafeNick(target))
            {
                Say(AddressComplaint(target), WarnBrush);
                return;
            }

            if (Running(p) != null)
            {
                SendToServer("ban-ip " + target, $"Отправил команду ban-ip {target}.");
                return;
            }

            Dialog.Info("Блокировку адреса без запущенного сервера панель не создаёт: banned-ips.json сервер " +
                        "заполняет своими датами и причинами, а адрес по нику без сервера всё равно не узнать. " +
                        "Запусти сервер и нажми кнопку снова либо впиши ban-ip " + target + " в консоль сам.",
                        "Нужен запущенный сервер");
        }

        void Pardon(string name)
        {
            if (name.Length == 0)
            {
                Say("У записи не прочитан ник — снимать блокировку придётся правкой файла.", WarnBrush);
                return;
            }

            if (Running(p) != null)
            {
                SendToServer("pardon " + name, $"Отправил команду pardon {name}.");
                return;
            }

            PlayerFile file = Load(PlayerFile.BannedPlayers);
            if (file.Error != null)
            {
                Say($"banned-players.json не читается: {file.Error}", WarnBrush);
                return;
            }

            if (Rewrite(file, file.Without(name), $"Снимаем блокировку с «{name}».")) Refresh();
        }

        void PardonIp(string address)
        {
            if (address.Length == 0)
            {
                Say("У записи не прочитан адрес — разбанить нечего.", WarnBrush);
                return;
            }

            if (Running(p) != null)
            {
                SendToServer("pardon-ip " + address, $"Отправил команду pardon-ip {address}.");
                return;
            }

            PlayerFile file = Load(PlayerFile.BannedIps);
            if (file.Error != null)
            {
                Say($"banned-ips.json не читается: {file.Error}", WarnBrush);
                return;
            }

            if (Rewrite(file, file.Without(address), $"Снимаем блокировку с адреса {address}.")) Refresh();
        }

        void PardonIpTyped()
        {
            string address = Ip();
            if (!IsSafeIp(address))
            {
                Say(IpComplaint(address), WarnBrush);
                return;
            }

            PardonIp(address);
        }

        // Удаление записи: на запущенном сервере — команда, при остановленном — перезапись файла.
        void RemoveFrom(string fileName, string command, string name, string target)
        {
            if (name.Length == 0)
            {
                Say("У записи не прочитан ник — трогать её не будем.", WarnBrush);
                return;
            }

            if (Running(p) != null)
            {
                SendToServer($"{command} {name}", $"Отправил команду {command} {name}.");
                return;
            }

            PlayerFile file = Load(fileName);
            if (file.Error != null)
            {
                Say($"{fileName} не читается: {file.Error}", WarnBrush);
                return;
            }

            if (file.FindByName(name) == null)
            {
                Say($"«{name}» в {fileName} уже нет — обнови список.", muted);
                return;
            }

            if (Rewrite(file, file.Without(name), $"Убираем «{name}» из {target}.")) Refresh();
        }

        void Refresh()
        {
            list.Children.Clear();

            ServerInstance? running = Running(p);
            bool hasProperties = File.Exists(Path.Combine(folder, "server.properties"));
            string whiteList = hasProperties ? ServerProperties.Read(folder, "white-list", "") : "";
            string online = hasProperties ? ServerProperties.Read(folder, "online-mode", "") : "";

            state.Text = running != null
                ? "Сервер запущен панелью: изменения уходят командой в консоль, файлы перезаписывает сервер."
                : "Сервер остановлен: панель правит файлы сборки, изменения применятся при следующем запуске.";
            state.Foreground = running != null ? OkBrush : muted;

            SectionTitle("Правила входа");
            Line(hasProperties
                ? whiteList switch
                {
                    "true" => "white-list=true — сервер пустит только ники из whitelist.json.",
                    "false" => "white-list=false — список в файле есть, но сервер по нему не пускает: заходит любой.",
                    _ => "Ключа white-list в server.properties нет — сервер ведёт себя так, будто список выключен.",
                }
                : "server.properties ещё не создан: список можно завести заранее, но применять его сервер начнёт после первого запуска.");

            var toggle = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            toggle.Children.Add(Btn("Включить белый список", (_, _) => ToggleWhitelist(true), whiteList != "true"));
            toggle.Children.Add(Btn("Выключить белый список", (_, _) => ToggleWhitelist(false), whiteList == "true"));
            list.Children.Add(toggle);

            Line(online switch
            {
                "false" => "online-mode=false — ник никто не проверяет: в список можно вписать любого, и под этим ником " +
                           "зайдёт кто угодно. Такой список скорее удобство, а не защита.",
                "true" => "online-mode=true — сервер сверяет ник с аккаунтом Mojang и его uuid, поэтому список работает как защита.",
                _ => "online-mode в файле не задан: как себя поведёт сервер, зависит от значений ядра по умолчанию.",
            });

            PlayerFile whitelist = Load(PlayerFile.Whitelist);
            PlayerFile ops = Load(PlayerFile.Ops);
            PlayerFile bannedPlayers = Load(PlayerFile.BannedPlayers);
            PlayerFile bannedIps = Load(PlayerFile.BannedIps);

            SectionTitle($"Белый список · whitelist.json ({Count(whitelist)})");
            if (Note(whitelist, "В белом списке пока никого."))
            {
                foreach (JsonObject entry in whitelist.Entries)
                {
                    string name = PlayerFile.NameOf(entry) ?? "";
                    EntryRow(name.Length == 0 ? "запись без ника" : name,
                        "uuid " + PlayerFile.ShortUuid(PlayerFile.UuidOf(entry)),
                        "Убрать", () => RemoveFrom(PlayerFile.Whitelist, "whitelist remove", name, "белого списка"));
                }
            }

            SectionTitle($"Операторы · ops.json ({Count(ops)})");
            if (Note(ops, "Операторов пока нет: это права на команды сервера, их дают одному-двум игрокам."))
            {
                foreach (JsonObject entry in ops.Entries)
                {
                    string name = PlayerFile.NameOf(entry) ?? "";
                    EntryRow(name.Length == 0 ? "запись без ника" : name,
                        $"уровень {PlayerFile.LevelOf(entry)} · uuid " + PlayerFile.ShortUuid(PlayerFile.UuidOf(entry)),
                        "Снять права", () => RemoveFrom(PlayerFile.Ops, "deop", name, "списка операторов"));
                }
            }

            SectionTitle($"Забаненные игроки · banned-players.json ({Count(bannedPlayers)})");
            if (Note(bannedPlayers, "Забаненных игроков нет."))
            {
                foreach (JsonObject entry in bannedPlayers.Entries)
                {
                    string name = PlayerFile.NameOf(entry) ?? "";
                    EntryRow(name.Length == 0 ? "запись без ника" : name,
                        "uuid " + PlayerFile.ShortUuid(PlayerFile.UuidOf(entry)),
                        "Разбанить", () => Pardon(name));
                }
            }

            SectionTitle($"Забаненные адреса · banned-ips.json ({Count(bannedIps)})");
            if (Note(bannedIps, "Забаненных адресов нет."))
            {
                foreach (JsonObject entry in bannedIps.Entries)
                {
                    string address = PlayerFile.IpOf(entry) ?? "";
                    EntryRow(address.Length == 0 ? "запись без адреса" : address,
                        "",
                        "Разбанить адрес", () => PardonIp(address));
                }
            }
        }

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(Txt($"Игроки и права — «{p.Name}»", 18, true));
        head.Children.Add(state);
        head.Children.Add(action);

        var inputs = new StackPanel();
        inputs.Children.Add(Txt("Ник игрока", 12, true, muted));
        inputs.Children.Add(nick);

        var nickRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        nickRow.Children.Add(Btn("В белый список", (_, _) => AddWhitelist()));
        nickRow.Children.Add(Btn("Дать оператора", (_, _) => AddOp()));
        nickRow.Children.Add(Btn("Забанить", (_, _) => Ban()));
        inputs.Children.Add(nickRow);
        inputs.Children.Add(Txt("Ник без пробелов и спецсимволов: кавычка или перевод строки превратились бы во " +
                                "вторую команду в консоли, поэтому панель такое не отправляет.", 11, false, muted));

        var ipLabel = Txt("IP-адрес или ник", 12, true, muted);
        ipLabel.Margin = new Thickness(0, 14, 0, 0);
        inputs.Children.Add(ipLabel);
        inputs.Children.Add(ip);

        var ipRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        ipRow.Children.Add(Btn("Забанить адрес", (_, _) => BanIp()));
        ipRow.Children.Add(Btn("Разбанить адрес", (_, _) => PardonIpTyped()));
        inputs.Children.Add(ipRow);
        inputs.Children.Add(Txt("«Забанить адрес» блокирует не ник, а подключение: с этого адреса не зайдёт никто, " +
                                "даже под другим именем. Сервер понимает и ник — он сам вспомнит, откуда игрок " +
                                "заходил, поэтому в поле можно вписать и его. Адрес видно в журнале подключения: " +
                                "строка вида Steve[/94.113.22.55:51234] logged in — она есть в консоли сборки и в " +
                                "logs/latest.log. «Разбанить адрес» требует именно адрес: по нику сервер его не находит.",
                                11, false, muted));

        Refresh();

        var bottom = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(Btn("Обновить", (_, _) => Refresh(), true));
        bottom.Children.Add(Btn("Открыть папку сборки", (_, _) => OpenUrl(folder)));
        bottom.Children.Add(Btn("Закрыть", (_, _) => win.Close()));

        var topPanel = new StackPanel();
        topPanel.Children.Add(head);
        topPanel.Children.Add(inputs);

        var content = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(topPanel, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        content.Children.Add(topPanel);
        content.Children.Add(bottom);
        content.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        win.Content = content;
        win.ShowDialog();
    }

    // Ник, который можно отправить серверу: только то, что Minecraft разрешает в имени.
    // Всё остальное ломает команду — а с переводом строки в поле стало бы второй командой.
    private static bool IsSafeNick(string name) =>
        name.Length is > 0 and <= 16 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static string NickComplaint(string name) => name.Length == 0
        ? "Сначала впиши ник: без имени команда не соберётся."
        : $"Ник «{name}» панель не отправит. Minecraft разрешает в имени до 16 символов: буквы, цифры и «_».";

    // Адрес — цифры, точки и двоеточия (IPv6 тоже проходит). Проверяется до отправки: перевод
    // строки в поле собрал бы из одной команды две.
    private static bool IsSafeIp(string address) =>
        address.Length is > 0 and <= 45 && address.All(c => char.IsAsciiDigit(c) || c is '.' or ':');

    private static string IpComplaint(string address) => address.Length == 0
        ? "Сначала впиши адрес: без него команда не соберётся."
        : $"«{address}» не похоже на IP-адрес: панель пропускает только цифры, точки и двоеточия.";

    // ban-ip принимает и ник, поэтому поле пропускает и его — но только то, что разрешено
    // в имени Minecraft. Прочее (хост, e-mail, маска с пробелом) сервер всё равно не понял бы.
    private static string AddressComplaint(string target) => target.Length == 0
        ? "Сначала впиши адрес или ник: без них команда не соберётся."
        : $"«{target}» — не адрес и не ник. Панель пропускает адреса из цифр, точек и двоеточий " +
          "и ники до 16 символов: буквы, цифры и «_».";

    // Окно «Резервные копии мира»: что панель уже скопировала, ручная копия и откат.
    // Откат ничего не удаляет — текущий мир уезжает в папку рядом со сборкой и остаётся там,
    // пока человек сам не решит, что копия ему нравится больше.
    private void ShowBackups(ServerProfile p)
    {
        string folder = BuildChecker.ResolveFolder(p, dataDir);

        if (BuildChecker.IsBedrock(p))
        {
            Dialog.Info("Это Bedrock-сборка: её миры лежат в папке worlds и устроены иначе, а запуск " +
                        "такого сервера панель ещё не делает. Резервные копии сейчас — только для Java-сборок.",
                        "Не Java-сборка");
            return;
        }

        string root = WorldBackup.RootFor(dataDir, p.Name);
        string world = WorldBackup.WorldPath(folder);
        string worldName = Path.GetFileName(world);
        var muted = (Brush)FindResource("Muted");

        var win = new Window
        {
            Title = $"Резервные копии — {p.Name}",
            Width = 660,
            Height = 620,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("Bg"),
            Foreground = TileForeground
        };
        Dialog.DarkCaption(win);

        var state = Txt("", 12, false, muted);
        state.Margin = new Thickness(0, 4, 0, 0);
        var action = Txt("Действий пока не было.", 12, false, muted);
        action.Margin = new Thickness(0, 4, 0, 0);
        var list = new StackPanel();

        // Копирование — процесс не быстрый и повторяющийся: вторая кнопка, нажатая пока первая
        // считает, породила бы две копии и гонку за папку мира.
        bool busy = false;

        void Say(string text, Brush? brush = null)
        {
            action.Text = text;
            action.Foreground = brush ?? muted;
        }

        void Line(string text, Brush? brush = null)
        {
            var t = Txt(text, 12, false, brush ?? muted);
            t.Margin = new Thickness(0, 4, 0, 0);
            list.Children.Add(t);
        }

        void SectionTitle(string text)
        {
            var t = Txt(text, 12, true, muted);
            t.Margin = new Thickness(0, 18, 0, 2);
            list.Children.Add(t);
        }

        void CopyRow(WorldCopy copy)
        {
            var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            var rollback = Btn("Откатить", (_, _) => Restore(copy));
            var remove = Btn("Удалить", (_, _) => Remove(copy));
            buttons.Children.Add(rollback);
            buttons.Children.Add(remove);

            var text = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            text.Children.Add(Txt($"{copy.Label} · мир «{copy.WorldName}»", 13, true));
            text.Children.Add(Txt($"{SystemInfo.FormatSize(copy.Bytes)} · {copy.Files} файлов · {copy.Path}",
                11, false, muted));

            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 6) };
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(text);
            list.Children.Add(row);
        }

        async Task Refresh()
        {
            list.Children.Clear();

            ServerInstance? running = Running(p);
            state.Text = running != null
                ? "Сервер запущен: копировать и откатывать нельзя — файлы мира заняты, и копия вышла бы " +
                  "набором кусков из разных состояний."
                : Directory.Exists(world)
                    ? $"Мир «{worldName}» в сборке есть — копия снимается с него."
                    : $"Папки мира «{worldName}» в сборке ещё нет: её создаст первый запуск.";
            state.Foreground = running != null ? WarnBrush : muted;

            List<WorldCopy> copies = await Task.Run(() => WorldBackup.List(root));

            SectionTitle($"Копии этой сборки · {root}");
            if (copies.Count == 0)
                Line("Копий пока нет. Автокопия перед запуском включается в настройках этой сборки, " +
                     "ручная — кнопкой ниже.", muted);
            foreach (WorldCopy copy in copies) CopyRow(copy);

            SectionTitle("Хранение");
            Line($"Панель держит {Math.Max(1, p.BackupKeep)} последних копий и удаляет старые сама — " +
                 "других папок она не касается. Прежний мир, отодвинутый при откате, остаётся в папке " +
                 "сборки до вашей ручной чистки.");
        }

        async void Create()
        {
            if (busy) return;
            if (Running(p) != null)
            {
                Say("Сервер запущен — копия с живых файлов мира получилась бы разной. Останови сервер.", WarnBrush);
                return;
            }

            if (!Directory.Exists(world))
            {
                Say($"Папки мира «{worldName}» ещё нет — копировать нечего.", WarnBrush);
                return;
            }

            if (!BeginCopy(folder)) return;
            busy = true;
            try
            {
                long size = await Task.Run(() => WorldBackup.SizeOf(world));
                if (!Dialog.Confirm($"Скопирую мир «{worldName}» ({SystemInfo.FormatSize(size)}) в:\n{root}\n\n" +
                        "Копия нужна, чтобы откатиться, если обновление мода или мира пойдёт не так.",
                        "Резервная копия мира"))
                    return;

                Say("Копирую мир…");
                WorldCopy? made = null;
                var (finished, error) = await RunWithProgressAsync("РЕЗЕРВНАЯ КОПИЯ МИРА",
                    $"{p.Name} · мир «{worldName}»", world, size, async (progress, token) =>
                    {
                        made = await WorldBackup.CreateAsync(folder, root, progress, token);
                    });

                if (!finished)
                {
                    if (error != null)
                    {
                        Say("Копия не сделана: " + error, WarnBrush);
                        Dialog.Info(error, "Копия не сделана");
                    }
                    else
                    {
                        Say("Копирование отменено — наполовину скопированную папку панель убрала.", muted);
                    }

                    return;
                }

                int removed = WorldBackup.Trim(root, Math.Max(1, p.BackupKeep));
                Say($"Копия готова: {SystemInfo.FormatSize(made!.Bytes)}, {made.Files} файлов" +
                    (removed > 0 ? $"; удалено старых копий: {removed}." : "."), OkBrush);
            }
            finally
            {
                busy = false;
                EndCopy(folder);
                await Refresh();
            }
        }

        async void Restore(WorldCopy copy)
        {
            if (busy) return;
            if (Running(p) != null)
            {
                Say("Сервер запущен — откатывать нельзя: он держит файлы мира открытыми.", WarnBrush);
                return;
            }

            bool sameName = WorldBackup.MatchesCurrentWorld(copy, folder);
            string preview = $"Откатываю сборку «{p.Name}» к копии от {copy.Label} " +
                             $"({SystemInfo.FormatSize(copy.Bytes)}, мир «{copy.WorldName}»).\n\n" +
                             (Directory.Exists(world)
                                 ? $"Текущий мир «{worldName}» панель НЕ удаляет: она переименует его в " +
                                   $"«{worldName}__{WorldBackup.RollbackMark}-…» и оставит в папке сборки. " +
                                   "Место под него нужно.\n\n"
                                 : $"Папки мира «{worldName}» сейчас нет — просто положим копию на её место.\n\n") +
                             (sameName
                                 ? ""
                                 : $"Копия сделана для мира «{copy.WorldName}», а сервер сейчас зовёт мир «{worldName}»: " +
                                   "содержимое копии ляжет в папку с текущим именем, иначе сервер не найдёт мир " +
                                   "и сгенерирует новый.\n\n") +
                             "Продолжить?";
            if (!Dialog.Confirm(preview, "Откат к копии")) return;
            if (!BeginCopy(folder)) return;

            busy = true;
            try
            {
                Say("Откатываю…");
                string moved = "";
                long size = copy.Bytes;
                var (finished, error) = await RunWithProgressAsync("ОТКАТ К КОПИИ",
                    $"{p.Name} · копия от {copy.Label}", copy.Path, size, async (progress, token) =>
                    {
                        moved = await WorldBackup.RestoreAsync(copy, folder, progress, token);
                    });

                if (!finished)
                {
                    if (error != null)
                    {
                        Say("Откат не выполнен: " + error, WarnBrush);
                        Dialog.Info(error, "Откат не выполнен");
                    }
                    else
                    {
                        Say("Откат прерван: копию дописывать не стали, прежний мир вернулся на место.", muted);
                    }

                    return;
                }

                Say(moved.Length > 0
                    ? $"Мир откатнут к копии от {copy.Label}. Прежний мир лежит здесь: {moved} — " +
                      "удалишь сам, когда убедишься, что откат удался."
                    : $"Мир откатнут к копии от {copy.Label}.", OkBrush);
                Dialog.Info($"Сервер теперь стартует с миром из копии от {copy.Label}.\n\n" +
                    (moved.Length > 0
                        ? $"Прежняя папка не удалена и ждёт рядом со сборкой:\n{moved}\n" +
                          "Разберёшься с ней сам: панель чужие папки в сборке не трогает."
                        : "Копировать было нечего — мира в сборке не было."),
                    "Откат готов");
            }
            finally
            {
                busy = false;
                EndCopy(folder);
                await Refresh();
            }
        }

        async void Remove(WorldCopy copy)
        {
            if (busy) return;

            if (!Dialog.Confirm($"Удалить копию от {copy.Label} ({SystemInfo.FormatSize(copy.Bytes)}, " +
                    $"мир «{copy.WorldName}»)?\n\n{copy.Path}\n\nУдалишь — и откатиться на это состояние " +
                    "мира будет некуда.", "Удаление копии"))
                return;

            try
            {
                await Task.Run(() => WorldBackup.Delete(copy));
                Say("Копия удалена.", OkBrush);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say("Удалить не удалось: " + ex.Message, WarnBrush);
            }

            await Refresh();
        }

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(Txt($"Резервные копии — «{p.Name}»", 18, true));
        head.Children.Add(state);
        head.Children.Add(action);

        var bottom = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(Btn("Сделать копию сейчас", (_, _) => Create(), true));
        bottom.Children.Add(Btn("Открыть папку копий", (_, _) =>
        {
            // Каталог свой и создаётся при первой копии: открыть его заранее — не обещание,
            // а просто пустая папка, в которой человек увидит, куда лягут копии.
            try { Directory.CreateDirectory(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say("Папку копий не создать: " + ex.Message, WarnBrush);
                return;
            }

            OpenUrl(root);
        }));
        bottom.Children.Add(Btn("Закрыть", (_, _) => win.Close()));

        var content = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        content.Children.Add(head);
        content.Children.Add(bottom);
        content.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        win.Content = content;
        _ = Refresh();
        win.ShowDialog();
    }

    // Обновления модов сборки. Идентификация одна на весь экран: SHA-1 файла ищется в базе Modrinth,
    // и если её нет в базе, панель так и пишет — «не опознан». По имени файла она проект не угадывает
    // и ничего не предлагает, потому что проверить такое предложение было бы нечем.
    private void ShowModUpdates(ServerProfile p)
    {
        string folder = BuildChecker.ResolveFolder(p, dataDir);

        if (BuildChecker.IsBedrock(p))
        {
            Dialog.Info("Это Bedrock-сборка: у неё свои форматы дополнений и запуск такого сервера панель " +
                        "ещё не делает. Обновления модов сейчас — только для Java-сборок.", "Не Java-сборка");
            return;
        }

        string mods = ModUpdates.ModsFolder(folder);
        if (!Directory.Exists(mods))
        {
            Dialog.Info($"Папки mods в сборке ещё нет:\n{mods}\n\nОна появляется, когда первый мод скачан " +
                        "из каталога или положен туда руками. Вложенные папки сервер модами не считает, " +
                        "поэтому смотрим только верхний уровень.", "Папки mods нет");
            return;
        }

        string backupRoot = ModUpdates.RootFor(dataDir, p.Name);
        string? buildLoader = ModUpdates.LoaderFor(p.Core);
        var muted = (Brush)FindResource("Muted");

        var win = new Window
        {
            Title = $"Обновления модов — {p.Name}",
            Width = 760,
            Height = 640,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("Bg"),
            Foreground = TileForeground
        };
        Dialog.DarkCaption(win);

        var state = Txt("", 12, false, muted);
        state.Margin = new Thickness(0, 4, 0, 0);
        var action = Txt("Проверку запускает кнопка снизу.", 12, false, muted);
        action.Margin = new Thickness(0, 4, 0, 0);
        var list = new StackPanel();

        var items = new List<ModUpdate>();
        var frames = new Dictionary<ModUpdate, Border>();
        bool busy = false;
        bool checking = false;
        CancellationTokenSource? cts = null;

        void Say(string text, Brush? brush = null)
        {
            action.Text = text;
            action.Foreground = brush ?? muted;
        }

        void StateLine()
        {
            ServerInstance? running = Running(p);
            state.Text = running != null
                ? "Сервер запущен: проверить можно, поставить обновление — нет. Java держит jar открытым, " +
                  "а новый файл всё равно начал бы работать только после перезапуска."
                : $"Папка: {mods} · ищем файлы под Minecraft {p.Version}" +
                  (buildLoader == null ? "" : $" и загрузчик {buildLoader}");
            state.Foreground = running != null ? WarnBrush : muted;
        }

        Border FrameFor(ModUpdate item)
        {
            var frame = new Border
            {
                Background = (Brush)FindResource("Panel2"),
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8)
            };
            frames[item] = frame;
            return frame;
        }

        string Headline(ModUpdate item) => item.State switch
        {
            ModState.Pending => "Проверяю…",
            ModState.Outdated => "Есть обновление",
            ModState.Current => "Самый свежий",
            ModState.NotForThisBuild => "Под эту сборку свежих нет",
            ModState.Unrecognized => "Modrinth этого файла не знает",
            _ => "Проверить не удалось"
        };

        // Кнопки строки компактнее обычных: их в списке бывает двадцать, и MinWidth в 80
        // превратил бы строку в два столбца.
        Button RowBtn(string label, RoutedEventHandler click, bool accent = false)
        {
            var b = Btn(label, click, accent);
            b.MinWidth = 0;
            b.MinHeight = 34;
            return b;
        }

        // Строка перерисовывается целиком: до ответа Modrinth в ней только имя файла, после —
        // название проекта, версии и кнопки. Промежуточных состояний два, и путаться в них незачем.
        void Render(ModUpdate item)
        {
            if (!frames.TryGetValue(item, out Border? frame)) return;

            var buttons = new WrapPanel();
            if (item.CanUpdate) buttons.Children.Add(RowBtn("Обновить", (_, _) => UpdateOne(item), true));
            if (item.PageUrl.Length > 0) buttons.Children.Add(RowBtn("Страница ↗", (_, _) => OpenUrl(item.PageUrl)));

            string versions = item.Installed.Length == 0
                ? "версия неизвестна"
                : item.CanUpdate ? $"{item.Installed} → {item.AvailableVersion}" : item.Installed;

            string status = item.Note.Length > 0 ? Headline(item) + " · " + item.Note : Headline(item);
            Brush tone = item.State switch
            {
                ModState.Outdated or ModState.Failed => WarnBrush,
                ModState.Current => OkBrush,
                _ => muted
            };

            var text = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            text.Children.Add(Txt(item.Label, 13, true));
            text.Children.Add(Txt($"{item.FileName} · {item.SizeText} · {versions}", 11, false, muted));
            text.Children.Add(Txt(status, 11.5, false, tone));

            var head = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(buttons, Dock.Right);
            head.Children.Add(buttons);
            // Картинку показываем только у опознанного проекта: у «не опознанного» файла и буквы
            // под заглушкой брать нечего — название неизвестно.
            if (item.ProjectId != null) head.Children.Add(ModIcon(IconCard(item)));
            head.Children.Add(text);

            frame.Child = head;
            frame.BorderBrush = item.State == ModState.Outdated ? WarnBrush : (Brush)FindResource("Line");
        }

        // Иконку рисует тем же кодом, что и карточки каталога, поэтому ей нужен формат карточки.
        CatalogCard IconCard(ModUpdate item) => new(
            "modrinth", item.ProjectId ?? "", item.Label, "", "", 0, item.PageUrl, item.IconUrl ?? "");

        void Summary()
        {
            int outdated = items.Count(i => i.State == ModState.Outdated);
            int current = items.Count(i => i.State == ModState.Current);
            int unknown = items.Count(i => i.State == ModState.Unrecognized);
            int failed = items.Count(i => i.State == ModState.Failed);
            int skipped = items.Count(i => i.State == ModState.NotForThisBuild);
            int others = ModUpdates.CountOtherFiles(mods);

            Say($"Проверено файлов: {items.Count}. Доступно обновлений: {outdated} · свежих: {current} · " +
                $"под эту сборку ничего нет: {skipped} · не опознано: {unknown}" +
                (failed > 0 ? $" · ошибок: {failed} (Modrinth не ответил или файл не читается — " +
                              "перезапусти проверку через минуту, сервис режет частые запросы)" : "") +
                (others > 0 ? $" · файлов не .jar: {others}, их панель не трогает" : "") + ".",
                outdated > 0 ? WarnBrush : muted);
        }

        // Один файл: докачка во временную папку, переезд прежнего в копии, новый в mods.
        // true — обновлён. Занятость интерфейса и разрешение человека — забота вызывающего.
        async Task<bool> ApplyFile(ModUpdate item)
        {
            Say($"Качу {item.AvailableFile!.Filename}…");
            AppliedUpdate? applied = null;
            var (finished, error) = await RunWithProgressAsync("ОБНОВЛЕНИЕ МОДА",
                $"{item.Label} · {item.Installed} → {item.AvailableVersion}",
                item.AvailableFile.Filename, item.AvailableFile.Size,
                async (progress, token) =>
                {
                    applied = await ModUpdates.ApplyAsync(item, folder, backupRoot, progress, token);
                });

            if (!finished)
            {
                Say(error == null
                    ? $"Обновление {item.FileName} отменено: прежний файл не тронут, недокачанный убран."
                    : "Обновление не прошло: " + error, error == null ? muted : WarnBrush);
                return false;
            }

            item.Installed = item.AvailableVersion;
            item.Available = null;
            item.AvailableFile = null;
            item.Behind = 0;
            item.State = ModState.Current;
            item.Note = $"Лёг в mods {Path.GetFileName(applied!.NewFile)}, прежний — в копиях";
            Render(item);
            return true;
        }

        string AskText(ModUpdate item) =>
            $"{item.Label}\n{item.Installed} → {item.AvailableVersion}\nфайл: {item.AvailableFile!.Filename}\n\n" +
            $"Прежний {item.FileName} панель не удаляет: он переедет в подпапку времени в\n{backupRoot}\n\n" +
            "Сервер возьмёт новый файл при следующем запуске. Продолжить?";

        async void UpdateOne(ModUpdate item)
        {
            if (busy || checking || !item.CanUpdate) return;

            if (Running(p) != null)
            {
                Say("Сервер запущен — файл занят Java, и новый мод всё равно не заработал бы до перезапуска. " +
                    "Сначала останови сервер.", WarnBrush);
                return;
            }

            if (!Dialog.Confirm(AskText(item), "Обновить мод")) return;

            busy = true;
            StateLine();
            try
            {
                bool done = await ApplyFile(item);
                if (done)
                    Say($"Готово: {item.Label} → {item.Installed}. Прежний файл в копиях: {backupRoot}", OkBrush);
            }
            finally
            {
                busy = false;
                StateLine();
            }
        }

        async void UpdateAll()
        {
            if (busy || checking) return;

            var updatable = items.Where(i => i.CanUpdate).ToList();
            if (updatable.Count == 0)
            {
                Say("Обновлять нечего: панель не нашла подходящих файлов. «Проверить заново» перечитает папку.", muted);
                return;
            }

            if (Running(p) != null)
            {
                Say("Сервер запущен — файлы заняты Java. Останови сервер и обнови список.", WarnBrush);
                return;
            }

            var lines = updatable.Select(i => $"· {i.Label}: {i.Installed} → {i.AvailableVersion}");
            if (!Dialog.Confirm(
                    $"Обновлю {updatable.Count} модов:\n{string.Join("\n", lines)}\n\n" +
                    $"Каждый прежний файл переедет в подпапку времени в\n{backupRoot}\n" +
                    "и вернётся в mods, если докачка или установка не удаются. Файлы ставятся по очереди: " +
                    "отмена остановит очередь, но уже поставленные останутся.\n\nПродолжить?",
                    "Обновить все моды"))
                return;

            busy = true;
            StateLine();
            int done = 0;
            try
            {
                foreach (ModUpdate item in updatable)
                    if (await ApplyFile(item)) done++;
            }
            finally
            {
                busy = false;
                StateLine();
            }

            Say(done > 0
                ? $"Обновлено: {done} из {updatable.Count}. Запусти сервер, чтобы новые файлы вступили в силу."
                : "Ни один файл не обновился — причину панель писала выше.", done > 0 ? OkBrush : muted);
        }

        async void Check()
        {
            if (busy || checking) return;

            cts?.Cancel();
            cts = new CancellationTokenSource();
            checking = true;
            items.Clear();
            frames.Clear();
            list.Children.Clear();
            StateLine();

            try
            {
                Say("Считаю контрольные суммы файлов…");
                List<ModUpdate> found = await ModUpdates.ScanAsync(mods, cts.Token);
                if (found.Count == 0)
                {
                    Say($"В {mods} нет ни одного .jar — обновлять нечего.", muted);
                    return;
                }

                foreach (ModUpdate item in found)
                {
                    items.Add(item);
                    list.Children.Add(FrameFor(item));
                    Render(item);
                }

                Say($"Проверяю {found.Count} файлов на Modrinth — по три запроса на каждый, " +
                    "поэтому список наполняется постепенно.");
                await ModUpdates.CheckAsync(found, p.Version, buildLoader, Render, cts.Token);
                Summary();
            }
            catch (OperationCanceledException)
            {
                Say("Проверка остановлена.", muted);
            }
            finally
            {
                checking = false;
            }
        }

        var bottom = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(Btn("Проверить заново", (_, _) => Check(), true));
        bottom.Children.Add(Btn("Обновить все доступные", (_, _) => UpdateAll()));
        bottom.Children.Add(Btn("Открыть папку mods", (_, _) => OpenUrl(mods)));
        bottom.Children.Add(Btn("Открыть папку копий", (_, _) =>
        {
            // Каталог свой и создаётся при первом обновлении: показать его заранее — просто
            // пустая папка, в которой видно, куда лягут вытесненные jar'ы.
            try { Directory.CreateDirectory(backupRoot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say("Папку копий не создать: " + ex.Message, WarnBrush);
                return;
            }

            OpenUrl(backupRoot);
        }));
        bottom.Children.Add(Btn("Закрыть", (_, _) => win.Close()));

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(Txt($"Обновления модов — «{p.Name}»", 18, true));
        head.Children.Add(state);
        head.Children.Add(action);
        head.Children.Add(Txt(
            $"Вытесненные файлы панель не удаляет: каждый переезжает в подпапку времени в {backupRoot}, " +
            "и удаляешь их только ты.", 11, false, muted));
        if (buildLoader == null)
        {
            head.Children.Add(Txt(
                $"Ядро «{p.Core}» не относится к Fabric, Quilt, Forge или NeoForge, поэтому фильтр по " +
                "загрузчику снимается и версии берутся из подписи самого файла. Ядра на Bukkit (Paper " +
                "и его ответвления) читают плагины из папки plugins, а не из mods.", 11, false, muted));
        }

        var content = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        content.Children.Add(head);
        content.Children.Add(bottom);
        content.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        win.Content = content;
        // Закрыли окно посреди проверки — запросы не должны жить без него.
        win.Closing += (_, _) => { cts?.Cancel(); };
        Check();
        win.ShowDialog();
    }


    // Окно с прогрессом долгой работы с файлами — та же форма, что и при загрузке ядра, но отмена
    // здесь означает «прекрати копировать», а не «удали файл». Finished=false и Error=null —
    // человек нажал «Отменить» сам.
    private Task<(bool Finished, string? Error)> RunWithProgressAsync(string caption, string subtitle,
        string source, long total, Func<IProgress<(long Done, long Total)>, CancellationToken, Task> work) =>
        RunWithProgressAsync(caption, subtitle, source, total,
            (progress, _, token) => work(progress, token));

    // Тот же экран, но у работы есть что показать строкой выше полосы: «файл 12 из 169» важнее
    // одной только полосы, когда установка идёт несколько минут.
    private Task<(bool Finished, string? Error)> RunWithProgressAsync(string caption, string subtitle,
        string source, long total, Func<IProgress<(long Done, long Total)>, IProgress<string>, CancellationToken, Task> work) =>
        RunWithProgressAsync(caption, subtitle, source, total, null, work);

    // unit == null — шкала в байтах, как у копий мира и одиночных файлов. У CurseForge-пака размер
    // файла известен только из ответа API, поэтому там шкала пофайловая: «12 из 168» честнее, чем
    // полоса, растянутая по догадкам.
    private async Task<(bool Finished, string? Error)> RunWithProgressAsync(string caption, string subtitle,
        string source, long total, string? unit,
        Func<IProgress<(long Done, long Total)>, IProgress<string>, CancellationToken, Task> work)
    {
        var cts = new CancellationTokenSource();
        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = total > 0 ? total : 1,
            Margin = new Thickness(0, 14, 0, 8)
        };
        var info = Txt("Считаю файлы…", 12, false, (Brush)FindResource("Muted"));
        var stageLine = Txt("", 12, false, (Brush)FindResource("Muted"));
        stageLine.Margin = new Thickness(0, 6, 0, 0);
        stageLine.TextWrapping = TextWrapping.Wrap;
        stageLine.Visibility = Visibility.Collapsed;

        var progress = new Progress<(long Done, long Total)>(report =>
        {
            bar.Value = Math.Min(report.Done, bar.Maximum);
            info.Text = unit == null
                ? $"{SystemInfo.FormatSize(report.Done)} из {SystemInfo.FormatSize(report.Total)}"
                : $"{report.Done} из {report.Total} {unit}";
        });
        var stage = new Progress<string>(text =>
        {
            stageLine.Text = text;
            stageLine.Visibility = Visibility.Visible;
        });

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Txt(caption, 11, true, (Brush)FindResource("Accent")));
        panel.Children.Add(Txt(subtitle, 18, true));
        panel.Children.Add(Txt($"Откуда: {source}", 11, false, (Brush)FindResource("Muted")));
        panel.Children.Add(bar);
        panel.Children.Add(info);
        panel.Children.Add(stageLine);

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Btn("Отменить", (_, _) => cts.Cancel()));
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = caption,
            Width = 560,
            Height = 280,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("Bg"),
            Foreground = TileForeground,
            Content = panel
        };
        Dialog.DarkCaption(win);
        win.Show();
        // Крестик на окне — та же отмена: недописанную копию работа сама уберёт.
        win.Closing += (_, _) => cts.Cancel();

        bool finished = false;
        string? error = null;
        try
        {
            await work(progress, stage, cts.Token);
            finished = true;
        }
        catch (OperationCanceledException)
        {
            // Отмена — не ошибка: окно просто закрывается, а что убирать после себя — решает работа.
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            win.Close();
            cts.Dispose();
        }

        return (finished, error);
    }

    // Копия мира до запуска. null — запуск отменён: автоскопия стоит в связке «сначала копия»,
    // и без неё человек просил не начинать.
    private async Task<string?> BackupBeforeStartAsync(ServerProfile p, string folder)
    {
        string world = WorldBackup.WorldPath(folder);
        string worldName = Path.GetFileName(world);
        if (!Directory.Exists(world))
            return $"Мира «{worldName}» в сборке ещё нет — копировать нечего, он создастся при этом запуске.";

        string root = WorldBackup.RootFor(dataDir, p.Name);
        if (!BeginCopy(folder)) return null;

        try
        {
            long size = await Task.Run(() => WorldBackup.SizeOf(world));
            if (!Dialog.Confirm($"Перед запуском копирую мир «{worldName}» ({SystemInfo.FormatSize(size)}) в:\n{root}\n\n" +
                    "Можно отменить — тогда и сервер не запустится; можно убрать автокопию в настройках сборки.",
                    "Резервная копия мира"))
                return null;

            WorldCopy? made = null;
            var (finished, error) = await RunWithProgressAsync("КОПИЯ ПЕРЕД ЗАПУСКОМ",
                $"{p.Name} · мир «{worldName}»", world, size, async (progress, token) =>
                {
                    made = await WorldBackup.CreateAsync(folder, root, progress, token);
                });

            if (!finished)
            {
                if (error == null) return null;
                if (Dialog.Confirm($"Копия не получилась:\n{error}\n\nЗапустить сервер без копии?", "Копия не сделана"))
                    return "";
                return null;
            }

            int removed = WorldBackup.Trim(root, Math.Max(1, p.BackupKeep));
            return $"Мир скопирован: {SystemInfo.FormatSize(made!.Bytes)}, {made.Files} файлов → {made.Path}" +
                   (removed > 0 ? $"; удалено старых копий: {removed}." : ".");
        }
        finally
        {
            EndCopy(folder);
        }
    }

    // true — копировать можно; false — для этой папки уже идёт другая копия, и человек предупреждён.
    private bool BeginCopy(string folder)
    {
        if (copying.Add(folder)) return true;
        Dialog.Info("Мир этой сборки уже копируется — дождись конца. Запуск или вторая копия посреди " +
                    "копии портят и копию, и мир.", "Копия уже идёт");
        return false;
    }

    private void EndCopy(string folder) => copying.Remove(folder);

    private void ShowCatalog()
    {
        SetActiveNav("Catalog");
        currentPage = "catalog";

        var root = new StackPanel();
        var modes = new WrapPanel { Margin = new Thickness(0, 0, 0, 15) };
        modes.Children.Add(ModeChip("Моды", "mods"));
        modes.Children.Add(ModeChip("Ядра серверов", "cores"));
        root.Children.Add(modes);

        if (_catalogMode == "cores")
        {
            SetHeader("ЗАГРУЗКИ", "Каталог ядер", "Серверные ядра Java и Bedrock: что-то панель ставит сама, " +
                "что-то придётся положить в папку руками.");
            root.Children.Add(Txt(
                "Плитка с надписью «панель устанавливает сама» ставится кнопкой «Установить ядро» на сборке. " +
                "Остальные открывают страницу проекта: там берут файл и кладут его в папку сборки — панель " +
                "после этого увидит ядро и предложит запустить. Если твоего ядра в списке нет, в настройках " +
                "сборки выбери последний пункт — «Своё ядро»: панель не будет ничего качать и откроет папку, " +
                "куда нужно положить файл.", 13, false, (Brush)FindResource("Muted")));
            root.Children.Add(CoreCatalogGrid());
            SetPage(root, "catalog-cores");
            return;
        }

        SetHeader("ЗАГРУЗКИ", "Каталог модов", "Поиск по Modrinth или CurseForge: файл ляжет в mods или в datapacks мира. Свою карту можно поставить из zip.");
        var sources = new WrapPanel { Margin = new Thickness(0, 0, 0, 7) };
        sources.Children.Add(SourceChip("Modrinth", "modrinth"));
        sources.Children.Add(SourceChip("CurseForge", "curseforge"));
        root.Children.Add(sources);
        root.Children.Add(ModFilterCard());
        root.Children.Add(Txt(_modStatus, 12, false, (Brush)FindResource("Muted")));
        root.Children.Add(ModResultsGrid());
        SetPage(root, "catalog");
    }

    private Button SourceChip(string label, string source)
    {
        bool active = _catalogSource == source;
        var b = new Button
        {
            Content = label,
            MinWidth = 0,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Background = active ? (Brush)FindResource("Pressed") : (Brush)FindResource("Panel2"),
            BorderBrush = active ? (Brush)FindResource("AccentLine") : (Brush)FindResource("Line")
        };
        b.Click += (_, _) =>
        {
            _catalogSource = source;
            _cards = new();
            _modStatus = source == "modrinth"
                ? "Поиск по публичному API Modrinth. Ключ не нужен."
                : "Поиск по CurseForge требует бесплатный API-ключ: впиши его в разделе «Настройки».";
            ShowCatalog();
        };
        return b;
    }

    private Button ModeChip(string label, string mode)
    {
        bool active = _catalogMode == mode;
        var b = new Button
        {
            Content = label,
            MinWidth = 0,
            Padding = new Thickness(13, 7, 13, 7),
            Margin = new Thickness(0, 0, 8, 8),
            Background = active ? (Brush)FindResource("Pressed") : (Brush)FindResource("Panel2"),
            BorderBrush = active ? (Brush)FindResource("AccentLine") : (Brush)FindResource("Line")
        };
        b.Click += (_, _) =>
        {
            _catalogMode = mode;
            ShowCatalog();
        };
        return b;
    }

    private UIElement CoreCatalogGrid()
    {
        var grid = TileGrid(300, 0);
        grid.Margin = new Thickness(0, 15, -TileGap, -TileGap);
        foreach (var entry in CoreList)
        {
            bool manual = CoreIsManual(entry.Name);
            string url = entry.Url;

            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            body.Children.Add(TileTitle(entry.Name));
            body.Children.Add(TileText(entry.Edition + " · " + entry.Note));
            body.Children.Add(TileActions(Btn(manual ? "Страница проекта ↗" : "Открыть источник ↗",
                (_, _) => OpenUrl(url))));
            grid.Children.Add(Tile(body, 140));
        }
        return grid;
    }

    private static int LoaderIndexOf(string? value)
    {
        for (int i = 0; i < ModLoaders.Length; i++)
            if (ModLoaders[i].Value == value) return i;
        return 0;
    }

    private Border ModFilterCard()
    {
        var panel = new StackPanel();
        bool datapack = _modKind == "datapack";

        // MinHeight, а не Height: фиксированная высота в 36px меньше, чем требует сама строка
        // текста (13px Segoe UI занимает ~23.6px плюс вертикальные отступы), и верх букв обрезался.
        var queryBox = new TextBox { Text = _modQuery, MinWidth = 240, MinHeight = 36 };
        Placeholder.SetText(queryBox, datapack ? "Название датапака" : "Название мода");
        var loaderBox = new ComboBox
        {
            ItemsSource = ModLoaders.Select(l => l.Label).ToList(),
            SelectedIndex = LoaderIndexOf(_modLoader),
            MinWidth = 170,
            MinHeight = 36
        };

        // Типы проектов у Modrinth — mod и datapack; отдельного типа «карта» в API нет.
        // Плагины там же, где и моды, отличаются только категорией загрузчика.
        // На CurseForge этот же выбор превращается в раздел: mods (classId 6) или data-packs (6945).
        var kindBox = new ComboBox
        {
            ItemsSource = new List<string> { "Моды", "Датапаки" },
            MinWidth = 160,
            MinHeight = 36
        };
        kindBox.SelectedIndex = datapack ? 1 : 0;
        // Обработчик вешаем после SelectedIndex: иначе перестройка страницы случится ещё до того,
        // как карточка собрана.
        kindBox.SelectionChanged += (_, _) =>
        {
            _modKind = kindBox.SelectedIndex == 1 ? "datapack" : "mod";
            ShowCatalog();
        };

        // Modrinth для датапаков фильтр по загрузчику не понимает: он отсёк бы весь результат.
        loaderBox.IsEnabled = !datapack;
        if (datapack) loaderBox.ToolTip = "Датапаки не зависят от загрузчика — ищем без этого фильтра.";

        var labels = new List<string> { "Версия не важна" };
        labels.AddRange(_modVersionIds);
        var versionBox = new ComboBox { ItemsSource = labels, MinWidth = 180, MinHeight = 36 };
        string? wantedVersion = _modVersion;
        versionBox.SelectedIndex = wantedVersion != null && labels.Contains(wantedVersion) ? labels.IndexOf(wantedVersion) : 0;
        if (_modVersionIds.Count == 0) LoadModVersionOptions(versionBox);

        if (_modTarget == null || !profiles.Any(p => p.Name == _modTarget))
            _modTarget = profiles.FirstOrDefault()?.Name;
        var names = profiles.Select(p => p.Name).ToList();
        var targetBox = new ComboBox { ItemsSource = names, MinWidth = 200, MinHeight = 36 };
        if (names.Count > 0)
        {
            int index = names.IndexOf(_modTarget ?? "");
            targetBox.SelectedIndex = index >= 0 ? index : 0;
        }

        var row = new WrapPanel();
        row.Children.Add(LabeledField("Что искать", kindBox));
        row.Children.Add(LabeledField("Поиск", queryBox));
        row.Children.Add(LabeledField("Загрузчик", loaderBox));
        row.Children.Add(LabeledField("Версия Minecraft", versionBox));
        row.Children.Add(LabeledField("Устанавливать в сборку", targetBox));
        panel.Children.Add(row);

        // Подсказка под строкой фильтров: placeholderInside поля исчезает при наборе, а эта остаётся.
        panel.Children.Add(new TextBlock
        {
            Text = datapack
                ? "Датапаки и на Modrinth, и на CurseForge ищутся без привязки к загрузчику. Файл .zip ляжет в папку datapacks текущего мира — мир берём из level-name в server.properties."
                : "Введи название, которое помнишь, и попробуем угадать. Источник (Modrinth или CurseForge) переключается вверху страницы. Плагины для Paper и Spigot на Modrinth лежат среди модов — ищи их выбором загрузчика.",
            FontSize = 11,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        });

        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(Btn(_catalogSource == "modrinth" ? "Найти на Modrinth" : "Найти на CurseForge",
            (_, _) => SearchMods(queryBox, loaderBox, versionBox, targetBox), true));
        buttons.Children.Add(Btn("Карта из zip…", (_, _) => InstallMapFromZip(targetBox)));
        if (names.Count == 0)
            buttons.Children.Add(Txt("Сборки ещё не созданы — сделай их в Библиотеке.", 12, false, WarnBrush));
        panel.Children.Add(buttons);

        queryBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) SearchMods(queryBox, loaderBox, versionBox, targetBox);
        };

        return Card(panel, new Thickness(0));
    }

    private StackPanel LabeledField(string label, UIElement control)
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
        box.Children.Add(Txt(label, 11, true, (Brush)FindResource("Muted")));
        box.Children.Add(control);
        return box;
    }

    // Пошаговая подсказка: нумерованные строки в отдельной плашке, чтобы они не сливались с полем ввода.
    private Border HintCard(string title, params string[] lines)
    {
        var panel = new StackPanel();
        panel.Children.Add(Txt(title, 12, true, (Brush)FindResource("AccentText")));
        foreach (var line in lines)
        {
            panel.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 11.5,
                Foreground = (Brush)FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        return new Border
        {
            Background = (Brush)FindResource("Panel2"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 14, 13),
            Margin = new Thickness(0, 2, 0, 16),
            Child = panel
        };
    }

    // Список версий Minecraft берётся из того же манифеста Mojang, что и форма конфигурации.
    private async void LoadModVersionOptions(ComboBox box)
    {
        try
        {
            var result = await MinecraftVersionList.LoadAsync(dataDir);
            var ids = result.Versions.Where(v => v.Kind == "release").Select(v => v.Id).ToList();
            if (_modVersionIds.Count > 0) return;
            _modVersionIds = ids;

            var labels = new List<string> { "Версия не важна" };
            labels.AddRange(ids);
            box.ItemsSource = labels;
            string? wanted = _modVersion;
            box.SelectedIndex = wanted != null && labels.Contains(wanted) ? labels.IndexOf(wanted) : 0;
        }
        catch
        {
            // Без списка всё равно можно искать: фильтр «Версия не важна» остаётся активным.
        }
    }

    private async void SearchMods(TextBox query, ComboBox loader, ComboBox version, ComboBox target)
    {
        _modQuery = query.Text.Trim();
        _modLoader = loader.SelectedIndex >= 0 ? ModLoaders[loader.SelectedIndex].Value : null;
        _modVersion = version.SelectedIndex > 0 ? version.SelectedItem as string : null;
        _modTarget = target.SelectedItem as string ?? _modTarget;

        _cards = new();
        string sourceName = _catalogSource == "modrinth" ? "Modrinth" : "CurseForge";
        _modStatus = string.IsNullOrWhiteSpace(_modQuery)
            ? (_modKind == "datapack" ? "Введи название датапака." : "Введи название мода — например «sodium» или «create».")
            : $"Ищу «{_modQuery}» на {sourceName}…";
        ShowCatalog();

        if (string.IsNullOrWhiteSpace(_modQuery)) return;

        try
        {
            if (_catalogSource == "modrinth")
            {
                var projects = await Modrinth.SearchAsync(_modQuery, _modLoader, _modVersion, _modKind);
                _cards = projects
                    .Select(p => new CatalogCard("modrinth", p.Slug, p.Title, p.Author, p.Description,
                        p.Downloads, "", Modrinth.ResolveIconUrl(p.IconUrl), _modKind))
                    .ToList();
            }
            else
            {
                var mods = await CurseForge.SearchAsync(
                    _modQuery, _modVersion, settings.CurseForgeApiKey, _modKind == "datapack");
                _cards = mods
                    .Select(m => new CatalogCard("curseforge", m.Id.ToString(), m.Name, m.Author,
                        m.Summary.Length > 0 ? m.Summary
                            : m.IsAvailable ? "Проект доступен для скачивания." : "CurseForge пометил проект как недоступный.",
                        m.Downloads, m.WebsiteUrl, m.LogoUrl, _modKind))
                    .ToList();
            }

            _modStatus = _cards.Count == 0
                ? $"{sourceName} ничего не нашёл под эти фильтры. Попробуй другой запрос или сбрось версию."
                : $"Найдено: {_cards.Count} · {sourceName}.";
        }
        catch (Exception ex)
        {
            _modStatus = sourceName + " недоступен: " + ex.Message;
        }

        ShowCatalog();
    }

    private UIElement ModResultsGrid()
    {
        if (_cards.Count == 0) return new StackPanel();

        var grid = TileGrid(340, 0);
        grid.Margin = new Thickness(0, 15, -TileGap, -TileGap);
        foreach (var card in _cards) grid.Children.Add(ModCard(card));
        return grid;
    }

    private Border ModCard(CatalogCard card)
    {
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Top };

        // Шапка карточки: иконка мода слева, название и автор справа.
        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        string author = string.IsNullOrWhiteSpace(card.Author) ? card.Source : card.Author;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(TileTitle(card.Title));
        text.Children.Add(Txt($"{author} · {card.Downloads:N0} загрузок", 12, false, (Brush)FindResource("Muted")));
        Grid.SetColumn(text, 1);

        head.Children.Add(ModIcon(card));
        head.Children.Add(text);
        body.Children.Add(head);
        body.Children.Add(TileText(card.Description));

        var targetBuild = profiles.FirstOrDefault(p => p.Name == _modTarget);
        var statusText = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Text = targetBuild == null
                ? "Сначала создай сборку в Библиотеке и выбери её в списке выше."
                : $"Файл ляжет в {DestinationFolder(BuildChecker.ResolveFolder(targetBuild, dataDir), card.Kind)}"
        };
        body.Children.Add(statusText);

        var buttons = new UniformGrid
        {
            Columns = string.IsNullOrEmpty(card.PageUrl) ? 1 : 2,
            Margin = new Thickness(0, 10, -8, 0)
        };

        var install = Btn("Скачать", (_, _) => InstallMod(card, statusText), true);
        install.MinWidth = 0;
        install.MinHeight = 36;
        install.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(install);

        if (!string.IsNullOrEmpty(card.PageUrl))
        {
            var page = Btn("Страница ↗", (_, _) => OpenUrl(card.PageUrl));
            page.MinWidth = 0;
            page.MinHeight = 36;
            page.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(page);
        }

        body.Children.Add(buttons);

        return Tile(body, 210);
    }

    // Иконка мода: картинка грузится асинхронно, а до неё (и если её вовсе нет) под картинкой
    // остаётся заглушка с первой буквой названия — карточка не «прыгает» по высоте.
    private FrameworkElement ModIcon(CatalogCard card)
    {
        var placeholder = new Border
        {
            Background = (Brush)FindResource("Panel2"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Child = new TextBlock
            {
                Text = card.Title.Trim().Length > 0 ? card.Title.Trim()[..1].ToUpperInvariant() : "?",
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Muted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        // Скругляем геометрией самой картинки: угол тогда точно совпадает с углом заглушки под ней.
        var image = new Image
        {
            Width = 50,
            Height = 50,
            Stretch = Stretch.UniformToFill,
            Clip = new RectangleGeometry(new Rect(0, 0, 50, 50), 11, 11)
        };
        var stack = new Grid { Width = 50, Height = 50, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(placeholder);
        stack.Children.Add(new Border { CornerRadius = new CornerRadius(11), Child = image });
        LoadIcon(image, placeholder, card.IconUrl);
        return stack;
    }

    private async void LoadIcon(Image target, UIElement placeholder, string url)
    {
        var (source, error) = await RemoteImage.GetAsync(url);
        if (source == null)
        {
            // Без ошибки значит картинки просто нет у проекта — заглушка в этом случае нормальна.
            if (error != null && placeholder is FrameworkElement holder) holder.ToolTip = error;
            return;
        }

        target.Source = source;
        placeholder.Visibility = Visibility.Collapsed;
    }

    // Куда ляжет файл: моды — в mods, датапаки — внутрь папки мира. Мир берём из level-name,
    // потому что сервер создаёт папку именно так, а «world» — только значение по умолчанию.
    private static string DestinationFolder(string buildFolder, string kind)
        => kind == "datapack"
            ? Path.Combine(buildFolder, ServerProperties.WorldFolder(buildFolder), "datapacks")
            : Path.Combine(buildFolder, "mods");

    private async void InstallMod(CatalogCard card, TextBlock statusText)
    {
        var target = profiles.FirstOrDefault(p => p.Name == _modTarget);
        if (target == null)
        {
            statusText.Text = "Сборка не выбрана — создай её в Библиотеке и выбери в списке выше.";
            return;
        }

        string installFolder = DestinationFolder(BuildChecker.ResolveFolder(target, dataDir), card.Kind);
        statusText.Text = $"Ищу файлы {card.Title} под {(_modVersion ?? "любую версию Minecraft")}…";

        try
        {
            if (card.Source == "modrinth")
                await InstallFromModrinth(card, installFolder, statusText);
            else
                await InstallFromCurseForge(card, installFolder, statusText);
        }
        catch (OperationCanceledException)
        {
            statusText.Text = "Загрузка отменена.";
        }
        catch (Exception ex)
        {
            statusText.Text = "Ошибка: " + ex.Message;
        }
    }

    private async Task InstallFromModrinth(CatalogCard card, string installFolder, TextBlock statusText)
    {
        // У датапаков версия не привязана к загрузчику: с фильтром запрос вернул бы пустой список.
        string? loader = card.Kind == "datapack" ? null : _modLoader;
        var versions = await Modrinth.GetVersionsAsync(card.Id, _modVersion, loader);
        if (versions.Count == 0)
        {
            statusText.Text = "Под выбранный загрузчик и версию Minecraft файлов нет. Сбрось фильтры и попробуй снова.";
            return;
        }

        var version = versions[0];
        var file = version.Files!.FirstOrDefault(f => f.Primary) ?? version.Files![0];
        statusText.Text = $"Беру {file.Filename} (версия {version.VersionNumber}).";

        await DownloadWithDialog(card.Title, file.Filename, file.Size, installFolder,
            (progress, token) => Modrinth.DownloadAsync(file, installFolder, progress, token), statusText);
    }

    private async Task InstallFromCurseForge(CatalogCard card, string installFolder, TextBlock statusText)
    {
        if (!int.TryParse(card.Id, out int modId))
        {
            statusText.Text = "CurseForge вернул непонятный идентификатор мода.";
            return;
        }

        var files = await CurseForge.GetFilesAsync(modId, _modVersion, settings.CurseForgeApiKey);
        if (files.Count == 0)
        {
            statusText.Text = _modVersion == null
                ? "У мода нет файлов для скачивания."
                : $"Под Minecraft {_modVersion} файлов нет — сбрось версию в фильтрах.";
            return;
        }

        // CurseForge фильтрует по версии Minecraft, но не по загрузчику в этом запросе,
        // поэтому загрузчик ищем в метках самого файла. Если меток нет — берём свежий и предупреждаем.
        // У датапаков загрузчика нет как класса — с ним запрос отсёк бы всё.
        string? loader = card.Kind == "datapack" ? null : _modLoader;
        var file = files.FirstOrDefault(f => loader == null || f.Loaders.Count == 0 ||
            f.Loaders.Any(l => string.Equals(l, loader, StringComparison.OrdinalIgnoreCase))) ?? files[0];

        bool guessed = loader != null && file.Loaders.Count == 0;
        statusText.Text = $"Беру {file.FileName}." +
            (guessed ? " CurseForge не указал загрузчик этого файла — проверь, что он подходит твоему ядру." : "");

        await DownloadWithDialog(card.Title, file.FileName, file.Length, installFolder,
            (progress, token) => CurseForge.DownloadAsync(file, installFolder, progress, settings.CurseForgeApiKey, token),
            statusText);
    }

    private async Task DownloadWithDialog(string title, string fileName, long declaredSize, string folder,
        Func<IProgress<(long Done, long Total)>, CancellationToken, Task<string>> download, TextBlock statusText)
    {
        var cts = new CancellationTokenSource();

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = declaredSize > 0 ? declaredSize : 1,
            Margin = new Thickness(0, 14, 0, 8),
            Visibility = declaredSize > 0 ? Visibility.Visible : Visibility.Collapsed
        };
        var info = Txt("Готовлю загрузку…", 12, false, (Brush)FindResource("Muted"));

        var progress = new Progress<(long Done, long Total)>(p =>
        {
            if (p.Total > 0) bar.Value = Math.Min(p.Done, p.Total);
            info.Text = $"{SystemInfo.FormatSize(p.Done)} из {(p.Total > 0 ? SystemInfo.FormatSize(p.Total) : "неизвестного объёма")} · {fileName}";
        });

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Txt("ЗАГРУЗКА", 11, true, (Brush)FindResource("Accent")));
        panel.Children.Add(Txt(title, 18, true));
        panel.Children.Add(Txt($"Папка: {folder}", 11, false, (Brush)FindResource("Muted")));
        panel.Children.Add(bar);
        panel.Children.Add(info);

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Btn("Отменить", (_, _) => cts.Cancel()));
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = "Загрузка мода",
            Width = 540,
            Height = 270,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("Bg"),
            Foreground = TileForeground,
            Content = panel
        };
        Dialog.DarkCaption(win);
        win.Show();
        // Закрыли окно крестиком — прерываем загрузку, чтобы не остался недокачанный файл.
        win.Closing += (_, _) => cts.Cancel();

        try
        {
            string saved = await download(progress, cts.Token);
            statusText.Text = "Файл сохранён: " + saved;
            if (Dialog.Confirm($"Файл установлен:\n{saved}\n\nОткрыть папку {Path.GetFileName(folder)}?", "Готово"))
                OpenUrl(folder);
        }
        finally
        {
            win.Close();
            cts.Dispose();
        }
    }

    // Карта — это папка мира, а не файл в mods: архив распаковываем рядом со server.properties
    // и подписываем новую папку в level-name, чтобы следующий запуск взял именно её.
    private async void InstallMapFromZip(ComboBox target)
    {
        var profile = profiles.FirstOrDefault(p => p.Name == (target.SelectedItem as string ?? _modTarget));
        if (profile == null)
        {
            _modStatus = "Карту некуда класть: сначала создай сборку в Библиотеке и выбери её в списке выше.";
            ShowCatalog();
            return;
        }
        if (BuildChecker.IsBedrock(profile))
        {
            _modStatus = "Для Bedrock-сборки карту так не поставить: у неё мир лежит в leveldb и распространяется файлом .mcworld. Распакуй его в папку сборки руками.";
            ShowCatalog();
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Архив с картой",
            Filter = "Архив карты (*.zip)|*.zip|Все файлы|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        string buildFolder = BuildChecker.ResolveFolder(profile, dataDir);
        _modStatus = $"Распаковываю {Path.GetFileName(dialog.FileName)} в {buildFolder}…";
        ShowCatalog();

        try
        {
            var installed = await MapInstaller.InstallAsync(dialog.FileName, buildFolder, CancellationToken.None);
            _modStatus = $"Карта «{installed.LevelName}»: распаковано файлов {installed.Files} в {installed.Folder}. " +
                         "level-name в server.properties обновлён — со следующего запуска сервер откроет этот мир.";
        }
        catch (Exception ex)
        {
            _modStatus = "Карту не установили: " + ex.Message;
        }

        ShowCatalog();
    }

    private void EditProfile(ServerProfile? profile)
    {
        bool isNew = profile == null;
        _editing = true;
        var p = profile == null ? new ServerProfile() : new ServerProfile
        {
            Name = profile.Name, Edition = profile.Edition, Version = profile.Version,
            Core = profile.Core, Port = profile.Port, MemoryGb = profile.MemoryGb, Folder = profile.Folder,
            Motd = profile.Motd, MaxPlayers = profile.MaxPlayers, OnlineMode = profile.OnlineMode,
            ViewDistance = profile.ViewDistance, Difficulty = profile.Difficulty, AllowEnd = profile.AllowEnd,
            AutoBackup = profile.AutoBackup, BackupKeep = profile.BackupKeep,
            JavaPath = profile.JavaPath,
            AutoRestart = profile.AutoRestart, RestartAt = profile.RestartAt,
            IconFile = profile.IconFile
        };

        SetHeader("КОНФИГУРАЦИЯ", isNew ? "Новая конфигурация" : "Настройки сборки", "Изменения сохраняются локально на этом компьютере.");
        var form = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        var name = Field(form, "Название", p.Name, "например, Выживание");
        var edition = Combo(form, "Платформа", new[] { "Java", "Bedrock" }, p.Edition);
        var (version, versionStatus) = VersionField(form, edition, p.Version);
        var core = CoreCombo(form, p.Edition, p.Core);
        var port = Field(form, "Порт", p.Port.ToString(), "25565",
            "Свободный порт на этом компьютере. Два сервера на одном порту не запустятся.");
        var memory = MemorySlider(form, p.MemoryGb);
        var folder = Field(form, "Папка сервера (необязательно)", p.Folder, "Оставь пустым — создадим сами",
            $"Без папки сервер попадёт в {Path.Combine(dataDir, "instances", "имя конфигурации")}.");
        var java = JavaField(form, edition, version, p.JavaPath);

        // Что из этого попадёт в server.properties. Пустые поля = «файл не трогаем»:
        // правки, сделанные вручную, переживать перезапуск не должны.
        form.Children.Add(FieldHint(
            "Ниже — то, что лаунчер допишет в server.properties. У Java-сборки это происходит перед " +
            "запуском; у Bedrock — сразу при сохранении: запуска Bedrock в панели нет, и подставить " +
            "эти значения было бы некому. Файл правим только показав, какой ключ чем был и чем станет."));
        var motd = Field(form, "Название в списке серверов (motd)", p.Motd, "например, Выживание 24/7",
            "Сервер показывает в списке именно motd — отдельного «имени» у него нет. Пусто: при первом " +
            "запуске подставим название конфигурации, а если файл уже есть — оставим как есть.");
        var maxPlayers = Field(form, "Максимум игроков (max-players)",
            p.MaxPlayers == null ? "" : p.MaxPlayers.Value.ToString(), "например, 20",
            "Пусто — оставить столько, сколько уже записано в файле. Сервер читает эти ключи при старте, " +
            "так что изменения применяются со следующего запуска.");

        const string keep = "Не менять — как в файле";
        const string licensed = "Включена — вход только с лицензии";
        const string offline = "Выключена — вход без лицензии";
        var online = Combo(form, "Проверка лицензии (online-mode)", new[] { keep, licensed, offline },
            p.OnlineMode == null ? keep : (p.OnlineMode.Value ? licensed : offline));

        var view = Field(form, "Прорисовка мира (view-distance)",
            p.ViewDistance == null ? "" : p.ViewDistance.Value.ToString(), "например, 8",
            "Сколько чанков сервер успевает отдать игроку вокруг него. Меньше — легче серверу, но туман " +
            "подступает ближе; vanilla принимает 2–32, Bedrock — от 5. Пусто — оставить то, что уже " +
            "записано в файле.");

        // Список подписан по-русски, а в файл уходит голое слово, которое ждёт server.properties.
        // Неизвестное значение (например, вписанное руками) трактуется как «не менять»: панель не
        // начинает считать «hardest» валидной сложностью только потому, что она где-то записана.
        const string diffKeep = "Не менять — как в файле";
        const string diffPeaceful = "Мирная — враждебных мобов нет";
        const string diffEasy = "Лёгкая — мобы слабее, от голода не умереть";
        const string diffNormal = "Обычная";
        const string diffHard = "Тяжёлая — голод убивает, мобы бьют больнее";
        var difficulty = Combo(form, "Сложность (difficulty)",
            new[] { diffKeep, diffPeaceful, diffEasy, diffNormal, diffHard },
            p.Difficulty switch
            {
                "peaceful" => diffPeaceful,
                "easy" => diffEasy,
                "normal" => diffNormal,
                "hard" => diffHard,
                _ => diffKeep
            });
        form.Children.Add(FieldHint(
            "Сервер берёт сложность из server.properties и применяет её ко всем своим мирам при " +
            "старте, так что передумать можно в любой момент — со следующего запуска. На уже " +
            "созданных мобах и предметах смена не сказывается: она влияет на спавн и на урон " +
            "дальше, а не переписывает то, что в мире лежит."));

        const string endKeep = "Не менять — как в bukkit.yml";
        const string endOpen = "Открыт — играть как обычно";
        const string endClosed = "Закрыт — в Энд не попасть";
        var end = Combo(form, "Измерение Энд (allow-end)", new[] { endKeep, endOpen, endClosed },
            p.AllowEnd == null ? endKeep : (p.AllowEnd.Value ? endOpen : endClosed));
        end.IsEnabled = BukkitYaml.Supports(SelectedCore(core));
        core.SelectionChanged += (_, _) => end.IsEnabled = BukkitYaml.Supports(SelectedCore(core));
        form.Children.Add(FieldHint(
            "Ключ allow-end есть только у ядер на Bukkit — Paper, Purpur, Folia и Leaf: сервер читает " +
            "bukkit.yml при старте и не пускает игроков в Энд. Vanilla, Fabric, Forge и NeoForge этот " +
            "файл не читают, поэтому для них поле неактивно — закрывать Энд нечем, и обещать кнопку, " +
            "которая ничего не делает, не будем."));

        // Копия мира перед запуском — страховка от неудачного обновления мода или испорченной
        // генерации. Выключена по умолчанию: мир бывает на десятки гигабайт, и молча снимать его
        // перед каждым стартом — значит каждый раз ждать и занимать диск.
        const string backupOff = "Не копировать — запуск сразу";
        const string backupOn = "Копировать мир перед каждым запуском";
        var backup = Combo(form, "Резервная копия мира", new[] { backupOff, backupOn },
            p.AutoBackup ? backupOn : backupOff);
        var copies = Field(form, "Хранить копий", p.BackupKeep.ToString(), "3",
            $"Копии лежат в {Path.Combine(dataDir, "backups", "имя сборки")} — не внутри сборки, чтобы сервер " +
            "не принял копию за мир. Сверх указанного числа старые копии панель удаляет сама; откатиться " +
            "на любую из них можно в меню «⋯» этой сборки → «Резервные копии мира».");

        // Авто-перезапуск нужен серверу, который стоит без присмотра: друг заходит вечером, а
        // окно с упавшим сервером никто не видит. Порог попыток жёсткий — сборка, которая не
        // может загрузить мир, крутила бы этот круг бесконечно.
        const string restartOff = "Не перезапускать — сервер живёт, пока его не остановят";
        const string restartOn = "Поднимать заново, если сервер упал сам";
        var restart = Combo(form, "Авто-перезапуск после падения", new[] { restartOff, restartOn },
            p.AutoRestart ? restartOn : restartOff);
        form.Children.Add(FieldHint(
            "Падением считается только то, что сервер завершился сам: кнопка «Остановить» и закрытие " +
            "панели его не вызывают. Первая попытка через 5 секунд, вторая через 15, третья через 45. " +
            "После трёх падений подряд панель перестаёт поднимать сервер и пишет в журнал причину — " +
            "дальше разбирает человек. Если сервер отработал дольше двух минут, счётчик падений обнуляется."));

        var restartAt = Field(form, "Плановый перезапуск (ЧЧ:ММ)", p.RestartAt, "например, 04:00",
            "Раз в сутки панель сохранит мир, остановит сервер и поднимет его заново — полезно для " +
            "сборки, которая к вечеру начинает тормозить. Пусто — выключено. Часы работают, только " +
            "пока панель открыта: закрытая панель сервер не будит.");

        form.Children.Add(Txt("Иконка сервера (server-icon.png)", 12, true, (Brush)FindResource("Muted")));        var iconRow = new DockPanel { Margin = new Thickness(0, 6, 0, 7) };
        var icon = new TextBox { Text = p.IconFile, MinHeight = 36, Margin = new Thickness(0, 0, 8, 0) };
        Placeholder.SetText(icon, "пусто — иконку не меняем");
        var iconPick = Btn("Выбрать картинку…", (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Иконка сервера",
                Filter = "Картинки|*.png;*.jpg;*.jpeg;*.bmp|Все файлы|*.*"
            };
            if (dialog.ShowDialog(this) == true) icon.Text = dialog.FileName;
        });
        DockPanel.SetDock(iconPick, Dock.Right);
        iconRow.Children.Add(iconPick);
        iconRow.Children.Add(icon);
        form.Children.Add(iconRow);
        form.Children.Add(FieldHint(
            "При запуске лаунчер обрежет выбранную картинку до квадрата 64×64 и положит рядом с " +
            "server.properties. Если картинка не менялась с последнего запуска, файл не перезаписываем."));

        // Смена платформы меняет и список ядер, и источник версий.
        edition.SelectionChanged += (_, _) => ApplyEdition(edition, version, versionStatus, core);

        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(Btn("Сохранить", (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { Dialog.Info("Укажи название конфигурации.", "Проверь ввод"); return; }
            if (!int.TryParse(port.Text, out int portNum) || portNum < 1 || portNum > 65535) { Dialog.Info("Порт должен быть от 1 до 65535.", "Проверь ввод"); return; }
            if (string.IsNullOrWhiteSpace(version.Text)) { Dialog.Info("Укажи версию Minecraft.", "Проверь ввод"); return; }
            int? players = null;
            if (!string.IsNullOrWhiteSpace(maxPlayers.Text))
            {
                if (!int.TryParse(maxPlayers.Text.Trim(), out int parsed) || parsed < 1)
                {
                    Dialog.Info("Максимум игроков — целое число от 1. Или оставь поле пустым.", "Проверь ввод");
                    return;
                }
                players = parsed;
            }

            int? distance = null;
            if (!string.IsNullOrWhiteSpace(view.Text))
            {
                if (!int.TryParse(view.Text.Trim(), out int parsed) || parsed is < 2 or > 32)
                {
                    Dialog.Info("Прорисовка — целое число от 2 до 32. Или оставь поле пустым.", "Проверь ввод");
                    return;
                }

                // У Bedrock нижняя граница другая: 5, а не 2 (Java-значения 2–4 он не принимает),
                // и проверка здесь, а не в BedrockProperties, — чтобы человек увидел её в поле,
                // а не в списке правок файла, куда значение уже приехало бы.
                if (BuildChecker.IsBedrock(p) && parsed < 5)
                {
                    Dialog.Info("Bedrock-сервер принимает прорисовку от 5 до 32 — меньше он просто " +
                        "не понимает. Или оставь поле пустым, тогда в файле останется то, что в нём есть.",
                        "Проверь ввод");
                    return;
                }

                distance = parsed;
            }

            p.Name = name.Text.Trim();
            p.Edition = EditionOf(edition);
            p.Version = version.Text.Trim();
            p.Core = (core.SelectedItem as CoreOption)?.Name ?? CoresFor(p.Edition).First();
            p.Port = portNum;
            p.MemoryGb = (int)Math.Round(memory.Value);
            p.Folder = folder.Text.Trim();
            p.JavaPath = (java.SelectedItem as JavaOption)?.Path ?? "";
            p.Motd = motd.Text.Trim();
            p.MaxPlayers = players;
            p.ViewDistance = distance;
            p.Difficulty = (string?)difficulty.SelectedItem switch
            {
                diffPeaceful => "peaceful",
                diffEasy => "easy",
                diffNormal => "normal",
                diffHard => "hard",
                _ => ""
            };
            p.AllowEnd = ((string?)end.SelectedItem) switch
            {
                endOpen => true,
                endClosed => false,
                _ => null
            };
            if (!int.TryParse(copies.Text.Trim(), out int stored) || stored is < 1 or > 20)
            {
                Dialog.Info("Сколько копий хранить — целое число от 1 до 20.", "Проверь ввод");
                return;
            }

            p.OnlineMode = ((string?)online.SelectedItem) switch
            {
                licensed => true,
                offline => false,
                _ => null
            };
            p.AutoBackup = (string?)backup.SelectedItem == backupOn;
            p.BackupKeep = stored;

            // Время берём только то, которое панель сумеет растолковать суточным кругом:
            // «25:00» TimeSpan разберёт как сутки и час, «08:00:30» — с секундами, всё равно
            // ненужными, а «восьмь» просто не разберётся. Пустая строка — план выключен.
            string when = restartAt.Text.Trim();
            TimeSpan planned = TimeSpan.Zero;
            if (when.Length > 0)
            {
                if (!TimeSpan.TryParse(when, out TimeSpan parsed) ||
                    parsed < TimeSpan.Zero || parsed.Days > 0 || parsed.Seconds > 0)
                {
                    Dialog.Info("Плановый перезапуск — время в формате ЧЧ:ММ, например 04:00. " +
                        "Или оставь поле пустым, если перезапуск по часам не нужен.", "Проверь ввод");
                    return;
                }

                planned = parsed;
            }

            p.AutoRestart = (string?)restart.SelectedItem == restartOn;
            p.RestartAt = when.Length == 0 ? "" : planned.ToString(@"hh\:mm");
            p.IconFile = icon.Text.Trim();

            if (isNew) profiles.Add(p);
            else
            {
                int idx = profiles.IndexOf(profile!);
                if (idx >= 0) profiles[idx] = p;
                else profiles.Add(p);
            }

            SaveProfiles();

            // У Bedrock-сборки запуска в панели нет, поэтому её свойства применяются прямо здесь:
            // значение, которое некому подставить при старте, иначе осталось бы висеть в
            // конфигурации и никогда не попало бы в server.properties.
            if (BuildChecker.IsBedrock(p)) ApplyBedrockProperties(p);

            ShowLibrary();
        }, true));
        buttons.Children.Add(Btn("Отмена", (_, _) => ShowLibrary()));
        form.Children.Add(buttons);
        SetPage(Card(form, new Thickness(0)), "edit");
    }

    // Bedrock правим по той же причине, по какой Java не трогаем: у него момента «перед запуском»
    // в панели просто нет. Файла нет — не пишем: его создаёт сам bedrock_server при первом старте,
    // и до этого момента применять нечего. Одинаковых значений тоже не показываем: пустой список —
    // это «файл уже такой, каким его хочет конфигурация», а не ошибка.
    private void ApplyBedrockProperties(ServerProfile p)
    {
        string folder = BuildChecker.ResolveFolder(p, dataDir);
        (bool found, List<BedrockProperties.Change> changes) = BedrockProperties.Diff(p, folder);
        if (!found || changes.Count == 0) return;

        string preview = string.Join("\n", changes.Select(c =>
            $"• {c.Key}\n    было:  {c.Was}\n    станет: {c.Will}"));

        if (!Dialog.Confirm(
                $"Сборка «{p.Name}»:\n{Path.Combine(folder, "server.properties")}\n\n" +
                $"Ключей на правку: {changes.Count}.\n{preview}\n\n" +
                "bedrock_server прочитает это при следующем запуске. Остальные строки и комментарии " +
                "файла не трогаем. Перезаписать?", "Свойства Bedrock"))
        {
            return;
        }

        try
        {
            BedrockProperties.Apply(p, folder);
            Dialog.Info("server.properties обновлён: эти значения bedrock_server возьмёт при " +
                "следующем запуске.", "Свойства Bedrock");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialog.Info($"Файл не перезаписался:\n{ex.Message}\n\nКонфигурация при этом сохранена — " +
                "сохрани настройки ещё раз, когда bedrock_server будет закрыт.", "Свойства Bedrock");
        }
    }

    private void ApplyEdition(ComboBox edition, ComboBox version, TextBlock versionStatus, ComboBox core)
    {
        string target = EditionOf(edition);
        bool bedrock = target == "Bedrock";
        string previous = (core.SelectedItem as CoreOption)?.Name ?? "";

        // Ядро другой платформы в новый список не переносим: у Bedrock и Java свои программы,
        // и чужое имя в списке было бы обещанием того, чего панель не сделает.
        // «Своё ядро» — исключение: оно есть у обеих платформ и переносится, потому что выбор
        // «файл кладу сам» от смены платформы не меняется.
        bool keep = CoreBelongs(target, previous);
        SetCoreOptions(core, target, keep ? previous : null);

        if (bedrock)
        {
            // Официального JSON-манифеста для Bedrock нет, поэтому список не запрашиваем.
            version.ItemsSource = null;
            versionStatus.Text = BedrockVersionHint;
        }
        else
        {
            LoadVersions(version, versionStatus, false, edition);
        }
    }

    // Поле «Ядро»: подписывает те сборки, которые панель ставит сама, теми, где файл нужно
    // достать руками. Хранится при этом голое имя — по нему InstallCore решает, чем устанавливать.
    private ComboBox CoreCombo(Panel parent, string edition, string selected)
    {
        parent.Children.Add(Txt("Ядро", 12, true, (Brush)FindResource("Muted")));
        var box = new ComboBox { Margin = new Thickness(0, 6, 0, 13), MinWidth = 450, MinHeight = 36 };
        SetCoreOptions(box, edition, selected);
        parent.Children.Add(box);

        var hint = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 13),
            Visibility = Visibility.Collapsed
        };
        parent.Children.Add(hint);
        box.SelectionChanged += (_, _) => ShowCustomCoreHint(box, hint);
        ShowCustomCoreHint(box, hint);
        return box;
    }

    // Подсказка относится только к «Своему ядру», поэтому в остальных случаях элемент свёрнут:
    // пустая строка сдвигала бы всё поле, а не показывала бы ничего.
    private static void ShowCustomCoreHint(ComboBox core, TextBlock hint)
    {
        bool custom = CoreInstaller.IsCustom(SelectedCore(core));
        core.Margin = new Thickness(0, 6, 0, custom ? 4 : 13);
        hint.Text = custom ? CustomCoreHint : "";
        hint.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetCoreOptions(ComboBox core, string edition, string? selected)
    {
        var names = CoresFor(edition).ToList();

        // Ядро из импортированной конфигурации может не быть в списке (Spigot, своя сборка).
        // Показываем его отдельной строкой: иначе поле молча выбрало бы первое ядро, и сохранение
        // переписало бы пользовательский выбор.
        if (!string.IsNullOrWhiteSpace(selected) &&
            !names.Any(n => string.Equals(n, selected, StringComparison.OrdinalIgnoreCase)))
            names.Insert(0, selected!);

        var options = names.Select(n => new CoreOption(n, CoreIsManual(n))).ToList();
        core.ItemsSource = options;
        core.SelectedItem = options.FirstOrDefault(o => o.Name == selected) ?? options[0];
    }

    // Имя ядра без подписи «· вручную»: подпись нужна только списку, а решение о способе
    // установки панель принимает по голому имени.
    private static string SelectedCore(ComboBox core) =>
        core.SelectedItem is CoreOption option ? option.Name : core.Text ?? "";

    // Пункт списка «Java для этой сборки». Хранит путь, а не версию: версия — производная от
    // файла, и показать надо ровно то, что java ответила сейчас, а не число из конфигурации.
    private sealed class JavaOption
    {
        private JavaOption(string path, JavaInstall? install)
        {
            Path = path;
            Install = install;
            Label = path.Length == 0
                ? "Автоподбор — ближайшая подходящая под версию Minecraft"
                : install != null
                    ? $"Java {install.Version} — {install.Path}"
                    : (File.Exists(path) ? "Версию прочитать не удалось — " : "Файла нет — ") + path;
        }

        public string Path { get; }
        public JavaInstall? Install { get; }
        public bool IsAuto => Path.Length == 0;
        public string Label { get; }

        public override string ToString() => Label;

        public static JavaOption Auto { get; } = new("", null);

        public static JavaOption Of(JavaInstall install) => new(install.Path, install);

        public static JavaOption ForPath(string path) => new(path, SystemInfo.Probe(path));
    }

    // Java на сборку: список того, что панель нашла на компьютере, плюс ручной выбор файла.
    // Нужна сборкам на старых версиях — 1.12 идёт только на Java 8, и молча запускать её
    // двадцать первой панель права не имеет.
    private ComboBox JavaField(Panel form, ComboBox edition, ComboBox version, string selected)
    {
        form.Children.Add(Txt("Java для этой сборки", 12, true, (Brush)FindResource("Muted")));

        var options = new List<JavaOption> { JavaOption.Auto };
        options.AddRange(SystemInfo.JavaInstalls().Select(JavaOption.Of));

        // Путь из импортированной конфигурации или с отключённого сейчас диска может не быть в
        // найденном списке — показываем его отдельным пунктом, иначе поле молча выбрало бы
        // «Автоподбор», и сохранение переписало бы выбор человека.
        string chosen = (selected ?? "").Trim();
        if (chosen.Length > 0 &&
            !options.Any(o => string.Equals(o.Path, chosen, StringComparison.OrdinalIgnoreCase)))
            options.Add(JavaOption.ForPath(chosen));

        var box = new ComboBox { MinWidth = 450, MinHeight = 36 };
        box.ItemsSource = options;
        box.SelectedItem = options.FirstOrDefault(
            o => string.Equals(o.Path, chosen, StringComparison.OrdinalIgnoreCase)) ?? JavaOption.Auto;

        var pick = Btn("Найти java.exe…", (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Файл java.exe",
                Filter = "Java — исполняемый файл|java.exe|Все файлы|*.*",
                FileName = "java.exe",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;

            var list = (List<JavaOption>)box.ItemsSource;
            JavaOption option = list.FirstOrDefault(
                    o => string.Equals(o.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase))
                ?? JavaOption.ForPath(dialog.FileName);
            if (!list.Contains(option)) list.Add(option);

            // Пункт добавлен в уже привязанный список: без перепривязки WPF его бы не показал.
            box.ItemsSource = null;
            box.ItemsSource = list;
            box.SelectedItem = option;
        });

        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 4) };
        DockPanel.SetDock(pick, Dock.Right);
        row.Children.Add(pick);
        row.Children.Add(box);
        form.Children.Add(row);

        var note = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 13)
        };
        form.Children.Add(note);

        // Подпись меняется вместе с выбором Java, платформой и версией Minecraft: именно на их
        // пересечении видно, годится ли выбранная JDK, и узнаёт человек об этом здесь, а не в
        // отказе запуска.
        void Sync()
        {
            bool bedrock = EditionOf(edition) == "Bedrock";
            box.IsEnabled = !bedrock;
            pick.IsEnabled = !bedrock;
            note.Foreground = (Brush)FindResource("Muted");

            if (bedrock)
            {
                note.Text = "Bedrock Dedicated Server — это bedrock_server.exe, Java ему не нужна, " +
                    "поэтому на этой платформе поле неактивно. Ядра на Java для Bedrock (Nukkit) " +
                    "панель пока не запускает.";
                return;
            }

            var option = box.SelectedItem as JavaOption ?? JavaOption.Auto;
            int needed = JavaRequirement.MinMajor(version.Text);

            if (option.IsAuto)
            {
                note.Text = "Панель возьмёт из найденных ближайшую к требованию: серверу " +
                    $"{version.Text} нужна Java {needed}+. Самую свежую она не берёт — на ней старый " +
                    "сервер не запустится. JDK, поставленную уже после запуска панели, отсюда не видно: " +
                    "тогда укажи путь кнопкой «Найти java.exe…».";
                return;
            }

            if (option.Install == null)
            {
                note.Foreground = (Brush)FindResource("Warn");
                note.Text = "Файла нет на диске, или «java -version» от него не отвечает. Сохранить " +
                    $"можно, но панель не начнёт запуск и объяснит почему: серверу нужна Java {needed}+.";
                return;
            }

            if (option.Install.Major < needed)
            {
                note.Foreground = (Brush)FindResource("Warn");
                note.Text = $"Серверу {version.Text} нужна Java {needed} или новее, а эта — " +
                    $"{option.Install.Version}. На ней запуск кончится UnsupportedClassVersionError, " +
                    "поэтому панель даже не начнёт.";
                return;
            }

            note.Text = $"Эта сборка всегда стартует на {option.Install.Version}, даже когда на машине " +
                "есть новее. Установщик Forge, если он понадобится, тоже пойдёт этой Java.";
        }

        box.SelectionChanged += (_, _) => Sync();
        edition.SelectionChanged += (_, _) => Sync();
        version.SelectionChanged += (_, _) => Sync();
        // События «текст изменился» у ComboBox нет, поэтому ручной ввод (Mohist, Magma, форки)
        // ловим на уходе фокуса: человек набрал версию, щёлкнул мимо — и подпись пересчитывается.
        version.LostFocus += (_, _) => Sync();
        Sync();
        return box;
    }

    private TextBox Field(Panel parent, string label, string value) => Field(parent, label, value, null);

    // hint — серая подсказка под полем; placeholder — текст внутри пустого поля. Это разные вещи:
    // placeholder исчезает, как только пользователь начал печатать, а hint остаётся.
    private TextBox Field(Panel parent, string label, string value, string? placeholder, string? hint = null)
    {
        parent.Children.Add(Txt(label, 12, true, (Brush)FindResource("Muted")));
        // MinHeight вместо Height: строка 13px вместе с отступами требует ~38px, а фиксированные
        // 36px обрезали верх букв — выглядело так, будто текст «встал криво».
        var box = new TextBox { Text = value, Margin = new Thickness(0, 6, 0, 7), MinWidth = 450, MinHeight = 36 };
        if (placeholder != null) Placeholder.SetText(box, placeholder);
        parent.Children.Add(box);
        if (hint != null) parent.Children.Add(FieldHint(hint));
        else box.Margin = new Thickness(0, 6, 0, 13);
        return box;
    }

    private TextBlock FieldHint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = (Brush)FindResource("Muted"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 13)
    };

    private ComboBox Combo(Panel parent, string label, IEnumerable<string> options, string selected)
    {
        parent.Children.Add(Txt(label, 12, true, (Brush)FindResource("Muted")));
        var box = new ComboBox { ItemsSource = options, SelectedItem = selected, Margin = new Thickness(0, 6, 0, 13), MinWidth = 450, MinHeight = 36 };
        if (box.SelectedItem == null) box.SelectedIndex = 0;
        parent.Children.Add(box);
        return box;
    }

    // Платформу берём из выбранного пункта, а не из Text: у ComboBox без редактирования Text —
    // величина производная и обновляется не всегда вовремя, и на нём Bedrock показывал Java-ядра.
    private static string EditionOf(ComboBox? box) => box?.SelectedItem as string ?? "Java";

    // Версия берётся из манифеста Mojang, но поле остаётся редактируемым: у сборок-форков
    // (Mohist, Magma, Spigot) своих идентификаторов в списке нет.
    // Для Bedrock список не запрашивается вообще — подсказка о ручном вводе висит под полем.
    private (ComboBox Box, TextBlock Status) VersionField(Panel parent, ComboBox edition, string value)
    {
        parent.Children.Add(Txt("Версия Minecraft", 12, true, (Brush)FindResource("Muted")));

        bool bedrock = EditionOf(edition) == "Bedrock";
        var status = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("Muted"),
            Margin = new Thickness(0, 4, 0, 13),
            TextWrapping = TextWrapping.Wrap,
            Text = bedrock ? BedrockVersionHint : "Загружаю список версий Minecraft…"
        };

        var box = new ComboBox { IsEditable = true, Text = value, MinWidth = 260, MinHeight = 36 };
        Placeholder.SetText(box, "1.20.1 или 1.19.2");
        var refresh = Btn("⟳ Обновить", (_, _) =>
        {
            if (EditionOf(edition) == "Bedrock")
            {
                status.Text = BedrockVersionHint;
                return;
            }
            LoadVersions(box, status, true, edition);
        });
        refresh.MinWidth = 110;

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(refresh, Dock.Right);
        row.Children.Add(refresh);
        row.Children.Add(box);

        parent.Children.Add(row);
        parent.Children.Add(status);
        if (!bedrock) LoadVersions(box, status, false, edition);
        return (box, status);
    }

    // edition нужен, чтобы асинхронный ответ не перезаписал подсказку Bedrock: за время запроса
    // платформу успевают переключить, а статус и список версий относятся к Java.
    private async void LoadVersions(ComboBox box, TextBlock status, bool force, ComboBox? edition = null)
    {
        if (EditionOf(edition) == "Bedrock") return;
        if (force) status.Text = "Обновляю список версий Minecraft…";
        try
        {
            var result = await MinecraftVersionList.LoadAsync(dataDir, force);
            var ids = result.Versions
                .Where(v => v.Kind == "release" || (settings.ShowSnapshots && v.Kind == "snapshot"))
                .Select(v => v.Id)
                .ToList();

            if (EditionOf(edition) == "Bedrock") return;
            string typed = box.Text;
            box.ItemsSource = ids;
            box.Text = typed;
            status.Text = $"{ids.Count} версий · {result.Note}";
        }
        catch (Exception ex)
        {
            if (EditionOf(edition) == "Bedrock") return;
            status.Text = "Список версий недоступен: " + ex.Message + " Версию можно ввести вручную.";
        }
    }

    private Slider MemorySlider(Panel parent, int value)
    {
        int maxGb = MemoryLimitGb();
        int start = Math.Clamp(value, 1, maxGb);

        parent.Children.Add(Txt("Оперативная память", 12, true, (Brush)FindResource("Muted")));

        var slider = new Slider
        {
            Minimum = 1,
            Maximum = maxGb,
            Value = start,
            SmallChange = 1,
            LargeChange = 1,
            MinWidth = 320,
            VerticalAlignment = VerticalAlignment.Center
        };
        var readout = new TextBlock
        {
            Text = $"{start} ГБ",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("AccentText"),
            MinWidth = 62,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.ValueChanged += (_, _) => readout.Text = $"{(int)Math.Round(slider.Value)} ГБ";

        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(readout, Dock.Right);
        row.Children.Add(readout);
        row.Children.Add(slider);

        parent.Children.Add(row);
        parent.Children.Add(Txt($"На компьютере {maxGb} ГБ — больше этого значения выбрать нельзя. Оставь запас системе и другим программам.", 11, false, (Brush)FindResource("Muted")));
        return slider;
    }

    private static int MemoryLimitGb()
    {
        var (total, _) = SystemInfo.Memory();
        int totalGb = total > 0 ? (int)(total / 1073741824L) : 16;
        return Math.Clamp(totalGb, 4, 512);
    }

    private void ShowDownloads()
    {
        SetActiveNav("Downloads");
        currentPage = "downloads";
        SetHeader("АКТИВНОСТЬ", "Загрузки", "Здесь будут отображаться установки ядер и модификаций.");
        SetPage(Card(Stack(
            Txt("Пока нет активных загрузок", 20, true),
            Txt("Автоматический загрузчик будет подключён следующим этапом. Каталог уже открывает официальные источники.", 13, false, (Brush)FindResource("Muted")))), "downloads");
    }

    private void ShowFiles()
    {
        SetActiveNav("Files");
        currentPage = "files";
        SetHeader("ДАННЫЕ", "Файлы и бэкапы", "Локальные папки конфигураций и резервные копии.");

        var root = new StackPanel();
        root.Children.Add(Txt("Резервное копирование пока создаёт ZIP-архив выбранной папки вручную.", 13, false, (Brush)FindResource("Muted")));

        // Кнопки в ряд, как на всех остальных страницах: в вертикальном StackPanel они растягивались
        // на всю ширину карточки и превращались в две «плитки».
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(Btn("Создать ZIP-бэкап папки…", (_, _) => BackupFolder(), true));
        actions.Children.Add(Btn("Открыть папку данных ForgePanel", (_, _) => OpenUrl(dataDir)));
        root.Children.Add(actions);
        SetPage(Card(root), "files");
    }

    private void BackupFolder()
    {
        var input = new TextBox { Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), MinWidth = 420 };
        var win = new Window
        {
            Title = "Папка для резервной копии",
            Content = Stack(Txt("Укажи полный путь к папке:", 13), input),
            Width = 500, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (Brush)FindResource("Panel"),
            Foreground = TileForeground
        };
        Dialog.DarkCaption(win);

        win.ShowDialog();
        if (string.IsNullOrWhiteSpace(input.Text) || !Directory.Exists(input.Text)) return;

        var save = new SaveFileDialog { Filter = "ZIP archive (*.zip)|*.zip", FileName = $"ForgePanel-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
        if (save.ShowDialog() == true)
        {
            try { System.IO.Compression.ZipFile.CreateFromDirectory(input.Text, save.FileName); Dialog.Info("Резервная копия создана.", "Готово"); }
            catch (Exception ex) { Dialog.Info(ex.Message, "Ошибка"); }
        }
    }

    // Метрики: всё читается с самого компьютера — реестр, Win32, диски и запуск java -version.
    private void ShowMetrics()
    {
        SetActiveNav("Metrics");
        currentPage = "metrics";
        SetHeader("СИСТЕМА", "Метрики", "Реальные характеристики этого компьютера.");

        var (total, free) = SystemInfo.Memory();
        var installs = SystemInfo.JavaInstalls();
        var java = installs.FirstOrDefault();
        int mhz = SystemInfo.CpuMhz();

        var grid = TileGrid(280, 0);
        grid.Margin = new Thickness(0, 0, -TileGap, 15 - TileGap);
        grid.Children.Add(SpecTile("ПРОЦЕССОР", SystemInfo.CpuName(),
            $"{Environment.ProcessorCount} логических" + (mhz > 0 ? $" · {mhz} МГц" : "")));
        grid.Children.Add(SpecTile("ОПЕРАТИВНАЯ ПАМЯТЬ", SystemInfo.FormatGb(total),
            $"Свободно сейчас: {SystemInfo.FormatGb(free)}"));
        grid.Children.Add(SpecTile("ВИДЕОКАРТА", SystemInfo.GpuName(), "Активные адаптеры Windows"));
        grid.Children.Add(SpecTile("JAVA", java != null ? java.Version : "не найдена",
            java != null
                ? $"{java.Path} · установлено копий: {installs.Count}"
                : "Java-сервер не запустится, пока Java не установится"));
        grid.Children.Add(SpecTile("ОПЕРАЦИОННАЯ СИСТЕМА", SystemInfo.WindowsName(),
            RuntimeInformation.OSArchitecture.ToString()));
        grid.Children.Add(SpecTile("СБОРКИ", profiles.Count.ToString(),
            $"Java {profiles.Count(p => p.Edition != "Bedrock")} · Bedrock {profiles.Count(p => p.Edition == "Bedrock")}"));

        SetPage(Stack(grid, DisksCard()), "metrics");
    }

    private Border SpecTile(string label, string value, string note) =>
        Tile(Stack(
            Txt(label, 10, true, (Brush)FindResource("Muted")),
            Txt(value, 17, true),
            Txt(note, 11, false, (Brush)FindResource("Muted"))), 128);

    private Border DisksCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(Txt("Диски", 18, true));

        var drives = SystemInfo.Drives();
        if (drives.Count == 0)
            panel.Children.Add(Txt("Локальные фиксированные диски не найдены.", 12, false, (Brush)FindResource("Muted")));

        foreach (var (name, total, free) in drives)
        {
            long used = Math.Max(0, total - free);
            double ratio = total > 0 ? Math.Clamp((double)used / total, 0, 1) : 0;
            panel.Children.Add(Txt($"{name}   занято {SystemInfo.FormatGb(used)} из {SystemInfo.FormatGb(total)} ({ratio * 100:0}%)", 12, false, (Brush)FindResource("Muted")));

            var track = new Grid { Height = 8, Margin = new Thickness(0, 5, 0, 14) };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });

            var rest = new Border
            {
                Background = (Brush)FindResource("Panel2"),
                BorderBrush = (Brush)FindResource("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0, 4, 4, 0),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(rest, 1);

            var fill = new Border
            {
                Background = (Brush)FindResource("Accent"),
                CornerRadius = new CornerRadius(4, 0, 0, 4),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(fill, 0);

            track.Children.Add(rest);
            track.Children.Add(fill);
            panel.Children.Add(track);
        }

        return Card(panel, new Thickness(0, 0, 0, 12));
    }

    private void ShowSettings()
    {
        SetActiveNav("Settings");
        currentPage = "settings";
        SetHeader("ПЕРСОНАЛИЗАЦИЯ", "Настройки", "Доступ к источникам загрузок и расположение данных.");

        var form = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(Txt("ForgePanel V2", 20, true));

        var key = Field(form, "CurseForge API-ключ (нужен только для CurseForge)", settings.CurseForgeApiKey,
            "ключ с console.curseforge.com");
        form.Children.Add(HintCard("Как получить ключ за 5 минут",
            "1. Открой console.curseforge.com и войди — аккаунт заводится по email или через Google, оплата не нужна.",
            "2. Нажми «Create an application» и укажи любое название, например ForgePanel.",
            "3. В открывшемся приложении добавь игру Minecraft (API Access → Minecraft). Без этого ключ не имеет права запрашивать моды и отвечает 403.",
            "4. Скопируй сгенерированный «API Key» и вставь в поле выше, затем нажми «Сохранить».",
            "5. Ключ хранится только на этом компьютере в файле settings.json. По правилам CurseForge он персональный и передавать его нельзя — у каждого пользователя должен быть свой.",
            "Без ключа эта вкладка не работает, но Modrinth доступен сразу: источник переключается вверху страницы «Каталог»."));

        var contact = Field(form, "Контакт для Modrinth (email или ссылка на проект)", settings.ModrinthContact,
            "you@mail.com или https://modrinth.com/...");
        form.Children.Add(Txt("Modrinth требует, чтобы лаунчер представился: заголовок User-Agent вида «ForgePanel/2.0 (твой контакт)». Без нормального User-Agent API отвечает 403.", 11, false, (Brush)FindResource("Muted")));

        var account = Field(form, "Имя локального профиля", settings.AccountName, "например, Alex");
        form.Children.Add(HintCard("Что такое локальный профиль",
            "Имя показывается внизу левого меню и помогает понять, чья это сборка, если за компьютером работают несколько человек.",
            "Это не аккаунт Mojang или Microsoft: пароля здесь нет и быть не может — ForgePanel работает офлайн и никуда ничего не отправляет.",
            "Вход на сервер с проверкой лицензии требует настоящего аккаунта Minecraft — это отдельная задача, а не эта форма."));

        // Переключатель ниже перерисовывает страницу, поэтому тексты полей сначала попадают в настройки.
        void Collect()
        {
            settings.CurseForgeApiKey = key.Text.Trim();
            settings.ModrinthContact = contact.Text.Trim();
            settings.AccountName = account.Text.Trim();
        }

        form.Children.Add(Txt("Акцент", 12, true, (Brush)FindResource("Muted")));
        var accentChips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (Theme.AccentPalette palette in Theme.Accents)
        {
            string pickedKey = palette.Key;
            accentChips.Children.Add(ToggleChip(palette.Label, Theme.NormalizeAccent(settings.Accent) == pickedKey, () =>
            {
                // Чипы перерисовывают страницу, поэтому несохранённый текст полей забираем заранее.
                Collect();
                settings.Accent = pickedKey;
                Theme.Apply(pickedKey);
                TrySaveSettings();
                ShowSettings();
            }));
        }
        form.Children.Add(accentChips);
        form.Children.Add(Txt("Тема одна — тёмная, значения переписаны с макета. Акцент меняет выделение в меню, кнопки и подсветку карточек сразу, без перезапуска; выбор хранится в settings.json.", 11, false, (Brush)FindResource("Muted")));

        form.Children.Add(Txt("Список версий Minecraft", 12, true, (Brush)FindResource("Muted")));
        var toggles = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        toggles.Children.Add(ToggleChip(
            settings.ShowSnapshots ? "Снапшоты: показываются" : "Снапшоты: скрыты",
            settings.ShowSnapshots,
            () =>
            {
                // Поля выше могут содержать несохранённый текст — забираем их до перерисовки страницы.
                Collect();
                settings.ShowSnapshots = !settings.ShowSnapshots;
                TrySaveSettings();
                ShowSettings();
            }));
        form.Children.Add(toggles);
        form.Children.Add(Txt("Снапшоты — ежедневные тестовые сборки. Если они не нужны, список версий станет короче; в каталоге модов и так ищутся только релизы. Уже открытая форма конфигурации обновит список по кнопке «⟳ Обновить».", 11, false, (Brush)FindResource("Muted")));

        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(Btn("Сохранить", (_, _) =>
        {
            Collect();
            string? error = TrySaveSettings();
            if (error == null) Dialog.Info("Настройки сохранены: ключ, контакт и имя профиля записаны в settings.json.", "Готово");
            else Dialog.Info("Не удалось сохранить настройки: " + error, "Ошибка");
        }, true));
        buttons.Children.Add(Btn("Открыть консоль CurseForge ↗", (_, _) => OpenUrl("https://console.curseforge.com/")));
        buttons.Children.Add(Btn("Открыть папку данных", (_, _) => OpenUrl(dataDir)));
        form.Children.Add(buttons);

        form.Children.Add(Txt("Discord сообщества ForgePanel", 12, true, (Brush)FindResource("Muted")));
        var discord = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        discord.Children.Add(Btn("Присоединиться ↗", (_, _) => OpenUrl(DiscordInvite)));
        form.Children.Add(discord);
        form.Children.Add(Txt(
            $"Ссылка, если открывать вручную: {DiscordInvite}. Панель только передаёт её браузеру: настройки, " +
            "ключи и сборки туда не отправляются и никуда не копируются.", 11, false, (Brush)FindResource("Muted")));

        form.Children.Add(Txt("Папка данных:", 12, true));
        form.Children.Add(Txt(dataDir, 12, false, (Brush)FindResource("Muted")));
        SetPage(Card(form, new Thickness(0)), "settings");
    }

    // Запись настроек на диск: одна и та же процедура нужна и кнопке «Сохранить», и переключателю снапшотов.
    private string? TrySaveSettings()
    {
        try
        {
            settings.Save(dataDir);
            Modrinth.ApplyUserAgent(settings.ModrinthContact);
        CoreInstaller.ApplyUserAgent(settings.ModrinthContact);
            ApplyAccount();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // Имя локального профиля живёт внизу бокового меню; бейдж с инициалом появляется вместе с ним.
    private void ApplyAccount()
    {
        string name = settings.AccountName.Trim();
        if (name.Length == 0)
        {
            SidebarAccountBadge.Visibility = Visibility.Collapsed;
            SidebarAccountName.Text = "Профиль не создан";
            SidebarAccountNote.Text = "Создай его в разделе «Настройки»";
            return;
        }

        SidebarAccountBadge.Visibility = Visibility.Visible;
        SidebarAccountInitial.Text = name[..1].ToUpperInvariant();
        SidebarAccountName.Text = name;
        SidebarAccountNote.Text = "Локальный режим · без пароля";
    }

    private void ImportProfile()
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var p = JsonSerializer.Deserialize<ServerProfile>(File.ReadAllText(dlg.FileName));
            if (p != null) { profiles.Add(p); SaveProfiles(); ShowLibrary(); }
        }
        catch (Exception ex) { Dialog.Info("Не удалось импортировать: " + ex.Message, "Ошибка"); }
    }

    private void ExportProfiles()
    {
        var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "forgepanel-servers.json" };
        if (dlg.ShowDialog() == true)
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Duplicate(ServerProfile p)
    {
        profiles.Add(new ServerProfile
        {
            Name = p.Name + " (копия)", Edition = p.Edition, Version = p.Version, Core = p.Core,
            Port = p.Port, MemoryGb = p.MemoryGb, Folder = p.Folder, Motd = p.Motd,
            MaxPlayers = p.MaxPlayers, OnlineMode = p.OnlineMode, IconFile = p.IconFile,
            ViewDistance = p.ViewDistance, Difficulty = p.Difficulty, AllowEnd = p.AllowEnd,
            AutoBackup = p.AutoBackup, BackupKeep = p.BackupKeep,
            JavaPath = p.JavaPath,
            AutoRestart = p.AutoRestart, RestartAt = p.RestartAt
        });
        SaveProfiles();
        ShowLibrary();
    }

    private void DeleteProfile(ServerProfile p)
    {
        string raw = BuildChecker.ResolveFolder(p, dataDir);
        string folder = Path.GetFullPath(raw);

        // Живой процесс держит файлы сборки открытыми: удаление под ним оставило бы огрызки
        // и оборвало сервер, к которому кто-то уже подключён. Поэтому только отказ.
        if (Running(p) != null)
        {
            Dialog.Info($"Сервер «{p.Name}» запущен, а его файлы заняты процессом. " +
                "Сначала останови его кнопкой «Стоп» — и потом удаляй.", "Сначала остановите сервер");
            return;
        }

        bool exists = Directory.Exists(folder);
        bool own = InsidePanelData(folder);

        // «Дублировать» копирует путь сборки, поэтому две конфигурации могут лежать в одной
        // папке. Стереть её по клику на одной из них — значит молча сломать вторую.
        ServerProfile? twin = profiles.FirstOrDefault(o => o != p &&
            string.Equals(Path.GetFullPath(BuildChecker.ResolveFolder(o, dataDir)), folder,
                StringComparison.OrdinalIgnoreCase));

        // Папки нет, или человек сам прописал ей путь вне каталога панели, или её делит с кем-то:
        // стирать чужие файлы по кнопке «Удалить» никто не просил, поэтому снимаем только конфиг.
        if (!exists || !own || twin is not null)
        {
            string note = !exists
                ? "Папки сборки на диске уже нет — уберём только конфигурацию из списка."
                : !own
                    ? $"Файлы этой сборки лежат вне каталога панели: {folder}. Панель их не тронет."
                    : $"Эту же папку использует конфигурация «{twin!.Name}»: стереть её — значит " +
                      "сломать и её тоже. Поэтому снимаем только запись из списка.";
            if (!Dialog.Confirm($"{note}\n\nУбрать конфигурацию «{p.Name}» из списка?", "Подтверждение")) return;
            DropProfile(p, raw, folder);
            return;
        }

        (int Files, long Bytes, int Links) stats = MeasureFolder(folder);

        // Внутри сборки могут лежать папки-ссылки (junction) на другие места диска: так делают,
        // когда хотят разделить mods между сборками. Рекурсивное удаление способно уйти
        // по такой ссылке за пределы сборки, поэтому при её наличии файлы не трогаем.
        if (stats.Links > 0)
        {
            Dialog.Info("Внутри папки сборки есть ссылки на другие папки диска (junction). " +
                "Стерев папку рекурсивно, панель могла бы стереть и то, что лежит по ту сторону " +
                $"ссылки, поэтому файлы не трогаем: {folder}.\n\n" +
                "Убери ссылку и повтори удаление — или удали папку вручную через проводник.",
                "В сборке чужие папки по ссылке");
            return;
        }

        int answer = Dialog.Choose(
            $"Сборка «{p.Name}».\n\nПапка: {folder}\n" +
            $"Файлов в ней: {stats.Files} · {SystemInfo.FormatSize(stats.Bytes)}\n\n" +
            "«Убрать из списка» — конфигурация исчезнет из панели, а мир, моды и настройки " +
            "останутся на диске: сборку можно будет вернуть.\n\n" +
            "«Удалить папку» — сотрётся всё, что в ней лежит, и вернуть это будет нечем.\n\n" +
            $"Копии мира панель хранит отдельно ({Path.Combine(dataDir, "backups")}) и при удалении " +
            "сборки их не трогает.",
            "Что удалить", DeleteAnswers, 1, 0);

        // 0 — «Отмена», -1 — окно закрыли крестиком. Enter как раз ведёт на безопасный ответ.
        if (answer != 1 && answer != 2) return;
        if (answer == 1)
        {
            DropProfile(p, raw, folder);
            return;
        }

        try
        {
            Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Конфигурацию не снимаем: сборка осталась на диске, и человек должен видеть её
            // в списке, а не искать по памяти, где лежит не удалённая папка.
            Dialog.Info($"Папку до конца стереть не вышло: {ex.Message}\n\n" +
                "Обычно мешает файл, который держит открытым антивирус, проводник или другая " +
                $"программа. Конфигурацию оставили на месте; проверь, что {folder} пуста, и повтори.",
                "Папка не удалена");
            return;
        }

        DropProfile(p, raw, folder);
    }

    // Удалять разрешаем только то, что панель создала сама внутри своего каталога. Проверка на
    // предок пути, а не на «папка называется instances»: путь человек вписывает руками, и в
    // нём легко оказываются «..» — GetFullPath до сравнения сворачивает их в нормальный вид.
    private bool InsidePanelData(string folder)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir));
        string full = Path.TrimEndingDirectorySeparator(folder);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static (int Files, long Bytes, int Links) MeasureFolder(string folder)
    {
        int files = 0;
        long bytes = 0;
        int links = 0;

        // Своей очередью, а не EnumerateFiles(AllDirectories): тот обрывает всё перечисление,
        // стоит одной подпапке оказаться занятой, а нам нужно пропустить одну. Ссылки и
        // соединения считаем отдельно: обход в них не заходит — иначе папка со ссылкой на сама
        // себя меряется до зависания.
        var queue = new Queue<string>();
        queue.Enqueue(folder);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            try
            {
                foreach (string sub in Directory.GetDirectories(current))
                {
                    if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        links++;
                        continue;
                    }
                    queue.Enqueue(sub);
                }

                foreach (string file in Directory.GetFiles(current))
                {
                    var info = new FileInfo(file);
                    if (!info.Exists) continue;
                    files++;
                    bytes += info.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Не дочитали подпапку — её файлы просто не попадут в замер; удалению это не мешает.
            }
        }

        return (files, bytes, links);
    }

    // Снятие конфигурации вместе с записями о процессе этой папки и о её счётчике перезапусков.
    private void DropProfile(ServerProfile p, string rawFolder, string fullFolder)
    {
        profiles.Remove(p);
        SaveProfiles();
        servers.Remove(rawFolder);
        servers.Remove(fullFolder);
        restarts.Remove(rawFolder);
        restarts.Remove(fullFolder);
        ShowLibrary();
    }

    private static readonly string[] DeleteAnswers = { "Отмена", "Убрать из списка", "Удалить папку" };

    private void OpenFolder(ServerProfile p)
    {
        var folder = BuildChecker.ResolveFolder(p, dataDir);
        Directory.CreateDirectory(folder);
        OpenUrl(folder);
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Dialog.Info(ex.Message, "Папку не открыть"); }
    }

    // Установка ядра: адрес берётся из официального API, файл качается с проверкой контрольной суммы
    // и кладётся в папку сборки. Если сервис не ответил или сумма не сошлась — показываем его ответ,
    // а не «успешно»: в папке после неудачи ничего не остаётся.
    private async void InstallCore(ServerProfile p)
    {
        // «Своё ядро» — это не про загрузку: здесь панель может только открыть папку, куда файл
        // кладут руками, и проверить, что он там появился.
        if (CoreInstaller.IsCustom(p.Core))
        {
            if (Dialog.Confirm(CoreInstaller.UnsupportedReason(p.Core) +
                    $"\n\nПапка сборки: {BuildChecker.ResolveFolder(p, dataDir)}\n\nОткрыть её?",
                    "Ядро кладёшь сам"))
            {
                OpenFolder(p);
            }

            return;
        }

        if (!CoreInstaller.Supports(p.Core) && !ForgeInstaller.Supports(p.Core))
        {
            Dialog.Info(CoreInstaller.UnsupportedReason(p.Core), "Это ядро ставится вручную");
            return;
        }

        string folder = BuildChecker.ResolveFolder(p, dataDir);
        CoreInstaller.ApplyUserAgent(settings.ModrinthContact);

        var cts = new CancellationTokenSource();
        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Margin = new Thickness(0, 14, 0, 8),
            Visibility = Visibility.Collapsed
        };
        var info = Txt("Готовлю загрузку…", 12, false, (Brush)FindResource("Muted"));

        var stage = new Progress<string>(text =>
        {
            // Полоса показывается с первым же отчётом: пока нечего показывать, висеть ей незачем.
            bar.Visibility = Visibility.Visible;
            info.Text = text;
        });
        var bytes = new Progress<(long Done, long Total)>(report =>
        {
            if (report.Total > 0)
            {
                bar.Maximum = report.Total;
                bar.Value = Math.Min(report.Done, report.Total);
                info.Text = $"{SystemInfo.FormatSize(report.Done)} из {SystemInfo.FormatSize(report.Total)}";
            }
            else
            {
                info.Text = $"{SystemInfo.FormatSize(report.Done)} · сервис не сообщил итоговый размер";
            }
        });

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Txt("УСТАНОВКА ЯДРА", 11, true, (Brush)FindResource("Accent")));
        panel.Children.Add(Txt($"{p.Core} {p.Version}", 18, true));
        panel.Children.Add(Txt($"Папка: {folder}", 11, false, (Brush)FindResource("Muted")));
        panel.Children.Add(bar);
        panel.Children.Add(info);

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Btn("Отменить", (_, _) => cts.Cancel()));
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = "Установка ядра",
            Width = 560,
            Height = 280,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("Bg"),
            Foreground = TileForeground,
            Content = panel
        };
        Dialog.DarkCaption(win);
        win.Show();
        // Закрыли окно крестиком — прерываем загрузку, чтобы в папке не остался недокачанный файл.
        win.Closing += (_, _) => cts.Cancel();

        string? saved = null;
        string? failure = null;
        bool cancelled = false;

        try
        {
            saved = await InstallCoreFilesAsync(p, folder, stage, bytes, cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            win.Close();
            cts.Dispose();
        }

        ShowLibrary();

        if (failure != null)
        {
            Dialog.Info(failure, "Установка не выполнена");
            return;
        }

        if (cancelled)
        {
            Dialog.Info("Загрузка прервана — файл не записан.", "Отменено");
            return;
        }

        if (saved != null)
        {
            // У Forge и NeoForge результат установки — не jar, а скрипт запуска: назови его
            // файлом запуска, иначе пользователь примет run.bat за лишний мусор рядом с ядром.
            string headline = ForgeInstaller.Supports(p.Core)
                ? $"Ядро развёрнуто установщиком. Файл запуска:\n{saved}"
                : $"Ядро на месте:\n{saved}";

            if (Dialog.Confirm(
                $"{headline}\n\nЛицензионное соглашение ещё не принято: без eula=true сервер не запустится. " +
                "Принять его можно в меню «⋯» этой сборки.\n\nОткрыть папку сборки?",
                "Готово"))
            {
                OpenUrl(folder);
            }
        }
    }

    // Как именно ставится ядро сборки: у Forge и NeoForge это установщик с докачкой библиотек,
    // у остальных — один jar. Метод выделен потому, что установка модпака сначала ставит ядро и
    // только потом файлы пака, и ждать эти два шага нужно подряд.
    private async Task<string> InstallCoreFilesAsync(ServerProfile p, string folder,
        IProgress<string> stage, IProgress<(long Done, long Total)> bytes, CancellationToken ct,
        string? pinnedLoader = null)
    {
        if (ForgeInstaller.Supports(p.Core))
        {
            stage.Report("Ищу сборку…");
            return await ForgeInstaller.InstallAsync(p, folder, stage, bytes, ct, pinnedLoader);
        }

        CoreFile file = await CoreInstaller.ResolveAsync(p.Core, p.Version, stage, ct);
        stage.Report($"{file.Source}: {file.FileName} · начинаю загрузку…");
        return await CoreInstaller.InstallAsync(file, folder, bytes, ct);
    }

    // Модпак Modrinth (.mrpack) — это zip с modrinth.index.json в корне. Панель читает индекс и
    // собирает под него НОВУЮ сборку: доливать чужой список модов в готовую означало бы устроить
    // в ней конфликт версий, а чинить такой конфликт лаунчер честно не берётся.
    private async void InstallModpack()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Модпак Modrinth",
            Filter = "Модпак Modrinth (*.mrpack)|*.mrpack|Архив (*.zip)|*.zip|Все файлы|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        ModpackIndex index;
        try
        {
            index = await Task.Run(() => ModpackIndex.Read(dialog.FileName));
        }
        catch (Exception ex)
        {
            Dialog.Info("Пак не разобран: " + ex.Message, "Модпак Modrinth");
            return;
        }

        ServerProfile p = ProfileFor(index);
        string folder = BuildChecker.ResolveFolder(p, dataDir);

        // Ядро из пака нужнее скачанного панелью: Fabric-паки упаковывают рабочий запускатель в
        // server.jar, и второе ядро рядом — просто лишний файл другой версии. Исключение —
        // Forge и NeoForge: их jar без библиотек, которые доносит установщик, не заведётся, так что
        // там своё ядро ставим всегда.
        bool coreFromPack = index.ShipsOwnServerJar && !ForgeInstaller.Supports(index.Core);
        if (!Dialog.Confirm(ModpackSummary(index, p, folder, coreFromPack), "Установить модпак")) return;

        profiles.Add(p);
        SaveProfiles();
        ShowLibrary();

        ModpackInstaller.Result? made = null;
        string? launcher = null;
        string staging = ModpackInstaller.StagingRoot(dataDir, p.Name);
        var (finished, error) = await RunWithProgressAsync("УСТАНОВКА МОДПАКА",
            $"{p.Name} · {index.Core} {index.GameVersion}",
            $"{Path.GetFileName(index.Archive)} · {index.InstallableFiles} файлов · {index.SizeText}",
            index.TotalBytes,
            async (bytes, stage, token) =>
            {
                Directory.CreateDirectory(folder);
                if (index.LoaderIsAuto && !coreFromPack)
                {
                    stage.Report($"Сначала ядро: {index.Core} {index.GameVersion}…");
                    launcher = await InstallCoreFilesAsync(p, folder, stage, bytes, token);
                }

                made = await ModpackInstaller.InstallAsync(index, folder, staging, bytes, stage, token);
            });

        // Плитка нарисована до установки и без этого вызова осталась бы в «Сервер ещё не
        // установлен»: человек видит предупреждение и идёт качать ядро руками поверх того, что
        // пак уже принёс. Обновляем до развилки — честное состояние нужно и при отмене.
        ShowLibrary();

        if (!finished)
        {
            // Сборка уже в списке, и часть файлов могла лечь: удаление конфигурации папку не трогает,
            // поэтому про молча оставленную недособранную сборку надо сказать вслух.
            Dialog.Info(error == null
                    ? $"Установка отменена на середине. Сборка «{p.Name}» осталась в списке, часть файлов " +
                      $"лежит в {folder}. Можешь удалить сборку в Библиотеке и поставить пак заново — " +
                      "файлы отменённой части панель не перезапишет, папку чистят руками."
                    : "Модпак не установлен: " + error,
                error == null ? "Отменено" : "Ошибка");
            return;
        }

        int files = made?.Downloaded ?? 0;
        string overrides = made != null && made.Unpacked.Count > 0
            ? "Переопределения добавили в сборку: " + string.Join(", ", made.Unpacked) + "."
            : "Переопределений в паке не было.";

        // Три разных исхода шага ядра, и путать их нельзя: «поставили», «в паке уже лежит своё»
        // и «панель не умеет, клади руками» — это разные инструкции человеку.
        string coreLine = launcher != null ? $"Ядро: {launcher}\n"
            : coreFromPack ? $"Ядро: {index.PackServerJarName} из пака — своё панель не качала.\n"
            : "Ядро панель не ставила — положи его в папку сборки сам.\n";

        Dialog.Info($"Сборка «{p.Name}» собрана: {index.Core} {index.GameVersion}, файлов {files} " +
                    $"({index.SizeText}), ещё {index.ClientOnly} клиент-only панель не качала — серверу они " +
                    $"не нужны. {overrides}\n\nПапка: {folder}\n" + coreLine +
                    "\nEULA панель за тебя не принимает: нажми «Принять EULA» в меню сборки, потом «▶ Играть». " +
                    "Первый запуск готовит мир и читает все моды — это не мгновенно.",
                    "Модпак установлен");
    }

    // Что из индекса получается сборкой: имя, версия, ядро, память и порт. Имя и папка не должны
    // столкнуться с уже заведённой сборкой — ResolveFolder молча вернул бы чужую папку.
    private ServerProfile ProfileFor(ModpackIndex index) =>
        PackProfile(index.Name.Length > 0 ? index.Name : Path.GetFileNameWithoutExtension(index.Archive),
            index.Core, index.GameVersion);

    // Сборка под модпак любого из двух источников: имя не должно столкнуть уже заведённую,
    // порт — пересечься с занятым. Пак — это десятки модов: им нужно больше памяти, чем голому
    // серверу, поэтому сборка стартует с 6 ГБ, а не с привычных 4; поправить можно в настройках.
    private ServerProfile PackProfile(string wantedName, string core, string version)
    {
        string wanted = wantedName.Length > 0 ? wantedName : "Модпак";
        string name = wanted;
        int counter = 2;
        while (profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ||
               Directory.Exists(BuildChecker.ResolveFolder(new ServerProfile { Name = name }, dataDir)))
            name = $"{wanted} ({counter++})";

        int port = 25565;
        while (profiles.Any(p => p.Port == port)) port++;

        return new ServerProfile
        {
            Name = name,
            Edition = "Java",
            Version = version,
            Core = core,
            MemoryGb = 6,
            Port = port,
            Motd = name
        };
    }

    // Сводка до единого «да»: что пак скачает, чего не скачает и почему.
    private string ModpackSummary(ModpackIndex index, ServerProfile p, string folder, bool coreFromPack)
    {
        var lines = new List<string>
        {
            $"Пак: {index.Name}" + (index.PackVersion.Length > 0 ? $" {index.PackVersion}" : ""),
            (index.Author.Length > 0 ? $"Автор: {index.Author}" : "Автор: в индексе не указан"),
            $"Minecraft {index.GameVersion} · ядро {index.Core}" +
            (index.LoaderVersion.Length > 0 ? $" (пак просит {index.LoaderVersion})" : ""),
            $"Скачать: {index.InstallableFiles} файлов · {index.SizeText} · куда: {index.RootsText}",
            $"Клиент-only, не качаем: {index.ClientOnly}" +
            (index.ForeignHost > 0 ? $" · ссылок не на Modrinth, не качаем: {index.ForeignHost}" : ""),
            $"Сборка: «{p.Name}» · {folder} · порт {p.Port} · {p.MemoryGb} ГБ ОЗУ",
        };

        if (coreFromPack)
        {
            lines.Add($"Ядро ставить не будем: пак сам приносит {index.PackServerJarName} в корень " +
                      "сборки, и запускаться будет он. Своё панель не качает — иначе в папке лежало бы " +
                      "два запускателя разных версий.");
        }
        else if (index.LoaderIsAuto && index.LoaderVersion.Length > 0)
        {
            lines.Add($"Панель ставит своё ядро под {index.GameVersion}, а не ровно {index.LoaderVersion} " +
                      "из пака: версия загрузчика в индексе — пожелание автора, свежие сборки обычно " +
                      "совместимы. Если сервер ругнётся на версии, скажи — добавим точную.");
        }

        foreach (string note in index.Notes) lines.Add("· " + note);
        lines.Add("");
        lines.Add("Сборка создаётся новой: существующие панели не трогаются. Файлы идут по списку " +
                  "из индекса; если у файла в индексе указан SHA-1, скачанный файл сверяется с ним.");
        lines.Add("EULA за тебя никто не принимает — его надо будет открыть в меню сборки.");

        return string.Join("\n", lines);
    }

    // Модпак CurseForge — обычный .zip с manifest.json в корне: список файлов задан парами
    // projectID/fileID, а рядом лежит папка переопределений. Как и с Modrinth, под него создаётся
    // НОВАЯ сборка: доливать чужой список модов в готовую означало бы устроить в ней конфликт
    // версий, а чинить такой конфликт лаунчер честно не берётся.
    private async void InstallCursePack()
    {
        if (string.IsNullOrWhiteSpace(settings.CurseForgeApiKey))
        {
            Dialog.Info("Модпаки CurseForge качаются через его API, а ключа в настройках нет: открой " +
                "«Настройки», впиши ключ с console.curseforge.com и возвращайся сюда.", "Нужен API-ключ");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Модпак CurseForge",
            Filter = "Архив модпака (*.zip)|*.zip|Все файлы|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        CursePackIndex pack;
        try
        {
            pack = await Task.Run(() => CursePackIndex.Read(dialog.FileName));
        }
        catch (Exception ex)
        {
            Dialog.Info("Пак не разобран: " + ex.Message, "Модпак CurseForge");
            return;
        }

        ServerProfile p = PackProfile(pack.Name.Length > 0 ? pack.Name : Path.GetFileNameWithoutExtension(pack.Archive),
            pack.Core, pack.GameVersion);
        string folder = BuildChecker.ResolveFolder(p, dataDir);

        // Готовый server.jar в переопределениях нужнее скачанного панелью — кроме Forge-семейства,
        // чей jar без библиотек установщика всё равно не заведётся.
        bool coreFromPack = pack.ShipsOwnServerJar && !ForgeInstaller.Supports(pack.Core);

        // Точный номер загрузчика из манифеста применить можно только там, где ядро ставит
        // установщик — то есть у Forge и NeoForge. У Fabric-семейства адрес строится по версии
        // Minecraft, и номера сборки из пака там просто нет.
        string pin = ForgeInstaller.Supports(pack.Core) ? pack.LoaderVersion : "";
        if (!Dialog.Confirm(CursePackSummary(pack, p, folder, coreFromPack, pin), "Установить модпак")) return;

        profiles.Add(p);
        SaveProfiles();
        ShowLibrary();

        bool coreStep = pack.LoaderIsAuto && !coreFromPack;
        int items = pack.RequiredFiles + (coreStep ? 1 : 0);

        CursePackInstaller.Result? made = null;
        string? launcher = null;
        string staging = ModpackInstaller.StagingRoot(dataDir, p.Name);
        var (finished, error) = await RunWithProgressAsync("УСТАНОВКА МОДПАКА CURSEFORGE",
            $"{p.Name} · {pack.Core} {pack.GameVersion}",
            $"{Path.GetFileName(pack.Archive)} · {pack.RequiredFiles} файлов · размер уточняется по ходу",
            items, "файлов",
            async (scale, stage, token) =>
            {
                Directory.CreateDirectory(folder);
                long baseItems = 0;

                if (coreStep)
                {
                    stage.Report($"Сначала ядро: {pack.Core} {pack.GameVersion}…");
                    // Ядро стоит ровно одного шага пофайловой шкалы: полоса дождётся его целиком
                    // и прыгнет на единицу, а чем именно занята панель эти минуты — видно в строке
                    // над полосой: ядро докладывает туда про поиск сборки и библиотеки.
                    var coreScale = new PackFiles.Inline(report =>
                    {
                        double part = report.Total > 0
                            ? Math.Clamp((double)report.Done / report.Total, 0, 1)
                            : 0;
                        scale.Report((baseItems + (long)part, items));
                    });

                    launcher = await InstallCoreFilesAsync(p, folder, stage, coreScale, token, pin);
                    baseItems = 1;
                    scale.Report((1, items));
                }

                made = await CursePackInstaller.InstallAsync(pack, folder, staging, settings.CurseForgeApiKey,
                    new PackFiles.Inline(report => scale.Report((baseItems + report.Done, items))), stage, token);
            });

        // Плитка нарисована до установки: без этого вызова она осталась бы в «Сервер ещё не
        // установлен», и человек пошёл бы качать ядро руками поверх того, что уже легло.
        ShowLibrary();

        if (!finished)
        {
            Dialog.Info(error == null
                    ? $"Установка отменена на середине. Сборка «{p.Name}» осталась в списке, часть файлов " +
                      $"лежит в {folder}. Можешь удалить сборку в Библиотеке и поставить пак заново — " +
                      "файлы отменённой части панель не перезапишет, папку чистят руками."
                    : "Модпак не установлен: " + error,
                error == null ? "Отменено" : "Ошибка");
            return;
        }

        string overrides = made != null && made.Unpacked.Count > 0
            ? "Переопределения добавили в сборку: " + string.Join(", ", made.Unpacked) + "."
            : "Переопределений в паке не было.";

        // Пропущенные файлы — это не «всё хорошо»: сервер без половины модов либо не запустится,
        // либо запустится не так, как задумывал автор пака. Список надо показать целиком.
        string lost = made == null || made.Failed == 0
            ? ""
            : $"\n\nСкачать не удалось ({made.Failed} шт.):\n" + FailureList(made.Failures);

        string coreLine = launcher != null ? $"Ядро: {launcher}\n"
            : coreFromPack ? $"Ядро: {pack.PackServerJarName} из пака — своё панель не качала.\n"
            : "Ядро панель не ставила — положи его в папку сборки сам.\n";

        Dialog.Info($"Сборка «{p.Name}» собрана: {pack.Core} {pack.GameVersion}, скачано " +
                    $"{made?.Downloaded ?? 0} файлов на {SystemInfo.FormatSize(made?.Bytes ?? 0)}. " +
                    $"Необязательных (required=false), которые панель не качала: {pack.OptionalFiles}. " +
                    $"{overrides}\n\nПапка: {folder}\n" + coreLine +
                    lost +
                    "\nEULA панель за тебя не принимает: нажми «Принять EULA» в меню сборки, потом «▶ Играть». " +
                    "Первый запуск готовит мир и читает все моды — это не мгновенно.",
                    "Модпак установлен");
    }

    // Первые шесть причин целиком, остальные — строкой: список на двести позиций в диалоге
    // не читается, а понять, что именно сломалось, нужно по первым строкам.
    private static string FailureList(List<string> failures)
    {
        const int show = 6;
        var lines = failures.Take(show).Select(f => "· " + f).ToList();
        if (failures.Count > show) lines.Add($"· …и ещё {failures.Count - show}");
        return string.Join("\n", lines);
    }

    // Сводка до единого «да»: сколько панель скачает, чего не скачает и почему здесь нельзя
    // показать полосу по байтам.
    private string CursePackSummary(CursePackIndex pack, ServerProfile p, string folder, bool coreFromPack, string pin)
    {
        var lines = new List<string>
        {
            $"Пак: {pack.Name}" + (pack.PackVersion.Length > 0 ? $" {pack.PackVersion}" : ""),
            pack.Author.Length > 0 ? $"Автор: {pack.Author}" : "Автор: в манифесте не указан",
            $"Minecraft {pack.GameVersion} · ядро {pack.Core}" +
            (pack.LoaderVersion.Length > 0 ? $" (пак просит {pack.LoaderVersion})" : ""),
            $"Скачать: {pack.RequiredFiles} файлов в mods · переопределения: {pack.RootsText}",
            $"Не качаем: {pack.OptionalFiles} необязательных (required=false)",
            $"Сборка: «{p.Name}» · {folder} · порт {p.Port} · {p.MemoryGb} ГБ ОЗУ",
        };

        if (coreFromPack)
        {
            lines.Add($"Ядро ставить не будем: пак сам приносит {pack.PackServerJarName} в переопределения, " +
                      "и запускаться будет оно. Своё панель не качает — иначе в папке лежало бы два " +
                      "запускателя разных версий.");
        }
        else if (pin.Length > 0)
        {
            lines.Add($"Ядро: ставлю ровно {pack.Core} {pin}, которую просит пак, а не свежайшую. " +
                      "Если репозиторий отдаст 404 по этому номеру, панель возьмёт рекомендованную " +
                      "сборку и напишет об этом в строке над полосой прогресса.");
        }
        else if (pack.LoaderIsAuto && pack.LoaderVersion.Length > 0)
        {
            lines.Add($"Панель ставит своё ядро под {pack.GameVersion}, а не ровно {pack.LoaderVersion} " +
                      "из пака: адрес ядра этого загрузчика собирается по версии Minecraft, точного " +
                      "номера сборки у него нет.");
        }

        lines.Add("Размера в манифесте нет, поэтому полоса идёт по числу файлов, а не по байтам: " +
                  "каждый файл панель сначала спрашивает у CurseForge (имя, размер, SHA-1), потом качает. " +
                  "На 150 файлов это около 300 запросов к API.");
        lines.Add("Если отдельный файл не отдаётся (автор снял его с CurseForge, кончилась квота запросов), " +
                  "панель не бросает установку, а пропускает его и называет списком в конце.");

        foreach (string note in pack.Notes) lines.Add("· " + note);

        lines.Add("");
        lines.Add("Сборка создаётся новой: существующие панели не трогаются. Файлы идут в mods по " +
                  "номерам из манифеста; если у файла в CurseForge указана SHA-1, скачанный файл сверяется с ней.");
        lines.Add("EULA за тебя никто не принимает — его надо будет открыть в меню сборки.");

        return string.Join("\n", lines);
    }

    // Соглашение принимает только человек: программа не подставляет eula=true сама и не ставит
    // галочку за пользователя — кнопка записи активна ровно после того, как отмечен чекбокс.
    private void AcceptEula(ServerProfile p)
    {
        string folder = BuildChecker.ResolveFolder(p, dataDir);
        string file = Path.Combine(folder, "eula.txt");

        if (File.Exists(file) && EulaAccepted(file))
        {
            Dialog.Info($"Соглашение уже принято: {file}", "EULA");
            return;
        }

        // Окно создаётся до обработчиков: лямбды кнопок обращаются к win, а в C# нельзя сослаться
        // на локальную переменную раньше её объявления.
        var win = new Window
        {
            Title = "Принятие EULA",
            Width = 560,
            Height = 300,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("Bg"),
            Foreground = TileForeground
        };
        Dialog.DarkCaption(win);

        var text = new TextBlock
        {
            Text = "Minecraft сервер распространяется по лицензионному соглашению Mojang. Его нужно прочитать самому: ниже только ссылка и запись того, что именно ты подтвердил.",
            FontSize = 12,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap
        };

        var check = new CheckBox
        {
            Content = "Я прочитал(а) условия соглашения и принимаю их.",
            FontSize = 13,
            Foreground = TileForeground,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var write = Btn("Принять и записать eula=true", (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(file, "# Принято вручную в ForgePanel " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\neula=true\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Dialog.Info("Файл не записать: " + ex.Message, "Ошибка");
                return;
            }

            win.Close();
            ShowLibrary();
            Dialog.Info($"Записано eula=true в {file}.", "Готово");
        }, true);
        write.IsEnabled = false;

        check.Checked += (_, _) => write.IsEnabled = true;
        check.Unchecked += (_, _) => write.IsEnabled = false;

        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(write);
        buttons.Children.Add(Btn("Открыть текст соглашения ↗", (_, _) => OpenUrl("https://www.minecraft.net/en-us/eula")));
        buttons.Children.Add(Btn("Отмена", (_, _) => win.Close()));

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Txt("ЛИЦЕНЗИОННОЕ СОГЛАШЕНИЕ", 11, true, (Brush)FindResource("Accent")));
        panel.Children.Add(Txt($"{p.Core} {p.Version}", 18, true));
        panel.Children.Add(text);
        panel.Children.Add(check);
        panel.Children.Add(buttons);

        win.Content = panel;
        win.Show();
    }

    private static bool EulaAccepted(string file)
    {
        try
        {
            return File.ReadAllLines(file)
                .Any(line => line.Trim().StartsWith("eula=true", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ── Пятый этап: запуск и консоль ────────────────────────────────────────────────
    //
    // Ключ словаря — папка сборки: у конфигурации без своей папки она выводится из имени, и два
    // процесса в одной папке сервера всё равно не поместятся (тот же порт и тот же мир).

    private readonly Dictionary<string, ServerInstance> servers = new(StringComparer.OrdinalIgnoreCase);

    // Папки, по которым прямо сейчас идёт копирование мира. Второй клик «Играть» или вторая
    // копия поверх первой — это старт сервера на середине копии, поэтому держим список открытым.
    private readonly HashSet<string> copying = new(StringComparer.OrdinalIgnoreCase);

    // Три попытки, паузы между ними растут: 5 с, 15 с, 45 с. Потолок нужен потому, что сборка,
    // которая не доживает до спавна на загрузке мира (битый мод, не та Java), падает сразу и
    // крутила бы бесконечный круг: каждый подъём — это десятки секунд диска, памяти и пустого
    // ожидания. Три — и стоп, дальше разбирает человек.
    private const int MaxAutoRestarts = 3;
    private static readonly int[] RestartDelays = { 5, 15, 45 };

    private sealed class RestartWatch
    {
        // Сколько падений подряд уже подняли. Сбрасывается, когда сервер отработал две минуты,
        // когда его остановили сами и когда человек нажал «Играть» руками.
        public int Attempts;

        // Отметка планового перезапуска, после которой он уже сработал. Без неё таймер
        // останавливал бы сервер каждые тридцать секунд до конца суток.
        public DateTime PlannedAt;

        // Что написать в журнал нового процесса первой строкой: без неё человек видит подъём
        // сервера и не понимает, что произошло до него.
        public string Reason = "";
    }

    private readonly Dictionary<string, RestartWatch> restarts = new(StringComparer.OrdinalIgnoreCase);

    // Ставится в первую же секунду закрытия панели. Сервер, умерщвлённый при закрывании, — это
    // намерение человека, а не падение: авто-перезапуск не должен поднимать его обратно.
    private bool shuttingDown;

    // Таймер плановой перезагрузки. Живёт всю жизнь окна: перезапуск по ЧЧ:ММ возможен, только
    // пока панель открыта, и говорим об этом в её настройках, а не обещаем невозможное.
    private DispatcherTimer? restartTimer;

    private ServerProfile? consoleProfile;
    private TextBox? consoleLog;
    private TextBox? consoleInput;
    private TextBlock? consoleState;
    private Button? consoleStart;
    private Button? consoleStop;

    // Карточка разбора журнала. Её заполняет RefreshTriage, а не постройка страницы: процесс
    // умирает, когда человек уже читает консоль, и перестраивать страницу ради одного блока
    // означало бы выбросить его прокрутку в начало журнала.
    private Border? consoleTriage;

    // Живой опрос нагрузки. Таймер нужен только пока открыта страница консоли: гонять его по
    // всем страницам — это лишние пробуждения интерфейса раз в секунду.
    private DispatcherTimer? loadTimer;
    private LoadHistory? loadHistory;
    private ServerInstance? loadInstance;
    private Sparkline? cpuSpark;
    private Sparkline? ramSpark;
    private TextBlock? cpuValue;
    private TextBlock? cpuNote;
    private TextBlock? ramValue;
    private TextBlock? ramNote;

    // До какого номера строк журнала экран уже заполнен.
    private long consoleShown;

    private void DetachConsole()
    {
        consoleProfile = null;
        consoleLog = null;
        consoleInput = null;
        consoleState = null;
        consoleStart = null;
        consoleStop = null;
        consoleTriage = null;
        consoleShown = 0;
        StopLoadTimer();
        loadHistory = null;
        loadInstance = null;
        cpuSpark = null;
        ramSpark = null;
        cpuValue = null;
        cpuNote = null;
        ramValue = null;
        ramNote = null;
    }

    private void StopLoadTimer()
    {
        if (loadTimer == null) return;
        loadTimer.Stop();
        loadTimer.Tick -= TickLoad;
        loadTimer = null;
    }

    // Живой процесс этой конфигурации, если он есть. Завершённый оставляем в словаре: его журнал
    // всё ещё хочется показать, но «запущен» он уже не называется.
    private ServerInstance? Running(ServerProfile p) =>
        Last(p) is { } instance && instance.Phase != ServerPhase.Stopped ? instance : null;

    private ServerInstance? Last(ServerProfile p) =>
        servers.TryGetValue(BuildChecker.ResolveFolder(p, dataDir), out var instance) ? instance : null;

    // Счётчик перезапусков на папку сборки — тот же ключ, что у servers, чтобы состояние
    // attempts не разъезжалось с процессом. Заводим на лету: сборка могла ни разу не падать.
    private RestartWatch Watch(string folder)
    {
        if (!restarts.TryGetValue(folder, out RestartWatch? watch))
            restarts[folder] = watch = new RestartWatch();
        return watch;
    }

    // Свободен ли порт. Занимаем его так же, как это делает сервер — на всех интерфейсах:
    // проверка одного 127.0.0.1 Windows иногда пропускает уже занятый wildcard-сокет и
    // отвечает ложным «свободно».
    private static bool PortFree(int port)
    {
        try
        {
            var probe = new TcpListener(IPAddress.Any, port);
            probe.Start();
            probe.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            // Пробить порт не удалось по другой причине — не мешаем запуску: сервер сам скажет,
            // что не так, и его слова будут точнее наших предположений.
            return true;
        }
    }

    // Название другой сборки этой панели, которая уже сидит на этом порту: без него «порт занят»
    // звучит как претензия к незнакомцу, хотя виноваты две конфигурации с одинаковым числом.
    private string PortRival(ServerProfile p)
    {
        string mine = BuildChecker.ResolveFolder(p, dataDir);
        foreach (ServerInstance instance in servers.Values)
        {
            if (instance.Phase == ServerPhase.Stopped || instance.Folder == mine) continue;
            if (instance.Profile.Port != p.Port) continue;
            return instance.Profile.Name;
        }

        return "";
    }

    private async void StartServer(ServerProfile p) => await StartAsync(p, true, null);

    // Один и тот же путь для ручного «Играть» и для авто-перезапуска. Разница одна: интерактивный
    // запуск имеет право спросить человека (окно «Порт занят», подтверждение копии мира), а
    // автоматический — нет, иначе сервер ждёт клика ровно тогда, когда никого нет у экрана.
    // Поэтому все отказы на неинтерактивном пути уходят в журнал предыдущего процесса, а он всё
    // ещё виден на консоли.
    private async Task StartAsync(ServerProfile p, bool interactive, ServerInstance? previous)
    {
        void Refuse(string message, string title)
        {
            if (interactive) Dialog.Info(message, title);
            else previous?.Note("Авто-перезапуск не состоялся: " + message.Replace("\n", " "));
        }

        string folder = BuildChecker.ResolveFolder(p, dataDir);

        // Сборку могли удалить из списка, пока шла пауза перед авто-перезапуском. Поднимать её
        // заново — значит создать папку заново на пустом месте и показать консоль для сервера,
        // которого человек уже вычеркнул.
        if (!profiles.Contains(p)) return;

        RestartWatch watch = Watch(folder);

        // Ручной «Играть» — это человек разобрался и чинил сборку, поэтому его клик обнуляет счётчик
        // попыток. Иначе вчерашнее падение съело бы право на перезапуск сегодняшней новой ошибки.
        if (interactive)
        {
            watch.Attempts = 0;
            watch.Reason = "";
        }

        if (Running(p) != null)
        {
            ShowConsole(p);
            return;
        }

        if (BuildChecker.IsBedrock(p))
        {
            Refuse(
                "Bedrock Dedicated Server — это bedrock_server.exe без перенаправляемого ввода: команды он " +
                "принимает только в своём окне. Для него запуск отдельная задача, сейчас запускаются Java-сборки.",
                "Запуск только для Java");
            return;
        }

        // Требование проверяем до запуска: сервер на неподходящей Java умирает мгновенно, и
        // UnsupportedClassVersionError в журнале — плохой способ сообщить о версии JDK.
        // Сборка может нести свою Java — тогда берём её, а не то, что панель подобрала бы сама.
        JavaChoice choice = JavaChoice.For(p);
        if (!choice.Ok)
        {
            Refuse(choice.Error, "Java не подходит");
            return;
        }

        JavaInstall java = choice.Install!;
        int needed = choice.Needed;

        if (!Directory.Exists(folder))
        {
            Refuse($"Папки сборки ещё нет:\n{folder}\nСначала установи ядро.", "Нечего запускать");
            return;
        }

        // Порт смотрим до запуска: сервер доходит до «FAILED TO BIND TO PORT» через десять секунд
        // после старта, и в журнале это читается как сломанное ядро, а не как две сборки с
        // одинаковым портом.
        if (!PortFree(p.Port))
        {
            string rival = PortRival(p);
            Refuse(
                $"Порт {p.Port} уже занят" +
                (rival.Length > 0 ? $": на нём стоит «{rival}»." : " другой программой.") +
                $"\n\nДай этой сборке другой порт — карточка «{p.Name}» → «Изменить» → «Порт».",
                "Порт занят");
            return;
        }

        // После установщика Forge в папке лежат run.bat и десятки библиотечных jar: PickCoreJar
        // выбрал бы ядро наугад из них, поэтому сначала смотрим на то, чем Forge считает себя
        // запускаемым, и только потом — общий перебор .jar.
        string corePath;
        string coreNote;
        string? forgeArgs;

        ForgeInstaller.Launch? forge = ForgeInstaller.DescribeLaunch(folder);
        if (forge != null)
        {
            corePath = forge.Target;
            coreNote = forge.Note;
            forgeArgs = forge.ArgsFile;

            // У запуска через cmd нет способа передать -Xmx извне: скрипт подмешивает свои
            // аргументы только из user_jvm_args.txt. Когда classpath даёт файл аргументов,
            // java стартует напрямую и память приходит обычным параметром.
            if (forgeArgs == null && ForgeInstaller.IsScript(corePath))
                ForgeInstaller.ApplyMemoryArgs(folder, p.MemoryGb);
        }
        else
        {
            (string? Jar, string Note) core = BuildChecker.PickCoreJar(folder);
            if (core.Jar == null)
            {
                Refuse(core.Note + "\n\nПапка: " + folder, "Ядро не выбрано");
                return;
            }

            corePath = core.Jar;
            coreNote = core.Note;
            forgeArgs = null;
        }

        bool viaScript = forgeArgs == null && ForgeInstaller.IsScript(corePath);

        if (!EulaAccepted(Path.Combine(folder, "eula.txt")))
        {
            Refuse(
                "Лицензионное соглашение не принято. Сервер прочитает eula.txt, увидит eula=false и остановится " +
                "через секунду — лучше принять его заранее: меню «⋯» этой сборки → «Принять EULA».",
                "EULA не принята");
            return;
        }

        // Копия мира до запуска — страховка от неудачного обновления мода или испорченной
        // генерации. Разрешает человек, и он же может её отменить: отмена копии отменяет и запуск.
        // На авто-перезапуске копию не предлагаем: окно подтверждения остановило бы подъём сервера
        // до тех пор, пока кто-то не придёт к экрану, а молча снимать десятки гигабайт без спроса
        // тем более нельзя. Поэтому на этом пути копии нет — и в журнал пишем именно это.
        string backupNote = "";
        if (p.AutoBackup && interactive)
        {
            string? note = await BackupBeforeStartAsync(p, folder);
            if (note == null) return;
            backupNote = note;
        }
        else if (p.AutoBackup)
        {
            backupNote = "Резервная копия мира перед этим запуском не делалась: на авто-перезапуске " +
                "панель не спрашивает, а молча снимать десятки гигабайт без спроса нельзя.";
        }

        try
        {
            string propertiesNote = ServerProperties.Ensure(p, folder);
            string iconNote = ServerIcon.Apply(folder, p.IconFile);
            string endNote = ApplyEndSetting(p, folder);
            var instance = ServerInstance.Start(p, folder, java.Path, corePath, forgeArgs);

            if (servers.TryGetValue(folder, out ServerInstance? stale) && !ReferenceEquals(stale, instance))
                stale.Dispose();
            servers[folder] = instance;

            instance.Line += (who, sequence, line) => AppendConsole(who, sequence, line);
            instance.Finished += (who, _) => OnServerFinished(who);

            // У нового процесса буфер пуст, и без этих строк человек, пришедший на перезапуск,
            // увидел бы чистый экран вместо того, что произошло минуту назад. Хвост старого журнала
            // подклеиваем из памяти мёртвого процесса — до выгрузки на диск он дойдёт в logs\ сам.
            if (previous != null)
            {
                instance.GraftTail(previous.Snapshot(), 15);
                if (watch.Reason.Length > 0) instance.Note(watch.Reason);
                watch.Reason = "";
            }

            instance.Note(coreNote);
            instance.Note($"Java {java.Version} из {java.Path} · процесс {instance.ProcessId} · требуется {needed}+ · " +
                (choice.Manual ? "взята из настроек сборки" : "подобрана по версии Minecraft"));
            instance.Note($"Команда: {(viaScript ? "cmd.exe" : Path.GetFileName(java.Path))} {instance.Arguments}");
            if (propertiesNote.Length > 0) instance.Note(propertiesNote);
            if (iconNote.Length > 0) instance.Note(iconNote);
            if (endNote.Length > 0) instance.Note(endNote);
            if (backupNote.Length > 0) instance.Note(backupNote);

            // Номер строки у нового процесса начинается с единицы, а экран старого остановился на
            // большом — без перестройки страницы журнал подъёма в него бы не попал. Наводим её
            // только когда консоль этой сборки и так у человека перед глазами: у авто-перезапуска
            // нет права уводить экран с того, что на нём открыто.
            if (interactive || (currentPage == "console" && ReferenceEquals(consoleProfile, p))) ShowConsole(p);
        }
        catch (Exception ex)
        {
            Refuse(ex.Message, "Запуск не выполнен");
        }
    }

    // allow-end применяется перед стартом, но не рядом с server.properties: файл другой, и
    // понимают его только ядра на Bukkit. Отказ от не поддерживающего ядра пишем в журнал
    // честно — молча проигнорированная настройка хуже, чем сообщение.
    private static string ApplyEndSetting(ServerProfile p, string folder)
    {
        if (p.AllowEnd == null) return "";
        if (!BukkitYaml.Supports(p.Core))
            return CoreInstaller.IsCustom(p.Core)
                ? "Энд не трогали: под «Своё ядро» панели неизвестно, читает ли сборка bukkit.yml — " +
                  "правь allow-end в файле сам."
                : $"Энд не трогали: ядро {p.Core} не читает bukkit.yml, ключа allow-end у него нет.";

        BukkitYaml.SetEnd(folder, p.AllowEnd.Value);
        return p.AllowEnd.Value
            ? "bukkit.yml: allow-end: true — Энд открыт."
            : "bukkit.yml: allow-end: false — Энд закрыт, сервер не пустит в измерение.";
    }

    // Быстрая кнопка в меню плитки: правит bukkit.yml сразу, без входа в настройки сборки.
    private void ToggleEnd(ServerProfile p)
    {
        string folder = BuildChecker.ResolveFolder(p, dataDir);
        bool allow = BukkitYaml.ReadEnd(folder) == false;

        try
        {
            BukkitYaml.SetEnd(folder, allow);
        }
        catch (Exception ex)
        {
            Dialog.Info("bukkit.yml не изменился: " + ex.Message, "Энд не тронут");
            return;
        }

        // Файл теперь и есть источник правды: оставим в настройках сборки своё значение —
        // следующий запуск перезапишет то, что человек только что выбрал кнопкой.
        p.AllowEnd = null;
        SaveProfiles();
        if (currentPage == "library") ShowLibrary(GlobalSearch.Text);

        Dialog.Info(
            allow
                ? "Энд открыт: в bukkit.yml записано allow-end: true. Сервер применит это при следующем запуске."
                : "Энд закрыт: в bukkit.yml записано allow-end: false. Перезапусти сервер, если он уже запущен.",
            "Измерение Энд");
    }

    private async void StopServer(ServerProfile p)
    {
        ServerInstance? instance = Running(p);
        if (instance == null) return;

        instance.Stop();
        RefreshConsoleState();

        if (!await instance.WaitForShutdownAsync(20))
        {
            instance.Note("За 20 секунд сервер не завершился — снимаю процесс. Всё, что было записано до " +
                "save-all, на диске; изменения после него — нет.");
            instance.Kill();
        }
    }

    private void ShowConsole(ServerProfile p)
    {
        SetActiveNav("Library");
        currentPage = "console";
        consoleProfile = p;

        string folder = BuildChecker.ResolveFolder(p, dataDir);
        // Консоль открывают и у остановленного сервера — чтобы прочитать, чем он упал.
        SetHeader(Running(p) != null ? "ЗАПУЩЕННЫЙ СЕРВЕР" : "СЕРВЕР ОСТАНОВЛЕН", p.Name,
            $"{p.Core} {p.Version} · порт {p.Port} · {folder}");

        // Шесть строк, и тянется из них только журнал: на высоком окне он забирает всё свободное
        // место, а кнопки, график, разбор, поле ввода и подсказка под ним остаются на своих краях.
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        buttons.Children.Add(Btn("← Библиотека", (_, _) => ShowLibrary(GlobalSearch.Text)));
        consoleStart = Btn("▶ Играть", (_, _) => StartServer(p), true);
        consoleStop = Btn("■ Остановить", (_, _) => StopServer(p));
        buttons.Children.Add(consoleStart);
        buttons.Children.Add(consoleStop);
        buttons.Children.Add(Btn("Очистить журнал", (_, _) =>
        {
            ServerInstance? instance = Last(p);
            instance?.ClearLog();
            consoleShown = instance?.Written ?? 0;
            if (consoleLog != null) consoleLog.Text = "";
        }));

        // Ротация log4j лежит рядом с latest.log, а в панели её не показать — за ней отправляем
        // в проводник.
        string logsFolder = Path.Combine(folder, "logs");
        if (Directory.Exists(logsFolder))
            buttons.Children.Add(Btn("Папка логов", (_, _) => OpenUrl(logsFolder)));

        var state = Txt("", 12, false, (Brush)FindResource("Muted"));
        state.VerticalAlignment = VerticalAlignment.Center;
        state.Margin = new Thickness(4, 0, 0, 0);
        buttons.Children.Add(state);
        root.Children.Add(buttons);
        Grid.SetRow(buttons, 0);

        UIElement load = LoadBlock(p);
        root.Children.Add(load);
        Grid.SetRow(load, 1);

        // Рамка разбора ставится сразу и пустая: её содержимое появляется в тот момент, когда
        // процесс умирает, а перестраивать страницу ради одного блока незачем.
        consoleTriage = new Border
        {
            Background = (Brush)FindResource("Panel2"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            MaxHeight = 300,
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(consoleTriage);
        Grid.SetRow(consoleTriage, 2);

        // Буфер живой сессии есть только у последней сборки; после перезапуска панели он пуст,
        // и тогда показываем хвост файла, который ядро ведёт само.
        string transcript = Last(p)?.Snapshot() ?? "";
        if (transcript.Length == 0) transcript = DiskHistory(folder);

        var log = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = (Brush)FindResource("FieldBg"),
            BorderBrush = (Brush)FindResource("Line"),
            Padding = new Thickness(12, 10, 12, 10),
            MinHeight = 120,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = transcript
        };
        log.PreviewMouseWheel += ConsoleLog_PreviewMouseWheel;
        root.Children.Add(log);
        Grid.SetRow(log, 3);

        var input = new TextBox
        {
            MinHeight = 38,
            Margin = new Thickness(0, 0, 8, 0),
            FontFamily = new FontFamily("Consolas")
        };
        Placeholder.SetText(input, "Команда серверу: list, save-all, say Привет, whitelist Add Имя");
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                SendConsole();
                e.Handled = true;
            }
        };

        var send = Btn("Отправить", (_, _) => SendConsole(), true);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(send, Dock.Right);
        row.Children.Add(send);
        row.Children.Add(input);
        root.Children.Add(row);
        Grid.SetRow(row, 4);

        UIElement hint = Txt(
            "Команды уходят в stdin сервера, журнал читается из его stdout — это то же самое, что писать в " +
            "чёрное окно консоли, только окно внутри панели. При остановке первым делом уходит save-all " +
            "(с 1.20.6 — с флагом flush, он ждёт реальной записи на диск), и только потом stop: если сервер " +
            "не доживёт до своего сохранения и процесс придётся снимать, на диске останется состояние " +
            "после save-all. Не завершился за 20 секунд — снимаем принудительно. Разбор над журналом " +
            "появляется, когда процесса уже нет: он ищет в этих строках известные причины простоя и к каждой " +
            "прикладывает строку, по которой сработал, — того, чего панель не знает, она не выдумывает.",
            12, false, (Brush)FindResource("Muted"));
        root.Children.Add(hint);
        Grid.SetRow(hint, 5);

        consoleLog = log;
        consoleInput = input;
        consoleState = state;
        consoleShown = Last(p)?.Written ?? 0;
        SetPage(root, "console");
        RefreshConsoleState();
        log.ScrollToEnd();
        input.Focus();
    }

    // Подпись над хвостом обязательна: без неё вчерашний журнал неотличим от строк текущего
    // запуска, а человек решит, что сервер уже пишет.
    private static string DiskHistory(string folder)
    {
        ServerLogs.Tail? saved = ServerLogs.Read(folder);
        if (saved == null || saved.Lines == 0) return "";

        return $"[ForgePanel] Журнал взят с диска: {saved.Path}\n" +
               "[ForgePanel] Сервер остановлен, писать командами некуда — это записи прошлых запусков.\n" +
               saved.Text;
    }

    // Разбор показываем только у остановленного сервера: у живого последняя ошибка в буфере может
    // относиться к прошлому запуску, и выдавать её за теперешнюю значило бы пугать человека попусту.
    private void RefreshTriage()
    {
        if (consoleTriage == null || consoleProfile == null) return;

        if (Running(consoleProfile) != null)
        {
            consoleTriage.Visibility = Visibility.Collapsed;
            return;
        }

        string transcript = Last(consoleProfile)?.Snapshot() ?? "";
        if (transcript.Length == 0)
            transcript = DiskHistory(BuildChecker.ResolveFolder(consoleProfile, dataDir));

        if (transcript.Length == 0)
        {
            consoleTriage.Visibility = Visibility.Collapsed;
            return;
        }

        LogTriage.Report report = LogTriage.Analyze(transcript);
        consoleTriage.BorderBrush = report.Findings.Any(f => f.Kind == LogTriage.Level.Fatal)
            ? WarnBrush
            : (Brush)FindResource("Line");

        // Рамка ограничена по высоте и скроллится сама: разбор из трёх причин с длинными
        // пояснениями вытеснил бы журнал из его тянущейся строки, а страница консоли скролла не имеет.
        consoleTriage.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = TriageCard(report),
        };
        consoleTriage.Visibility = Visibility.Visible;
    }

    private UIElement TriageCard(LogTriage.Report report)
    {
        int fatal = report.Findings.Count(f => f.Kind == LogTriage.Level.Fatal);
        var stack = new StackPanel();

        string headline;
        Brush tone;
        if (fatal > 0 && report.ExitCode == 0)
        {
            // Хвост с диска помнит не один запуск: вчера порт был занят, сегодня сервер остановили
            // кнопкой. Называть вчерашнее падение теперешней причиной нельзя.
            headline = "Разбор журнала: последний запуск завершился по команде, но падение в этом журнале есть";
            tone = WarnBrush;
        }
        else if (fatal > 0)
        {
            headline = "Разбор журнала: из-за этих строк сервер не держится";
            tone = WarnBrush;
        }
        else if (report.ExitCode == 0)
        {
            headline = "Разбор журнала: сервер остановлен по команде, падений не видно";
            tone = OkBrush;
        }
        else if (report.Findings.Count > 0)
        {
            headline = "Разбор журнала: причин простоя нет, есть замечания";
            tone = TileForeground;
        }
        else if (report.ExitCode != null)
        {
            headline = $"Разбор журнала: процесс завершился кодом {report.ExitCode}, а причины в тексте панель не узнаёт";
            tone = WarnBrush;
        }
        else
        {
            headline = "Разбор журнала: явных причин не найдено";
            tone = TileForeground;
        }

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        if (report.CrashReport != null)
        {
            string file = report.CrashReport;
            // Отчёт лежит в папке crash-reports рядом с ядром: в нём версия, список модов и
            // полный стек, то есть ровно то, чего в хвосте журнала не хватает.
            var open = Btn("Открыть отчёт", (_, _) => OpenUrl(file));
            DockPanel.SetDock(open, Dock.Right);
            header.Children.Add(open);
        }
        header.Children.Add(Txt(headline, 13, true, tone));
        stack.Children.Add(header);

        const int shown = 3;
        foreach (LogTriage.Finding finding in report.Findings.Take(shown))
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            block.Children.Add(Txt(
                finding.Hits > 1 ? $"{finding.Title} · {finding.Hits} {TimesWord(finding.Hits)} в журнале" : finding.Title,
                13, true,
                finding.Kind == LogTriage.Level.Fatal ? WarnBrush : TileForeground));

            // Строка, по которой сработало правило, обязательна: без неё разбор — просто мнение.
            block.Children.Add(EvidenceLine(LogTriage.Cut(finding.Evidence, 240)));
            block.Children.Add(Txt(finding.Fix, 12));
            stack.Children.Add(block);
        }

        if (report.Findings.Count > shown)
            stack.Children.Add(MutedLine($"Ещё {report.Findings.Count - shown} — они в журнале ниже."));

        if (fatal > 0)
            stack.Children.Add(MutedLine(report.StartedOk
                ? "В хвосте журнала есть строка готовности сервера: он поднимался, и обрыв случился уже в работе."
                : "До строки готовности For help, type ни один запуск в этом хвосте не дошёл — сервер не поднимался.", 11));

        if (report.Harmless > 0)
            stack.Children.Add(MutedLine(
                $"Строк уровня ERROR/WARN, которые моды и ядро пишут в штатной работе: {report.Harmless}. " +
                "По ним панель тревогу не поднимает.", 11));

        if (report.Unclear > 0)
            stack.Children.Add(MutedLine(
                $"Ещё {report.Unclear} пугающих строк разбор не узнаёт: читать их нужно глазами в журнале ниже.", 11));

        return stack;
    }

    private TextBlock EvidenceLine(string line) =>
        new()
        {
            Text = line,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = (Brush)FindResource("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 5),
        };

    // Стиль TextBox гасит PreviewMouseWheel, чтобы поля в одну строку не ездили от колеса.
    // Обработчик элемента навешивается после EventSetter стиля, поэтому колесо возвращаем здесь.
    private void ConsoleLog_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox box) return;
        for (int i = 0; i < 3; i++)
        {
            if (e.Delta > 0) box.LineUp();
            else box.LineDown();
        }
        e.Handled = true;
    }

    // Живая нагрузка сервера: оперативная память и процессор, по две минуты истории на графике.
    // Считается по процессу java, поэтому для запуска через run.bat панель честно говорит, что
    // измерить нечего: там она видит cmd, а java живёт внутри него.
    private UIElement LoadBlock(ServerProfile p)
    {
        ServerInstance? instance = Running(p);
        if (instance == null)
            return MutedLine("Сервер остановлен — нагрузку измерять не по чему. График появится сразу после запуска.");

        if (!instance.MeasuresJvmProcess)
            return WarnLine("Эта сборка стартует через run.bat: панель видит процесс cmd, а java живёт внутри него, " +
                "и график по cmd показывал бы нули вместо нагрузки сервера. Честные замеры — для запуска напрямую.");

        loadHistory = new LoadHistory(Environment.ProcessorCount);
        loadHistory.Prime(instance);
        loadInstance = instance;

        Brush accent = (Brush)FindResource("Accent");
        Brush area = accent.Clone();
        area.Opacity = 0.25;

        cpuValue = Txt("", 20, true);
        cpuNote = MutedLine("", 11);
        cpuSpark = LoadSpark(accent, area);

        ramValue = Txt("", 20, true);
        ramNote = MutedLine("", 11);
        ramSpark = LoadSpark(accent, area);

        // До первой дельты цифр нет, а пустое место под заголовком читается как сломанный график.
        cpuValue.Text = "—";
        ramValue.Text = "—";
        cpuNote.Text = "Первый замер через секунду: загрузка — это прирост между двумя снимками.";
        ramNote.Text = "Первый замер через секунду.";

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(LoadColumn("ПАМЯТЬ СЕРВЕРА", ramValue!, ramNote!, ramSpark!,
            $"Пунктир — потолок -Xmx {p.MemoryGb} ГБ. Рабочая память java обычно чуть выше кучи: она держит " +
            "и нативные части — это не утечка."));

        UIElement right = LoadColumn("ПРОЦЕССОР СЕРВЕРА", cpuValue!, cpuNote!, cpuSpark!,
            "Пунктир — одно ядро. Основной поток Minecraft упирается примерно сюда: выше — сервер разгружается " +
            "на воркеры, держится ниже одного ядра — узкое место один поток, а не машина. Верх шкалы — " +
            "типичная загрузка, а не вся машина: всплеск запуска может упираться в потолок, пик подписан " +
            "числом. Настоящие проценты — в строке над графиком.");
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        loadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LoadHistory.IntervalMs) };
        loadTimer.Tick += TickLoad;
        loadTimer.Start();
        TickLoad(null, EventArgs.Empty);

        return grid;
    }

    private Sparkline LoadSpark(Brush accent, Brush area) => new()
    {
        Height = 56,
        Margin = new Thickness(0, 8, 0, 0),
        LineBrush = accent,
        AreaBrush = area,
        GridBrush = (Brush)FindResource("Line"),
        MarkerBrush = (Brush)FindResource("Muted")
    };

    private StackPanel LoadColumn(string label, TextBlock value, TextBlock note, Sparkline spark, string caption)
    {
        var column = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        column.Children.Add(Txt(label, 10, true, (Brush)FindResource("Muted")));

        value.Margin = new Thickness(0, 4, 0, 0);
        column.Children.Add(value);

        note.Margin = new Thickness(0, 2, 0, 0);
        column.Children.Add(note);

        column.Children.Add(spark);

        TextBlock cap = MutedLine(caption, 10);
        cap.Margin = new Thickness(0, 6, 0, 0);
        column.Children.Add(cap);
        return column;
    }

    // Опрос раз в секунду, только пока страница консоли открыта.
    private void TickLoad(object? sender, EventArgs e)
    {
        if (loadHistory == null || loadInstance == null || consoleProfile == null) return;

        if (loadHistory.Add(loadInstance) == null)
        {
            if (loadInstance.Phase == ServerPhase.Stopped) FreezeLoadColumns();
            return;
        }

        int cores = loadHistory.CoreCount;
        double percent = loadHistory.PercentOfMachine(loadHistory.LastCores);

        // Верх шкалы процессора — не «все ядра разом» и не максимум: на шестнадцати ядрах линия
        // сервера, который честно упирается в один поток, лежала бы у самого низа. Берём два ядра,
        // а если типичная загрузка выше — округлённый вверх девяностопятипроцентный уровень, но не
        // больше, чем ядер у машины. Пунктир на одном ядре остаётся ориентиром в обоих случаях.
        double cpuAxis = Math.Min(cores, Math.Max(2, Math.Ceiling(loadHistory.UpperCores(0.95))));

        if (cpuValue != null)
        {
            cpuValue.Text = $"{loadHistory.LastCores:0.#} ядра из {cores}";
            // Девяносто процентов машины — это уже не «сервер работает», а «серверу тесно»:
            // красим цифру, чтобы проценты не надо было считать глазами.
            cpuValue.Foreground = percent >= 90 ? (Brush)FindResource("Warn") : TileForeground;
        }
        if (cpuNote != null)
            cpuNote.Text = $"{percent:0} % машины · пик {loadHistory.PeakCores:0.#} · среднее {loadHistory.AverageCores:0.#}";
        if (cpuSpark != null)
        {
            cpuSpark.AxisMax = cpuAxis;
            cpuSpark.MarkerValue = 1;
            cpuSpark.Values = loadHistory.Samples.Select(s => s.Cores).ToList();
        }

        // Шкала памяти — потолок -Xmx или пик, что больше, плюс запас сверху. Без запаса пунктир
        // вставал ровно в верхнюю грань графика и исчезал как раз тогда, когда памяти съедено
        // меньше всего от -Xmx: подпись обещала границу, а на экране её не было.
        double axisGb = Math.Ceiling(Math.Max(Math.Max(consoleProfile.MemoryGb, loadHistory.PeakBytes / 1073741824.0), 1) * 1.12);

        if (ramValue != null) ramValue.Text = SystemInfo.FormatSize((long)loadHistory.LastBytes);
        if (ramNote != null)
        {
            var (total, free) = SystemInfo.Memory();
            // Плоская линия у самого низа честная, но «далеко ли до потолка» по ней не оценить,
            // поэтому доля от -Xmx идёт числом рядом.
            double ofCeiling = loadHistory.LastBytes / 1073741824.0 / Math.Max(1, consoleProfile.MemoryGb) * 100;
            ramNote.Text = $"пик {SystemInfo.FormatSize((long)loadHistory.PeakBytes)} · {ofCeiling:0} % от -Xmx · " +
                $"в системе свободно {SystemInfo.FormatGb(free)} из {SystemInfo.FormatGb(total)}";
        }
        if (ramSpark != null)
        {
            ramSpark.AxisMax = axisGb;
            ramSpark.MarkerValue = Math.Min(consoleProfile.MemoryGb, axisGb);
            ramSpark.Values = loadHistory.Samples.Select(s => s.Bytes / 1073741824.0).ToList();
        }
    }

    // Процесс умер: таймер больше не нужен, а последняя история остаётся на экране — по ней видно,
    // с чем именно сервер закончил.
    private void FreezeLoadColumns()
    {
        StopLoadTimer();
        if (cpuValue != null) cpuValue.Foreground = (Brush)FindResource("Muted");
        if (ramValue != null) ramValue.Foreground = (Brush)FindResource("Muted");
        if (cpuNote != null) cpuNote.Text = "Процесс завершён — на графике последние две минуты до остановки.";
    }

    private void SendConsole()
    {
        if (consoleProfile == null || consoleInput == null) return;

        string command = consoleInput.Text.Trim();
        if (command.Length == 0) return;

        ServerInstance? instance = Running(consoleProfile);
        if (instance == null)
        {
            Dialog.Info("Сервер остановлен — писать некуда. Нажми «Играть».", "Нет процесса");
            return;
        }

        try
        {
            instance.Send(command);
            consoleInput.Clear();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Dialog.Info(ex.Message, "Команда не ушла");
        }
    }

    private void AppendConsole(ServerInstance instance, long sequence, string line)
    {
        // Чтение процесса идёт в своих потоках: в поток интерфейса переходим через Dispatcher.
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(consoleProfile, instance.Profile) || consoleLog == null) return;

            // Строки, записанные до постройки страницы, уже лежат в Text, который мы взяли из
            // буфера. Их отложенные события приходят следом в очереди Dispatcher — пропускаем.
            if (sequence <= consoleShown) return;

            consoleShown = sequence;
            consoleLog.AppendText(line + "\n");
            consoleLog.ScrollToEnd();
        });
    }

    private void OnServerFinished(ServerInstance instance)
    {
        // Событие приходит из потока чтения процесса. Разбор и очередь подъёмов заводим через
        // Dispatcher: PlanAutoRestart — async-метод, и продолжение после его пауз должно вернуться
        // в поток интерфейса, иначе WPF не позволит ему трогать страницы.
        Dispatcher.BeginInvoke(() =>
        {
            RefreshConsoleState();
            if (currentPage == "library") ShowLibrary(GlobalSearch.Text);
            PlanAutoRestart(instance);
        });
    }

    // Судьба упавшего сервера решается здесь. Два случая авто-перезапуск не трогают: закрытие
    // панели (shuttingDown) и штатная остановка (StopRequested) — в обоих сервер убрали по
    // намерению человека, и поднимать его против этого решения нельзя.
    private async void PlanAutoRestart(ServerInstance instance)
    {
        if (shuttingDown) return;

        ServerProfile p = instance.Profile;
        RestartWatch watch = Watch(instance.Folder);

        if (instance.StopRequested)
        {
            watch.Attempts = 0;
            return;
        }

        if (!p.AutoRestart) return;

        // Две минуты — граница «сервер жил нормально». Падение после неё засчитывается первым,
        // а не четвёртым подряд: иначе ночной сбой съел бы все три попытки за минуту, и сборка
        // больше не поднялась бы без человека.
        int uptime = (int)Math.Round((DateTime.UtcNow - instance.StartedAt).TotalSeconds);
        if (uptime >= 120) watch.Attempts = 0;

        if (watch.Attempts >= MaxAutoRestarts)
        {
            string text = $"Сервер падал {MaxAutoRestarts} раза подряд — панель больше не поднимает его. " +
                "Так ведёт себя сломанная сборка: битый мод, не та Java, нехватка памяти. " +
                "Причины — в журнале выше. Почини и нажми «Играть».";
            instance.Note(text);
            Dialog.Info(text, "Авто-перезапуск остановлен");
            return;
        }

        int delay = RestartDelays[watch.Attempts];
        watch.Attempts++;

        // Строка живёт в watch, а не в вызове: пишет её уже новый процесс, который ещё не создан
        // в момент, когда мы решаем перезапускать. Фразы два разных: хвост мёртвого журнала
        // подклеивается в новый, и повторённая дословно строка читалась бы там дважды подряд.
        watch.Reason = $"Авто-перезапуск {watch.Attempts} из {MaxAutoRestarts}: предыдущий процесс " +
            $"продержался {uptime} с и завершился сам.";
        instance.Note($"Сервер упал. Жду {delay} с и пробую поднять заново — попытка " +
            $"{watch.Attempts} из {MaxAutoRestarts}.");

        await Task.Delay(TimeSpan.FromSeconds(delay));

        // За паузу могло измениться всё: панель закрывают, человек уже нажал «Играть» сам.
        if (shuttingDown || Running(p) != null) return;
        await StartAsync(p, false, instance);
    }

    // Перезагрузка по ЧЧ:ММ — раз в сутки, пока панель открыта. Смысл один: сервер, который
    // никто не трогает, со временем начинает тормозить, а руками его перезапускают редко.
    private async void TickScheduledRestarts(object? sender, EventArgs e)
    {
        if (shuttingDown) return;

        foreach (ServerInstance instance in servers.Values
                     .Where(i => i.Phase == ServerPhase.Running && i.Profile.RestartAt.Length > 0)
                     .ToList())
        {
            if (!TimeSpan.TryParse(instance.Profile.RestartAt, out TimeSpan at) || at.Days > 0) continue;

            DateTime target = DateTime.Today + at;
            if (DateTime.Now < target) continue;

            // Сервер подняли уже после этой отметки — значит время в настройках относится к
            // прошедшему часу, а не к будущему: человек запустил сборку в 08:30, а перезапуск
            // стоит на 08:00. Дёргать его через полчаса после его же старта никто не просил.
            if (instance.StartedAt >= target.ToUniversalTime()) continue;

            // Просрочка дальше пяти минут — это не «опоздали», а та же отметка из прошлого:
            // панель открыли вечером, а в настройках стоит утренний час. Завтра он придёт снова.
            if (DateTime.Now - target > TimeSpan.FromMinutes(5)) continue;

            RestartWatch watch = Watch(instance.Folder);
            if (watch.PlannedAt >= target) continue;
            watch.PlannedAt = target;

            await ScheduledRestartAsync(instance, target);
        }
    }

    // Останавливаем штатно — той же дорогой, что и кнопка «Стоп» (save-all, потом stop), — и
    // поднимаем заново. Если сервер не уложился в отведённое время, процесс снимаем: иначе он
    // остался бы висеть без консоли, а перезапуск так и не случился.
    private async Task ScheduledRestartAsync(ServerInstance instance, DateTime target)
    {
        int uptime = (int)Math.Round((DateTime.UtcNow - instance.StartedAt).TotalSeconds);
        Watch(instance.Folder).Reason = $"Плановый перезапуск в {target:HH:mm}: предыдущий процесс отработал " +
            $"{uptime} с, мир сохранён и сервер остановлен по расписанию. Поднимаю заново.";

        instance.Note($"Плановый перезапуск на {target:HH:mm}. Останавливаю сервер…");
        instance.Stop();

        if (!await instance.WaitForShutdownAsync(20))
        {
            instance.Note("За 20 секунд сервер не закрылся — снимаю процесс. Последние изменённые чанки " +
                "могли не лечь на диск.");
            instance.Kill();
            await instance.WaitForShutdownAsync(5);
        }

        if (shuttingDown || Running(instance.Profile) != null) return;
        await StartAsync(instance.Profile, false, instance);
    }

    // Таймер плановой перезагрузки: полминуты — это проще, чем будильник на точную минуту, и
    // тот же результат в пределах одного интервала опроса.
    private void StartRestartTimer()
    {
        restartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        restartTimer.Tick += TickScheduledRestarts;
        restartTimer.Start();
    }

    private void RefreshConsoleState()
    {
        if (consoleProfile == null) return;

        ServerInstance? instance = Running(consoleProfile);
        if (consoleState != null)
        {
            if (instance == null) consoleState.Text = "Остановлен — журнал ниже сохранился.";
            else if (instance.Phase == ServerPhase.Stopping) consoleState.Text = "Останавливается…";
            // Про авто-перезапуск говорим прямо здесь: человек должен видеть, что падение этого
            // сервера панель попытается залечить сама, а не ждать ручного «Играть».
            else consoleState.Text = $"Запущен · процесс {instance.ProcessId}" +
                (instance.Profile.AutoRestart ? " · авто-перезапуск включён" : "");
        }

        if (consoleStart != null) consoleStart.IsEnabled = instance == null;
        if (consoleStop != null) consoleStop.IsEnabled = instance != null;

        // Тот же момент, когда кнопка «Играть» снова стала доступной, — пора показывать разбор:
        // процесс умер, и читать теперь нужно причину, а не бегущую строку.
        RefreshTriage();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Таймер снимаем до вопроса: модальный диалог качает Dispatcher, и тик плановой
        // перезагрузки мог бы прийти ровно в тот момент, когда человек решает, закрывать ли панель.
        restartTimer?.Stop();

        var alive = servers.Values.Where(i => i.Phase != ServerPhase.Stopped).ToList();
        if (alive.Count > 0)
        {
            string names = string.Join(", ", alive.Select(i => i.Profile.Name));
            if (!Dialog.Confirm(
                    $"Запущены серверы: {names}.\n\nЗакрою панель — отправлю им команду stop и дождусь завершения " +
                    "(до 15 секунд на каждый), иначе они останутся висеть без консоли.\n\nОстановить и закрыть?",
                    "Серверы ещё работают"))
            {
                // Отказ — панель живёт дальше, и её расписание тоже.
                restartTimer?.Start();
                e.Cancel = true;
                base.OnClosing(e);
                return;
            }
        }

        // Точка невозврата: серверы сейчас остановим мы сами, и ни один из них не должен
        // подняться после этого — ни по часам, ни как «упавший сам».
        shuttingDown = true;
        restartTimer = null;

        foreach (ServerInstance instance in alive) instance.Stop();
        try
        {
            Task.WhenAll(alive.Select(i => i.WaitForShutdownAsync(15))).Wait(TimeSpan.FromSeconds(18));
        }
        catch (AggregateException)
        {
            // Часть процессов не дождались — их снимит Dispose ниже.
        }

        foreach (ServerInstance instance in servers.Values) instance.Dispose();
        servers.Clear();

        // Размер окна помним только здесь, когда закрытие стало неизбежным: пока человек не ответил
        // на вопрос про живые серверы, ничего сохранять нельзя — панель могла остаться открытой.
        WindowGeometry.Capture(this).Save(dataDir);

        base.OnClosing(e);
    }

    private void GlobalSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (currentPage == "library" && !_editing) ShowLibrary(GlobalSearch.Text);
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowHome();
    private void Library_Click(object sender, RoutedEventArgs e) => ShowLibrary();
    private void Catalog_Click(object sender, RoutedEventArgs e) => ShowCatalog();
    private void Downloads_Click(object sender, RoutedEventArgs e) => ShowDownloads();
    private void Files_Click(object sender, RoutedEventArgs e) => ShowFiles();
    private void Metrics_Click(object sender, RoutedEventArgs e) => ShowMetrics();
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    // Перетаскивание, двойной клик и пиктограмма «восстановить» обрабатывает WindowChrome и триггеры в XAML.
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

// Пункт свёрнутого меню плитки: подпись и действие.
public sealed class MenuEntry
{
    public MenuEntry(string label, Action run)
    {
        Label = label;
        Run = run;
    }

    public string Label { get; }
    public Action Run { get; }
}

public class ServerProfile
{
    public string Name { get; set; } = "Новая конфигурация";
    public string Edition { get; set; } = "Java";
    public string Version { get; set; } = "1.21.1";
    public string Core { get; set; } = "Paper";
    public int Port { get; set; } = 25565;
    public int MemoryGb { get; set; } = 4;
    public string Folder { get; set; } = "";

    // Название, которое игрок видит в списке серверов. Vanilla-сервер показывает именно motd —
    // отдельного «имени сервера» в server.properties нет.
    public string Motd { get; set; } = "";

    // null означает «в файле уже написано другое — не трогаем». Иначе запуск каждый раз
    // возвращал бы к значениям лаунчера правки, сделанные руками в server.properties.
    public int? MaxPlayers { get; set; }
    public bool? OnlineMode { get; set; }

    // Дистанция прорисовки (view-distance): сколько чанков сервер успевает отдать игроку.
    public int? ViewDistance { get; set; }

    // Сложность сервера (difficulty): ровно то слово, которое ждёт server.properties — peaceful,
    // easy, normal или hard. Пустая строка значит «файл не трогаем», как и у чисел выше; храним
    // строкой, а не перечислением, чтобы значение читалось в servers.json и правилось руками.
    public string Difficulty { get; set; } = "";

    // allow-end из bukkit.yml: null — не трогаем файл, false — в Энд не попасть. Понимается
    // только ядрами на Bukkit, поэтому применяется выборочно (см. BukkitYaml.Supports).
    public bool? AllowEnd { get; set; }

    // Копия мира перед каждым запуском. По умолчанию выключена: мир бывает на десятки гигабайт,
    // и молча снимать его перед каждым стартом — значит каждый раз ждать и занимать диск.
    public bool AutoBackup { get; set; }

    // Поднимать сборку заново, если сервер умер сам. Остановка кнопкой или закрытие панели под
    // этот случай не попадают — иначе «Остановить» означало бы «остановить на пять секунд».
    public bool AutoRestart { get; set; }

    // Плановый перезапуск раз в сутки в формате «ЧЧ:ММ»; пусто — по расписанию не трогаем.
    // Храним строкой, а не TimeSpan: так поле читается в servers.json без форматтера, и человек
    // может поправить его руками.
    public string RestartAt { get; set; } = "";

    // Сколько копий держать. Старые удаляются только из каталога копий панели и только свои.
    public int BackupKeep { get; set; } = 3;

    // Своя Java для этой сборки: полный путь к java.exe. Пусто — автоподбор по версии Minecraft.
    // Хранится только путь, никогда не версия: версия читается из файла каждый раз, поэтому
    // обновлённая на том же месте JDK не может «застрять» в конфигурации старым числом.
    public string JavaPath { get; set; } = "";

    // Путь к картинке, из которой при запуске делается 64×64 server-icon.png.
    public string IconFile { get; set; } = "";

    // Сборка помечена как нужная. Хранится в профиле, а не в памяти интерфейса: тогда отметка
    // живёт в servers.json, ездит вместе с «Экспорт JSON» и возвращается после импорта.
    public bool Favorite { get; set; }
}
