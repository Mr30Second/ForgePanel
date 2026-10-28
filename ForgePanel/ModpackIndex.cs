using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgePanel;

// Один файл из modrinth.index.json. Путь берётся из индекса таким, каким его написал автор пака:
// моды лежат в mods, но пак может принести и resourcepacks, и config — всё это едет в ту же
// подпапку внутри сборки, а не сваливается в mods наугад.
public sealed class ModpackFile
{
    // Один путь внутри сборки: «mods/fabric-api.jar», «resourcepacks/x.zip» или просто
    // «fabric-server-mc…jar» — пак вправе положить файл и в корень, и это не обязательно мусор:
    // так Fabric-паки приносят собственный запускатель (см. ModpackIndex.PackServerJarName).
    public string RelativePath { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public long Size { get; init; }
    public string ClientEnv { get; init; } = "required";
    public string ServerEnv { get; init; } = "required";

    public string FileName => System.IO.Path.GetFileName(RelativePath);

    // Адреса в индексе обязаны вести на CDN Modrinth: пак — чужой файл, и качать по первой
    // ссылке из него с произвольного хоста панель не будет.
    public bool FromModrinthCdn => IsModrinthAddress(Url);

    private static bool IsModrinthAddress(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? host)) return false;
        if (host.Scheme != Uri.UriSchemeHttps) return false;
        return string.Equals(host.Host, "modrinth.com", StringComparison.OrdinalIgnoreCase) ||
               host.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase);
    }

    // Каталог внутри сборки, куда файл принадлежит («mods», «resourcepacks», пусто — корень).
    public string Root => System.IO.Path.GetDirectoryName(RelativePath)?.Replace('\\', '/') ?? "";

    // Сервер не качает файл, если автор пака отметил его как «unsupported» для сервера.
    public bool WantedByServer => !string.Equals(ServerEnv, "unsupported", StringComparison.OrdinalIgnoreCase);

    // Ставим только то, что серверу положено и что панель может скачать честно: адрес с Modrinth.
    public bool Installable => WantedByServer && FromModrinthCdn;

    public bool SkippedByEnv => !WantedByServer;
    public bool SkippedByHost => WantedByServer && !FromModrinthCdn;
    public string SizeText => SystemInfo.FormatSize(Size);
}

// Прочитанный modrinth.index.json. Класс ничего не пишет и не качает: он только объясняет,
// что внутри архива, — решение ставить или нет принимает человек в окне сводки.
public sealed class ModpackIndex
{
    public string Archive { get; init; } = "";
    public string Name { get; init; } = "";
    public string PackVersion { get; init; } = "";
    public string Author { get; init; } = "";
    public string GameVersion { get; init; } = "";

    // Ядро по зависимостям пака: Fabric / Quilt / Forge / NeoForge, Vanilla — если загрузчика нет.
    public string Core { get; init; } = "Vanilla";
    public string LoaderVersion { get; init; } = "";
    public int FormatVersion { get; init; }
    public List<ModpackFile> Files { get; init; } = new();
    public List<string> Notes { get; init; } = new();

    // Папки с переопределениями: общие действуют и для клиента, и для сервера, server-overrides —
    // только для сервера. Обе лежат внутри того же zip.
    public string OverrideFolder { get; init; } = "overrides";
    public string ServerOverrideFolder { get; init; } = "server-overrides";

    public int InstallableFiles => Files.Count(f => f.Installable);
    public int ClientOnly => Files.Count(f => f.SkippedByEnv);
    public int ForeignHost => Files.Count(f => f.SkippedByHost);
    public long TotalBytes => Files.Where(f => f.Installable).Sum(f => f.Size);
    public string SizeText => SystemInfo.FormatSize(TotalBytes);

