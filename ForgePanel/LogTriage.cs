using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ForgePanel;

// Разбор журнала. Сырой latest.log человеку бесполезен: в нём 300 строк, из которых настоящая
// причина — одна, а вокруг неё десятки пугающих ERROR/WARN, которые моды пишут в штатном режиме.
// Таблица собрана по строкам, реально найденным в журналах на этой машине
// (instances\Vanila+\Server\logs\latest.log и C:\Servers\Server\logs\latest.log), плюс несколько
// хрестоматийных сообщений самой JVM — их панель перехватывает, только если они всплывают.
public static class LogTriage
{
    public enum Level
    {
        // Сервер из-за этого не работает прямо сейчас.
        Fatal,
        // Работает, но плохо: тормоза, нехватка памяти, спорные настройки.
        Warning,
        // Знать полезно, ругаться не за что.
        Info
    }

    public sealed class Rule
    {
        // Все подстроки обязаны встретиться в одной строке журнала: одиночка вроде «requires»
        // ловила бы половину файла.
        public required string[] Requires { get; init; }
        public required Level Kind { get; init; }
        public required string Title { get; init; }
        public required string Fix { get; init; }

        public bool Matches(string line) =>
            Requires.All(part => line.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    public sealed record Finding(Level Kind, string Title, string Fix, string Evidence, int Hits);

    // Harmless — сколько строк ERROR/WARN панель опознала как заведомо безвредный шум.
    // Unclear — сколько alarming-строк не опознано вовсе; их разбор честно не объясняет.
    // StartedOk — сервер успел подняться до обрыва, значит речь о падении в работе, а не при старте.
    public sealed record Report(
        List<Finding> Findings,
        int Harmless,
        int Unclear,
        string? CrashReport,
        bool StartedOk,
        int? ExitCode);

    private static readonly Rule[] Rules =
    {
        new()
        {
            Requires = new[] { "FAILED TO BIND TO PORT" },
            Kind = Level.Fatal,
            Title = "Порт занят: второй сервер на нём не встанет",
            Fix = "Один порт может слушать только одна java. Чаще всего это прошлый запуск этой же " +
                  "сборки, который остался висеть в фоне: сними его в диспетчере задач или дай " +
                  "панели остановить сервер кнопкой «Остановить». Если же рядом живёт другая сборка — " +
                  "разведи их по портам в «Изменить» → порт.",
        },
        new()
        {
            Requires = new[] { "Address already in use" },
            Kind = Level.Fatal,
            Title = "Порт занят: второй сервер на нём не встанет",
            Fix = "Тот же случай, названный самой java: сокет на этом порту уже кто-то держит.",
        },
        new()
        {
            Requires = new[] { "Perhaps a server is already running" },
            Kind = Level.Fatal,
            Title = "Порт занят: второй сервер на нём не встанет",
            Fix = "Сервер сам подсказывает самую вероятную причину — совпадение портов двух запусков.",
        },
        new()
        {
            Requires = new[] { "UnsupportedClassVersionError" },
            Kind = Level.Fatal,
            Title = "Ядро собрано под более новую Java, чем запустилась",
            Fix = "В строке ниже написано, какой версии class-файла просит ядро и какую знает его JVM. " +
                  "Панель подбирает Java по версии Minecraft сама, но если в настройках сборки стоит " +
                  "свой путь или сервер стартовал через run.bat, могла подняться системная java — " +
                  "проверь «Изменить» → «Java для этой сборки».",
        },
        new()
        {
            Requires = new[] { "OutOfMemoryError" },
            Kind = Level.Fatal,
            Title = "Java исчерпала лимит памяти",
            Fix = "Это -Xmx: панель выдаёт сборке ровно столько гигабайт, сколько задано в её " +
                  "настройках. Подними память, если физически на машине есть свободно, или " +
                  "уменьши нагрузку — дистанцию прорисовки и количество модов.",
        },
        new()
        {
            Requires = new[] { "A fatal error has been detected by the Java Runtime Environment" },
            Kind = Level.Fatal,
            Title = "Авария самой JVM, а не сервера",
            Fix = "Java упала на уровне нативного кода: в папке сборки должен лежать файл " +
                  "hs_err_pid<номер>.log, и первые его строки называют проблемный модуль. Виновники " +
                  "обычно — мод с нативной библиотекой или память, выставленная больше, чем есть на самом деле.",
        },
        new()
        {
            Requires = new[] { "Encountered an unexpected exception" },
            Kind = Level.Fatal,
            Title = "Исключение в рабочем потоке сервера",
            Fix = "Дальше по журналу идёт стек, а полная картина — в crash-отчёте: там версия ядра, " +
                  "моды и тот самый шаг, на котором сервер споткнулся.",
        },
        new()
        {
            Requires = new[] { "You need to agree to the EULA" },
            Kind = Level.Fatal,
            Title = "Лицензионное соглашение не принято",
            Fix = "Сервер прочитал eula.txt, увидел eula=false и не стал подниматься. Принять " +
                  "соглашение можно в меню этой сборки → «Принять EULA»: панель не подставляет " +
                  "eula=true сама, согласие должен дать человек.",
        },
        new()
        {
            Requires = new[] { "Could not find or load main class" },
            Kind = Level.Fatal,
            Title = "Java не нашла главный класс ядра",
            Fix = "Так выглядит запуск на файле, который не является рабочим jar: докачанный куском " +
                  "загрузчик, переименованный или перемещённый файл ядра. Переустанови ядро сборки.",
        },
        new()
        {
            Requires = new[] { "requires", "which is missing!" },
            Kind = Level.Fatal,
            Title = "Моду не хватает зависимости",
            Fix = "В строке журнала название указано прямо. Найди его в каталоге модов этой сборки и " +
                  "поставь — без него загрузчик не начинает.",
        },
        new()
        {
            Requires = new[] { "Can't keep up!" },
            Kind = Level.Warning,
            Title = "Сервер не успевает обрабатывать тики",
            Fix = "Мир живёт, но отстаёт: генерация, моб или слишком тяжёлые моды съедают тик. " +
                  "Обычно помогает меньшая дистанция прорисовки; разовые всплески на несколько " +
                  "секундов — норма при входе игрока или телепортации.",
        },
        new()
        {
            Requires = new[] { "SERVER IS RUNNING IN OFFLINE/INSECURE MODE" },
            Kind = Level.Info,
            Title = "Сервер не проверяет лицензии: любой может взять чужой ник",
            Fix = "online-mode=false. Для круга своих это удобно и ничему не мешает, но защита от " +
                  "подделки ника и скины выключены, а белый список работает по именам, которым " +
                  "сервер не верит. Для публичного сервера включи онлайн-режим в настройках сборки.",
        },
        new()
        {
            Requires = new[] { "recommends", "which is missing!" },
            Kind = Level.Info,
            Title = "Мод советует другой мод — это не требование",
            Fix = "Слово «recommends»: сервер запустится и так. Чаще всего так просят клиентские " +
                  "моды вроде ModMenu — они нужны игроку, а не сборке.",
        },
        new()
        {
            Requires = new[] { "Failed to load persistent groups" },
            Kind = Level.Info,
            Title = "Мод голосовых групп не нашёл свои файлы — при первом запуске это норма",
            Fix = GroupsNote,
        },
        new()
        {
            Requires = new[] { "Failed to load auto join groups" },
            Kind = Level.Info,
            Title = "Мод голосовых групп не нашёл свои файлы — при первом запуске это норма",
            Fix = GroupsNote,
        },
        // Датапак пака ссылается на элемент лут-таблицы, которого в сборке нет: «graves:entities/player»,
        // «villager_death:entities/villager» и проч. Это не опечатка в журнале и не поломка — просто
        // тот датапак рассчитан на соседа, который автор пака не положил. Уровень Info, а не безвредный
        // шум, потому что за строкой стоит настоящая невыданная награда: сервер падать из-за неё не
        // будет, но и дропа из этого датапака игрок не увидит.
        new()
        {
            Requires = new[] { "Found loot table element validation problem" },
            Kind = Level.Info,
            Title = "Датапак ждёт лут-таблицу, которой в сборке нет",
            Fix = "В строке назван отсутствующий элемент — обычно это часть другого датапака или мода, " +
                  "которого в сборке нет. Сервер из-за этого не падает: он берёт ванильный вариант лута. " +
                  "Посмотри, не потерялся ли рядом тот пак, чьё имя стоит в названии элемента.",
        },
    };

    // Уровень Info, а не молча безвредный шум, у этих строк не зря: они безобидны ровно до тех пор,
    // пока сборка не жила. Проверено на журнале мода-пака, где следом шло «Added 0 persistent
    // groups», а сервер поднялся до «Done». Если же строки появляются на сервере, который уже
    // работал, их конфиг существует и не читается — это стоит увидеть, а не спрятать в счётчик.
    private const string GroupsNote =
        "Так пишет мод групп голосового чата: сохранённых групп ещё нет, и он честно сообщает об " +
        "этом уровнем ERROR. На первом запуске это пустой файл, а не поломка. Если строки лезут " +
        "и на втором, и на третьем запуске — конфиг мода уже существует и не читается: посмотри " +
        "файлы этого мода в папке config сборки.";

    // Строчки, которые ядра и моды печатают в штатной работе. Уровня ERROR они не стесняются,
    // поэтому просто по «ERROR» в журнале пугаться нельзя — и поднимать тревогу по ним панель не будет.
    private static readonly string[][] HarmlessRules =
    {
        new[] { "Failed to load properties from file: server.properties" },
        new[] { "Exception handling console input" },
        new[] { "Invalid path in mod resource-pack", "ignoring" },
        new[] { "isn't compatible with Loader's extended semantic version format" },
        new[] { "Reference map", "could not be read" },
        new[] { "@Mixin target", "was not found" },
        new[] { "Error loading class:", "ClassNotFoundException" },
        new[] { "Warnings were found!" },
        new[] { "Exception stopping the server" },
        new[] { "Voice chat encryption is not secure" },
    };

    private const string CrashMarker = "This crash report has been saved to: ";
    private const string ExitMarker = "[ForgePanel] Процесс завершился, код возврата ";

    public static Report Analyze(string logText)
    {
        var hits = new Dictionary<string, Finding>(StringComparer.Ordinal);
        var order = new List<string>();
        int harmless = 0;
        int unclear = 0;
        string? crash = null;
        int? exit = null;
        bool started = false;
        int number = 0;

        // Mojang пишет одно предупреждение несколькими записями подряд: «SERVER IS RUNNING IN
        // OFFLINE/INSECURE MODE!» и следом три строки пояснения. Разобрав первую, панель обязана
        // молчать про остальные, иначе честный счётчик «неразобранного» показывает 3 там, где
        // человек уже всё прочитал. Помечаем поток последней разобранной строки и её номер.
        string lastTag = "";
        int lastMatched = -1;

        foreach (string raw in logText.Split('\n'))
        {
            number++;
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.Contains(CrashMarker, StringComparison.OrdinalIgnoreCase))
            {
                crash = AfterMarker(line, CrashMarker) ?? crash;
                continue;
            }

            if (line.Contains(ExitMarker, StringComparison.OrdinalIgnoreCase))
            {
                exit = ParseExitCode(AfterMarker(line, ExitMarker));
                continue;
            }

            if (line.Contains("For help, type", StringComparison.OrdinalIgnoreCase)) started = true;

            bool matched = false;
            foreach (Rule rule in Rules)
            {
                if (!rule.Matches(line)) continue;
                matched = true;

                // Одно и то же событие сервер пишет двадцать раз за запуск. Сводим его в одну
                // строку с счётчиком, а показываем самое свежее совпадение: к моменту падения
                // текст выше мог уже относиться к другой проблеме.
                if (hits.TryGetValue(rule.Title, out Finding? seen))
                    hits[rule.Title] = seen with { Hits = seen.Hits + 1, Evidence = line };
                else
                {
                    hits[rule.Title] = new Finding(rule.Kind, rule.Title, rule.Fix, line, 1);
                    order.Add(rule.Title);
                }
            }

            if (matched)
            {
                lastTag = Tag(line);
                lastMatched = number;
                continue;
            }

            if (IsAlarming(line))
            {
                if (HarmlessRules.Any(parts => parts.All(p => line.Contains(p, StringComparison.OrdinalIgnoreCase))))
                    harmless++;
                else if (!ContinuesMatchedLine(lastTag, lastMatched, number, line))
                    unclear++;
            }
        }

        var findings = order.Select(t => hits[t])
            .OrderBy(f => f.Kind)
            .ThenByDescending(f => f.Hits)
            .ToList();

        return new Report(findings, harmless, unclear, IfOnDisk(crash), started, exit);
    }

