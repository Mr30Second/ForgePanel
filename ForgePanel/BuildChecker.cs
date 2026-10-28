using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ForgePanel;

public sealed record CheckItem(bool Ok, string Title, string Detail);

public enum BuildState
{
    NotInstalled,
    NeedsAttention,
    Ready
}

// Проверка читает только то, что реально лежит на диске: файлы ядра, eula.txt,
// server.properties, наличие Java и свободное место. Ничего не выдумывается.
public static class BuildChecker
{
    public static string ResolveFolder(ServerProfile p, string dataDir)
    {
        if (!string.IsNullOrWhiteSpace(p.Folder)) return p.Folder;
        return Path.Combine(dataDir, "instances", SafeName(p.Name));
    }

    public static bool IsBedrock(ServerProfile p) =>
        string.Equals(p.Edition, "Bedrock", StringComparison.OrdinalIgnoreCase);

    public static List<CheckItem> Check(ServerProfile p, string dataDir)
    {
        var items = new List<CheckItem>();
        var folder = ResolveFolder(p, dataDir);
        bool bedrock = IsBedrock(p);

        bool exists = Directory.Exists(folder);
        items.Add(new(exists, "Папка сборки", exists ? folder : "Папки ещё нет — файлы сервера не загружены."));

        if (exists)
        {
            var core = FindCoreFile(folder, bedrock);
            items.Add(core ?? new(false, "Файл ядра", "Не найден ни один подходящий файл."));

            var eula = CheckEula(folder);
            items.Add(eula);

            items.Add(CheckProperties(folder, p, bedrock));
        }

        if (!bedrock)
        {
            // Версии сервера нужна своя Java: на восьмой сервер 1.21 умрёт по
            // UnsupportedClassVersionError, и разобрать это в журнале умеет не каждый.
            // Проверяем ровно ту, что возьмёт запуск: у сборки может быть указана своя.
            JavaChoice choice = JavaChoice.For(p);
            items.Add(choice.Ok
                ? new(true, "Java", choice.Detail + $" — серверу {p.Version} требуется {choice.Needed}+.")
                : new(false, choice.Requested.Length > 0 ? "Java (своя на сборку)" : "Java", choice.Error));
        }
        else
        {
            items.Add(new(true, "Java", "Bedrock Dedicated Server работает без Java."));
        }

        items.Add(CheckDisk(folder));
        items.Add(CheckMemory(p));

        return items;
    }

    public static BuildState QuickState(ServerProfile p, string dataDir)
    {
        var folder = ResolveFolder(p, dataDir);
        try
        {
            if (!Directory.Exists(folder)) return BuildState.NotInstalled;
            var core = FindCoreFile(folder, IsBedrock(p));
            if (core == null || !core.Ok) return BuildState.NotInstalled;
            return CheckEula(folder).Ok ? BuildState.Ready : BuildState.NeedsAttention;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BuildState.NeedsAttention;
        }
    }

    public static string StateLabel(BuildState state) => state switch
    {
        BuildState.Ready => "Файлы на месте, EULA принята",
        BuildState.NeedsAttention => "EULA не принята — сервер не запустится",
        _ => "Сервер ещё не установлен"
    };

    private static CheckItem? FindCoreFile(string folder, bool bedrock)
    {
        if (bedrock)
        {
            var exe = Path.Combine(folder, "bedrock_server.exe");
            if (File.Exists(exe)) return new(true, "Ядро сервера", "bedrock_server.exe найден.");

            // Bedrock-ядро на Java (Nukkit, PowerNukkit, «Своё ядро» из списка) — это jar, а не exe.
            // Находим его тем же перебором, но итог всё равно «не готов»: запуска Bedrock в панели нет.
            return new(false, "Ядро сервера",
                FindJavaCore(folder).Detail + " Запуск Bedrock-сборок в панели ещё не сделан.");
        }

        return FindJavaCore(folder);
    }

    private static CheckItem FindJavaCore(string folder)
    {
        // У Forge и NeoForge в папке несколько jar'ов, и угадывать среди них нечего: запускается
        // то, что написал установщик (run.bat или universal jar) — смотрим до общего перебора.
        ForgeInstaller.Launch? forge = ForgeInstaller.DescribeLaunch(folder);
        if (forge != null) return new(true, "Ядро сервера", forge.Note);

        var (jar, note) = PickCoreJar(folder);
        return new(jar != null, "Ядро сервера", note);
    }