    // Чем пак наполнит сборку, кроме mods: человек должен увидеть это до подтверждения.
    public string RootsText
    {
        get
        {
            var roots = Files.Where(f => f.Installable)
                .GroupBy(f => f.Root.Length == 0 ? "корень сборки" : f.Root)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} — {g.Count()}");
            return string.Join(", ", roots);
        }
    }

    public bool LoaderIsAuto => CoreInstaller.IsAuto(Core);

    // Большинство паков приносят готовый запускатель в переопределениях: внутри архива лежит
    // уже собранный server.jar, а не ссылка на него. Скачивать поверх второе ядро смысла нет:
    // в папке лягут два запускателя разных версий, и панель по имени всё равно выберет файл пака.
    // Ищем именно тот jar, который сборка сочла бы ядром: в корне, .jar, со словом «server» и без
    // «installer» — та же логика, что у BuildChecker.PickCoreJar.
    public string PackServerJarName { get; private set; } = "";

    public bool ShipsOwnServerJar => PackServerJarName.Length > 0;

    private const string IndexName = "modrinth.index.json";

    // Индекс читается из корня архива и только оттуда: если его нет, перед нами либо чужой zip,
    // либо пак другой формы, а угадывать по списку файлов панель не будет.
    public static ModpackIndex Read(string archive)
    {
        if (!File.Exists(archive)) throw new FileNotFoundException("Файл пака не найден.", archive);

        using var zip = ZipFile.OpenRead(archive);
        ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/').TrimStart('/'), IndexName, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
            throw new InvalidDataException($"В архиве нет {IndexName} в корне. Модпаки Modrinth приходят файлом " +
                                           ".mrpack; если это архив с модами из другого источника, панель его не разбирает.");

        RawIndex? raw;
        try
        {
            using var stream = entry.Open();
            // Modrinth пишет индекс обычным JSON, но пак мог быть отредактирован руками —
            // комментарии и висячая запятая не должны превращаться в «файл битый».
            raw = JsonSerializer.Deserialize<RawIndex>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{IndexName} не читается как JSON: {ex.Message}");
        }

        if (raw == null) throw new InvalidDataException($"{IndexName} оказался пустым.");

        string game = (raw.Game ?? "").Trim();
        if (game.Length > 0 && !string.Equals(game, "minecraft", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Панель ставит паки Minecraft, а этот помечен game=«{game}».");

        var index = Build(archive, raw);
        if (index.GameVersion.Length == 0)
            throw new InvalidDataException("В индексе пака нет зависимости «minecraft» — непонятно, под какую " +
                                           "версию игры создавать сборку.");
        if (index.Files.Count == 0)
            index.Notes.Add("В индексе нет ни одного файла: пак состоит только из переопределений.");

        return index;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static ModpackIndex Build(string archive, RawIndex raw)
    {
        // Загрузчик ищется по ключам зависимостей. Порядок фиксированный: NeoForge проверятся
        // раньше Forge, потому что пак теоретически может нести оба ключа, и один из них пустой.
        Dictionary<string, string?> deps = raw.Dependencies ?? new();
        string game = Value(deps, "minecraft");
        (string Core, string Key)? loader =
            Dep(deps, "fabric-loader", "Fabric") ??
            Dep(deps, "quilt-loader", "Quilt") ??
            Dep(deps, "neoforge", "NeoForge") ??
            Dep(deps, "forge", "Forge");

        var index = new ModpackIndex
        {
            Archive = archive,
            Name = (raw.Name ?? "").Trim().Length > 0 ? raw.Name!.Trim() : Path.GetFileNameWithoutExtension(archive),
            PackVersion = (raw.Version ?? "").Trim(),
            Author = (raw.Author ?? "").Trim(),
            GameVersion = game,
            Core = loader?.Core ?? "Vanilla",
            LoaderVersion = loader == null ? "" : Value(deps, loader.Value.Key),
            FormatVersion = raw.FormatVersion,
            OverrideFolder = (raw.Overrides ?? "").Trim().Length > 0 ? raw.Overrides!.Trim() : "overrides",
            ServerOverrideFolder = (raw.ServerOverrides ?? "").Trim().Length > 0
                ? raw.ServerOverrides!.Trim() : "server-overrides"
        };

        int noHash = 0, rejected = 0;
        foreach (RawFile? f in raw.Files ?? new())
        {
            if (f == null) continue;
            string path = Clean(f.Path);
            if (path.Length == 0) { rejected++; continue; }

            string url = f.Downloads?.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u)) ?? "";

            string sha1 = f.Hashes?.Sha1 ?? "";
            if (sha1.Length == 0) noHash++;

            index.Files.Add(new ModpackFile
            {
                RelativePath = path,
                Url = url.Trim(),
                Sha1 = sha1.Trim(),
                Size = f.FileSize,
                ClientEnv = f.Env?.Client ?? "required",
                ServerEnv = f.Env?.Server ?? "required"
            });
        }

        if (rejected > 0)
            index.Notes.Add($"Записей с пустым путём или с выходом за пределы сборки («../», «C:\\»): {rejected} — " +
                            "панель их не ставит.");

        // О самом «своём» запускаторе человеку говорит отдельная строка сводки; здесь только тот
        // случай, где он не помогает: Forge-family jar без библиотек установщика не заведётся, и
        // панель своё всё равно поставит. Об этом надо предупредить, иначе кажется, что файл лишний.
        index.PackServerJarName = FindOwnServerJar(index);
        if (index.ShipsOwnServerJar && ForgeInstaller.Supports(index.Core))
            index.Notes.Add($"В паке лежит «{index.PackServerJarName}», но ядро «{index.Core}» без " +
                            "развёрнутых библиотек установщика не запускается — своё панель поставит, " +
                            "и запускаться будет оно.");

        if (noHash > 0)
            index.Notes.Add($"Файлов без SHA-1 в индексе: {noHash} — панель их докачает, но сверить контрольную " +
                            "сумму не с чем, так что доверяем только адресу с cdn.modrinth.com.");

        if (index.ForeignHost > 0)
            index.Notes.Add($"Файлов, которые панель не скачает (адрес пустой или не с modrinth.com): " +
                            $"{index.ForeignHost} — их придётся положить в сборку руками.");

        if (index.Core == "Vanilla" && index.Files.Any(f => f.WantedByServer &&
            f.RelativePath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
            f.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
        {
            index.Notes.Add("В паке есть jar'ы в mods, но загрузчика в зависимостях нет: чистый Vanilla их " +
                            "не подключит. Сборку создаём — моды лягут, но сервер их увидит только после " +
                            "установки загрузчика.");
        }

        if (!index.LoaderIsAuto)
            index.Notes.Add($"Ядро «{index.Core}» панель сама не ставит ({CoreInstaller.UnsupportedReason(index.Core)}) " +
                            "Список файлов и переопределения распакуем, а jar загрузчика нужно положить в папку " +
                            "сборки до первого запуска.");

        // index.json живёт в том же архиве, что и overrides: смотрим, есть ли они вовсе, чтобы
        // в сводке не обещать распаковку того, чего в паке нет.
        if (!PackFiles.HasFolder(archive, index.OverrideFolder) &&
            !PackFiles.HasFolder(archive, index.ServerOverrideFolder))
            index.Notes.Add("Переопределений в архиве нет — config и остальные файлы сервер создаст сам.");

        if (index.FormatVersion > 1)
            index.Notes.Add($"formatVersion индекса — {index.FormatVersion}; панель читает первый формат, " +
                            "поэтому новые поля индекса (например, раздельные клиентские списки) игнорирует.");

        return index;
    }

    private static string Value(Dictionary<string, string?> deps, string key) =>
        deps.TryGetValue(key, out string? value) ? (value ?? "").Trim() : "";

    private static (string Core, string Key)? Dep(Dictionary<string, string?> deps, string key, string core) =>
        Value(deps, key).Length > 0 ? (core, key) : null;

    // Путь из индекса допускаем только относительный и без выхода за пределы сборки: «../» и
    // «C:\» в индексе означают битый или злой пак, и такой файл просто не ставится.
    private static string Clean(string? path)
    {
        string value = (path ?? "").Trim().Replace('\\', '/').TrimStart('/');
        if (value.Length == 0) return "";
        if (System.IO.Path.IsPathRooted(value)) return "";
        if (value.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..")) return "";
        return string.Join('/', value.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string FindOwnServerJar(ModpackIndex index)
    {
        var names = new List<string>();

        foreach (ModpackFile file in index.Files)
            if (file.Installable && file.Root.Length == 0 && PackFiles.LooksLikeServerJar(file.FileName))
                names.Add(file.FileName);

        names.AddRange(PackFiles.FindRootServerJars(index.Archive,
            new[] { index.ServerOverrideFolder, index.OverrideFolder }));

        return PackFiles.PickServerJar(names);
    }

    private sealed record RawHashes([property: JsonPropertyName("sha1")] string? Sha1);

    private sealed record RawEnv(
        [property: JsonPropertyName("client")] string? Client,
        [property: JsonPropertyName("server")] string? Server);

    private sealed record RawFile(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("hashes")] RawHashes? Hashes,
        [property: JsonPropertyName("env")] RawEnv? Env,
        [property: JsonPropertyName("downloads")] List<string>? Downloads,
        [property: JsonPropertyName("fileSize")] long FileSize);

    private sealed record RawIndex(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("author")] string? Author,
        [property: JsonPropertyName("game")] string? Game,
        [property: JsonPropertyName("format_version")] int FormatVersion,
        [property: JsonPropertyName("files")] List<RawFile?>? Files,
        [property: JsonPropertyName("dependencies")] Dictionary<string, string?>? Dependencies,
        [property: JsonPropertyName("overrides")] string? Overrides,
        [property: JsonPropertyName("server_overrides")] string? ServerOverrides);
}