    // «[20:52:29] [Server thread/WARN]: …» — поток и уровень второй скобки. Пусто для строк,
    // которые в таком формате не написаны: вывод самой панели, стек-трейсы, stderr.
    private static string Tag(string line)
    {
        int first = line.IndexOf('[');
        if (first < 0) return "";
        int firstEnd = line.IndexOf(']', first + 1);
        if (firstEnd < 0) return "";

        int second = line.IndexOf('[', firstEnd + 1);
        if (second < 0) return "";
        int secondEnd = line.IndexOf(']', second + 1);

        return secondEnd < 0 ? "" : line[(second + 1)..secondEnd];
    }

    // Та же запись, что и разбор выше: тот же поток и уровень, и стоит в пределах нескольких строк.
    private static bool ContinuesMatchedLine(string lastTag, int lastMatched, int number, string line) =>
        lastTag.Length > 0 && lastMatched >= 0 && number - lastMatched <= 5 &&
        string.Equals(Tag(line), lastTag, StringComparison.Ordinal);

    // Строка, от которой у человека должно ёкнуть: уровень ERROR/WARN из лога ядра или вывод stderr
    // процесса.
    private static bool IsAlarming(string line)
    {
        string text = line.TrimStart();
        bool stderr = text.StartsWith("[stderr]", StringComparison.OrdinalIgnoreCase);
        if (stderr) text = text["[stderr]".Length..].TrimStart();

        // Кадр стек-трейса сам по себе не новость: их бывает по десятку на одну ошибку, и считать
        // их отдельными неразобранными строками значит раздуть счётчик до бессмыслицы.
        if (text.StartsWith("at ", StringComparison.Ordinal)) return false;

        return stderr ||
               text.Contains("/ERROR]", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("/WARN]", StringComparison.OrdinalIgnoreCase);
    }

    private static string? AfterMarker(string line, string marker)
    {
        int index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        string rest = line[(index + marker.Length)..].Trim();
        return rest.Length == 0 ? null : rest;
    }

    private static int? ParseExitCode(string? tail)
    {
        if (tail == null) return null;

        // «0.» или «-1» — берём столько символов, сколько реально похоже на число.
        int end = 0;
        if (tail[0] == '-') end = 1;
        while (end < tail.Length && char.IsDigit(tail[end])) end++;

        return end > 0 && int.TryParse(tail[..end], out int code) ? code : null;
    }

    // Путь сервер пишет абсолютный и в свою папку; файла мог уже и не быть — кнопки тогда не будет.
    private static string? IfOnDisk(string? path) =>
        path != null && File.Exists(path) ? path : null;

    public static string Cut(string line, int max) =>
        line.Length <= max ? line : line[..max] + "…";
}
