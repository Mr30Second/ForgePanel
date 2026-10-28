using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgePanel;

// Одна запись files[] из manifest.json: проект + конкретный номер файла. Имя файла в манифесте
// отсутствует, поэтому его придётся спросить у API — см. CurseForge.GetFileAsync.
public sealed class CursePackFile
{
    public int ProjectId { get; init; }
    public int FileId { get; init; }
    public bool Required { get; init; }

    public string Label => $"проект {ProjectId}, файл {FileId}";
}

// Прочитанный manifest.json из мода-архива CurseForge (.zip). Класс ничего не качает и не пишет:
// он объясняет, что внутри архива, — ставить или нет решает человек в окне сводки.
//
// Формат проще Modrinth-индекса: файл описан парой номеров, пути внутри сборки манифест не
// задаёт (всё идёт в mods), а контрольной суммы в нём нет — её даёт API вместе с именем файла.
public sealed class CursePackIndex
{
    public string Archive { get; init; } = "";
    public string Name { get; init; } = "";
    public string PackVersion { get; init; } = "";
    public string Author { get; init; } = "";
    public string GameVersion { get; init; } = "";

    // Ядро по modLoader.name: Forge / NeoForge / Fabric / Quilt, Vanilla — если загрузчика нет.
    public string Core { get; init; } = "Vanilla";
    public string LoaderVersion { get; init; } = "";
    public int ManifestVersion { get; init; }

    public List<CursePackFile> Files { get; init; } = new();
    public List<string> Notes { get; init; } = new();

    // Переопределения в curse-паке лежат в одной папке (по умолчанию overrides); клиентская
    // client-overrides серверу не нужна, scripts/ панель не запускает.
    public string OverrideFolder { get; init; } = "overrides";
    public bool HasOverrides { get; private set; }
    public bool HasScripts { get; private set; }
    public List<(string Folder, int Files)> OverrideContents { get; private set; } = new();

    public int RequiredFiles => Files.Count(f => f.Required);
    public int OptionalFiles => Files.Count(f => !f.Required);
    public bool LoaderIsAuto => CoreInstaller.IsAuto(Core);

    // Чем переопределения наполнят сборку — человек должен увидеть это до подтверждения.
    public string RootsText => OverrideContents.Count == 0
        ? "только mods"
        : string.Join(", ", OverrideContents.Select(c => c.Folder == "корень сборки"
            ? $"{c.Folder} — {c.Files}"
            : $"{c.Folder}/ — {c.Files}"));

    // Тот же случай, что у Modrinth-паков: в overrides мог лежать уже собранный server.jar, и
    // второе ядро рядом — лишний файл другой версии.
    public string PackServerJarName { get; private set; } = "";
    public bool ShipsOwnServerJar => PackServerJarName.Length > 0;

    private const string ManifestName = "manifest.json";

    public static CursePackIndex Read(string archive)
    {
        if (!File.Exists(archive)) throw new FileNotFoundException("Файл пака не найден.", archive);

        RawManifest raw;
        using (var zip = ZipFile.OpenRead(archive))
        {
            ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName.Replace('\\', '/').TrimStart('/'), ManifestName, StringComparison.OrdinalIgnoreCase));

            if (entry == null)
                throw new InvalidDataException($"В архиве нет {ManifestName} в корне. Модпаки CurseForge приходят " +
                    "обычным .zip с этим файлом; если это архив с модами в свободной форме (просто папка mods), " +
                    "панель его не разбирает — такие файлы кладут в сборку руками.");

            try
            {
                using var stream = entry.Open();
                // Пак мог быть поправлен руками: комментарий или висячая запятая не должны
                // превращаться в «файл битый».
                raw = JsonSerializer.Deserialize<RawManifest>(stream, JsonOptions)
                    ?? throw new InvalidDataException($"{ManifestName} оказался пустым.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{ManifestName} не читается как JSON: {ex.Message}");
            }

            var index = Build(archive, raw, zip);
            Validate(index, raw, archive);
            return index;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static CursePackIndex Build(string archive, RawManifest raw, ZipArchive zip)
    {
        string game = First(raw.MinecraftVersion, raw.Minecraft?.Version);
        string loaderName = (raw.Loader?.Name ?? "").Trim().ToLowerInvariant();
        string loaderVersion = (raw.Loader?.Version ?? "").Trim();
        if (loaderName.Length == 0) (loaderName, loaderVersion) = LoaderFromId(raw.Minecraft);

        var index = new CursePackIndex
        {
            Archive = archive,
            Name = (raw.Name ?? "").Trim().Length > 0 ? raw.Name!.Trim() : Path.GetFileNameWithoutExtension(archive),
            PackVersion = (raw.Version ?? "").Trim(),
            Author = (raw.Author ?? "").Trim(),
            GameVersion = game,
            Core = LoaderCore(loaderName),
            LoaderVersion = loaderVersion,
            ManifestVersion = raw.ManifestVersion,
            OverrideFolder = (raw.Overrides ?? "").Trim().Length > 0 ? raw.Overrides!.Trim() : "overrides"
        };

        int rejected = 0;
        var seen = new HashSet<(int, int)>();

        foreach (RawFile? f in raw.Files ?? new())
        {
            if (f == null) continue;
            int project = f.ProjectId;
            int file = f.FileId;
            if (project <= 0 || file <= 0) { rejected++; continue; }
            if (!seen.Add((project, file))) { rejected++; continue; }

            index.Files.Add(new CursePackFile
            {
                ProjectId = project,
                FileId = file,
                Required = f.Required ?? true
            });
        }

        if (rejected > 0)
            index.Notes.Add($"Записей без номеров проекта/файла и повторяющихся: {rejected} — панель их не ставит.");

        string rootPrefix = index.OverrideFolder.Replace('\\', '/').Trim('/') + "/";
        index.HasOverrides = rootPrefix.Length > 1 &&
            zip.Entries.Any(e => e.FullName.Replace('\\', '/').StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase));
        index.HasScripts = zip.Entries.Any(e =>
            e.FullName.Replace('\\', '/').StartsWith("scripts/", StringComparison.OrdinalIgnoreCase));
        index.OverrideContents = index.HasOverrides
            ? PackFiles.OverrideRoots(archive, index.OverrideFolder)
            : new List<(string, int)>();
        index.PackServerJarName = index.HasOverrides
            ? PackFiles.PickServerJar(PackFiles.FindRootServerJars(archive, new[] { index.OverrideFolder }))
            : "";