    // Какой .jar запускать, если в папке их несколько. mods\ не трогаем: там лежат моды,
    // а не ядро. Установщики тоже отбрасываем: скачанный forge-installer лежит рядом и по размеру
    // похож на сервер, но запустить его — значит просто снова открыть установщик.
    // Дальше порядок такой: точное server.jar, потом любое имя со словом server, потом единственный
    // файл. Угадать нечем — возвращаем null и показываем, что нашли.
    public static (string? Jar, string Note) PickCoreJar(string folder)
    {
        string[] all = Directory.GetFiles(folder, "*.jar");
        string[] jars = all
            .Where(j => !Path.GetFileName(j).Contains("installer", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (jars.Length == 0)
        {
            if (all.Length == 0)
                return (null, "В папке нет ни одного .jar — устанавливать пока нечего.");
            return (null, $"В папке только установщик ({Path.GetFileName(all[0])}) — ядро ещё не развёрнуто.");
        }

        string Name(string path) => Path.GetFileName(path);
        string? pick = jars.FirstOrDefault(j => Name(j).Equals("server.jar", StringComparison.OrdinalIgnoreCase))
                       ?? jars.FirstOrDefault(j => Name(j).Contains("server", StringComparison.OrdinalIgnoreCase))
                       ?? (jars.Length == 1 ? jars[0] : null);

        if (pick == null)
        {
            string shown = string.Join(", ", jars.Select(Name).Take(4));
            return (null, jars.Length > 4 ? $"Похожих серверов несколько: {shown} … и ещё {jars.Length - 4}."
                                          : $"Похожих серверов несколько: {shown}.");
        }

        string note = "Запускаться будет " + Name(pick);
        if (jars.Length > 1) note += $" — и ещё {jars.Length - 1} .jar в папке";
        return (pick, note);
    }

    private static CheckItem CheckEula(string folder)
    {
        var file = Path.Combine(folder, "eula.txt");
        if (!File.Exists(file))
            return new(false, "Лицензионное соглашение", "eula.txt появится при первом запуске сервера, и его нужно будет принять.");

        try
        {
            bool accepted = File.ReadAllLines(file)
                .Any(line => line.Trim().StartsWith("eula=true", StringComparison.OrdinalIgnoreCase));
            return accepted
                ? new(true, "Лицензионное соглашение", "eula=true — соглашение принято.")
                : new(false, "Лицензионное соглашение", "eula.txt есть, но eula=true не установлен: сервер остановится на первом запуске.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(false, "Лицензионное соглашение", "Файл не читается: " + ex.Message);
        }
    }

    private static CheckItem CheckProperties(string folder, ServerProfile p, bool bedrock)
    {
        var file = Path.Combine(folder, "server.properties");
        if (!File.Exists(file))
            return new(false, "server.properties", "Файла нет — сервер создаст его при первом запуске.");

        string? port = ReadProperty(file, "server-port");
        if (port == null)
            return new(false, "server.properties", "Порт в файле не задан.");

        return port == p.Port.ToString()
            ? new(true, "server.properties", $"Порт {port} совпадает с конфигурацией.")
            : new(false, "server.properties", $"В файле порт {port}, в конфигурации {p.Port}. Запускаться сервер будет по порту из файла.");
    }

    private static string? ReadProperty(string file, string key)
    {
        try
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#") || !trimmed.Contains('=')) continue;
                var parts = trimmed.Split('=', 2);
                if (string.Equals(parts[0].Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return parts[1].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    private static CheckItem CheckDisk(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
                return new(false, "Свободное место", "Не удалось определить диск для этого пути.");

            var drive = new DriveInfo(root);
            long free = drive.TotalFreeSpace;
            bool ok = free > 2L * 1024 * 1024 * 1024;
            return new(ok, "Свободное место", $"{SystemInfo.FormatGb(free)} свободно на {drive.Name}" + (ok ? "" : " — меньше 2 ГБ, установка может не поместиться."));
        }
        catch (Exception ex)
        {
            return new(false, "Свободное место", "Диск недоступен: " + ex.Message);
        }
    }

    private static CheckItem CheckMemory(ServerProfile p)
    {
        var (total, free) = SystemInfo.Memory();
        long request = (long)p.MemoryGb * 1073741824L;
        if (total == 0)
            return new(false, "Оперативная память", "Объём памяти определить не удалось.");

        bool fits = request <= total;
        bool tight = request > free;
        string text = $"Запрошено {p.MemoryGb} ГБ, всего {SystemInfo.FormatGb(total)}, свободно {SystemInfo.FormatGb(free)}.";
        return new(fits && !tight, "Оперативная память", fits
            ? (tight ? text + " Свободно меньше запрошенного — при запуске закроет другие программы." : text)
            : text + " Запрос больше физического объёма.");
    }

    private static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