        return index;
    }

    private static void Validate(CursePackIndex index, RawManifest raw, string archive)
    {
        string type = (raw.ManifestType ?? "").Trim();
        if (type.Length > 0 && !string.Equals(type, "minecraftModpack", StringComparison.OrdinalIgnoreCase))
            index.Notes.Add($"В манифесте manifestType=«{type}», а не «minecraftModpack» — панель всё равно " +
                            "ставит по списку files[], но этот архив может быть не паком.");

        if (index.GameVersion.Length == 0)
            throw new InvalidDataException("В манифесте нет версии Minecraft: нет ни «minecraftVersion», ни " +
                "«minecraft.version». Непонятно, под какую версию создавать сборку.");

        if (index.Files.Count == 0)
            throw new InvalidDataException("В манифесте пустой список files[]: качать нечего. Если это архив " +
                                           "с уже лежащими внутри jar'ами, распакуй его в папку сборки сам.");

        if (index.Core == "Vanilla" && index.RequiredFiles > 0)
            index.Notes.Add("В манифесте нет modLoader, но файлы в списке есть: чистый Vanilla моды не " +
                            "подключит. Сборку создадим, а загрузчик придётся положить руками.");

        if (!index.LoaderIsAuto)
            index.Notes.Add($"Ядро «{index.Core}» панель сама не ставит ({CoreInstaller.UnsupportedReason(index.Core)}) " +
                            "Список файлов и переопределения распакуем, а jar загрузчика нужно положить в папку " +
                            "сборки до первого запуска.");

        if (index.ManifestVersion > 1)
            index.Notes.Add($"manifestVersion — {index.ManifestVersion}; панель читает первый формат, " +
                            "поэтому новые поля манифеста игнорирует.");

        if (index.HasScripts)
            index.Notes.Add("В архиве есть папка scripts/ — панель её не запускает: это установочные скрипты " +
                            "клиентских лаунчеров (обычно shell для Linux), серверу они не нужны.");

        if (index.ShipsOwnServerJar && ForgeInstaller.Supports(index.Core))
            index.Notes.Add($"В переопределениях лежит «{index.PackServerJarName}», но ядро «{index.Core}» без " +
                            "развёрнутых библиотек установщика не запускается — своё панель поставит, " +
                            "и запускаться будет оно.");
    }

    // CurseForge отдаёт манифест в двух формах: прежняя — «minecraftVersion» и «modLoader» рядом,
    // свежая — блок «minecraft», где версия в «version», а загрузчики лежат в «modLoaders[]» одной
    // строкой в «id». Паки, скачанные сейчас, приходят вторым видом, поэтому читаем оба.
    private static string First(params string?[] values)
    {
        foreach (string? value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return "";
    }

    // «neoforge-26.2.0.88» → имя до первого дефиса и версия после него. Если дефиса нет, считаем,
    // что это только имя загрузчика, а сборку панели подберут по версии Minecraft.
    private static (string Name, string Version) LoaderFromId(RawMinecraft? minecraft)
    {
        List<RawModLoader?>? loaders = minecraft?.ModLoaders;
        if (loaders == null) return ("", "");

        RawModLoader? chosen = loaders.FirstOrDefault(l => l?.Primary == true)
            ?? loaders.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l?.Id));

        string id = (chosen?.Id ?? "").Trim();
        if (id.Length == 0) return ("", "");

        int dash = id.IndexOf('-');
        return dash < 0
            ? (id.ToLowerInvariant(), "")
            : (id[..dash].ToLowerInvariant(), id[(dash + 1)..].Trim());
    }

    // Имена загрузчиков в манифесте пишутся строчными буквами и иногда с пробелом («neo forge»).
    private static string LoaderCore(string name) => name.Replace(" ", "").Replace("-", "") switch
    {
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "" => "Vanilla",
        _ => char.ToUpperInvariant(name[0]) + name[1..]
    };

    private sealed record RawLoader(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("version")] string? Version);

    private sealed record RawModLoader(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("primary")] bool? Primary);

    private sealed record RawMinecraft(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("modLoaders")] List<RawModLoader?>? ModLoaders);

    private sealed record RawFile(
        [property: JsonPropertyName("projectID")] int ProjectId,
        [property: JsonPropertyName("fileID")] int FileId,
        // required может быть отсутствовать: тогда считаем файл обязательным, как это делает и
        // клиентский лаунчер CurseForge.
        [property: JsonPropertyName("required")] bool? Required);

    private sealed record RawManifest(
        [property: JsonPropertyName("manifestType")] string? ManifestType,
        [property: JsonPropertyName("manifestVersion")] int ManifestVersion,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("author")] string? Author,
        [property: JsonPropertyName("minecraftVersion")] string? MinecraftVersion,
        [property: JsonPropertyName("minecraft")] RawMinecraft? Minecraft,
        [property: JsonPropertyName("modLoader")] RawLoader? Loader,
        [property: JsonPropertyName("files")] List<RawFile?>? Files,
        [property: JsonPropertyName("overrides")] string? Overrides);
}
