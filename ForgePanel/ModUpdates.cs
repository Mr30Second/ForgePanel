using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ForgePanel;

public enum ModState
{
    // Файл посчитан, но Modrinth по нему ещё не спрашивали.
    Pending,
    // На Modrinth есть файл этого же проекта свежее — есть чем обновить.
    Outdated,
    // Лежащий файл и есть самый свежий под версию сборки.
    Current,
    // Проект найден, но под версию сборки (или загрузчик) более свежего файла на Modrinth нет.
    NotForThisBuild,
    // Такого файла в базе Modrinth нет: он с CurseForge, собран руками или это не мод вовсе.
    Unrecognized,
    // Файл не читается или Modrinth не ответил.
    Failed
}

// Один файл из папки mods и всё, что о нём удалось узнать. Пустые строки означают именно
// «не узнали», а не «не знаем, чем заполнить»: панель рисует по ним честный статус.
public sealed class ModUpdate
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required long Bytes { get; init; }
    public string Sha1 { get; set; } = "";
    public ModState State { get; set; } = ModState.Pending;
    public string Note { get; set; } = "";
    public string? ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string? IconUrl { get; set; }
    public string PageUrl { get; set; } = "";
    public string Installed { get; set; } = "";
    public ModrinthVersion? Available { get; set; }
    public ModrinthFile? AvailableFile { get; set; }

    // На сколько записей в списке лежащий файл отстал от свежей. 0 — не считали или не отставал.
    public int Behind { get; set; }

    // Внутри этого класса имя Path занято свойством, поэтому полный путь к файлу берём через
    // System.IO — иначе «Path.GetFileNameWithoutExtension» читалось бы как вызов у строки.
    public string Label => Title.Length > 0 ? Title : System.IO.Path.GetFileNameWithoutExtension(FileName);
    public string AvailableVersion => Available?.VersionNumber ?? "";
    public bool CanUpdate => AvailableFile != null;
    public string SizeText => SystemInfo.FormatSize(Bytes);
}

// Что реально легло в mods: путь к новому файлу и путь, куда панель отодвинула прежний.
public sealed record AppliedUpdate(string NewFile, string BackupFile);

// Обновления модов. Идентификация лежит в одном месте и она одна: SHA-1 файла ищется на Modrinth,
// и только по совпадению панель вообще называет файл «модом X версии Y».
//
// Правила, которые соблюдаем:
// · по имени файла не гадаем: не совпала сумма — пишем «не опознан», а не «похоже на Sodium»;
// · предлагем только то, что подходит самой сборке: версия Minecraft из конфигурации и загрузчик;
// · ничего не удаляем: прежний jar переезжает в каталог копий панели и возвращается на место,
//   если новый файл не лёг;
// · качаем во временную папку вне сборки, чтобы отменённая загрузка не оставила в mods огрызок,
//   который сервер потом попытается загрузить как мод.
public static class ModUpdates
{
    private const string StampFormat = "yyyyMMdd-HHmmss";
    private const string Staging = "staging";

    // Папка, куда панель кладёт файлы каталога и откуда сервер берёт моды.
    public static string ModsFolder(string buildFolder) => Path.Combine(buildFolder, "mods");

    // Копии вытесненных jar'ов — рядом со сборками, но не внутри них.
    public static string RootFor(string dataDir, string profileName) =>
        Path.Combine(dataDir, "mod-backups", SafeName(profileName));

    // Загрузчик, под который ищем свежие файлы. Ядро знает про mods только для загрузчиков модов;
    // остальным (Vanilla, Paper, «Своё ядро») фильтр снимаем и берём загрузчик из подписи файла.
    public static string? LoaderFor(string? core) => core switch
    {
        "Fabric" => "fabric",
        "Quilt" => "quilt",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        _ => null
    };

    // Только .jar верхнего уровня: вложенные папки сервер как моды не читает, а чтениеWhole файла
    // размером в сотни мегабайт целиком в память вынесло бы панель.
    public static Task<List<ModUpdate>> ScanAsync(string modsFolder, CancellationToken ct) =>
        Task.Run(() => Scan(modsFolder), ct);

    // Файлы в mods, которые .jar'ами не являются: о них говорим отдельной строкой, чтобы человек
    // видел — панель их не потеряла, а просто не считает модами.
    public static int CountOtherFiles(string modsFolder)
    {
        if (!Directory.Exists(modsFolder)) return 0;

        try
        {
            return Directory.GetFiles(modsFolder).Count(f =>
                !string.Equals(Path.GetExtension(f), ".jar", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // Обход и хэши — в рабочем потоке; дальше идут сетевые запросы, по одному на файл.
    private static List<ModUpdate> Scan(string modsFolder)
    {
        var items = new List<ModUpdate>();
        if (!Directory.Exists(modsFolder)) return items;

        string[] files;
        try
        {
            files = Directory.GetFiles(modsFolder, "*.jar");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return items;
        }

        foreach (string path in files.OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) continue;

            var item = new ModUpdate { Path = path, FileName = info.Name, Bytes = info.Length };
            try
            {
                item.Sha1 = Sha1OfFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                item.State = ModState.Failed;
                item.Note = "Файл не читается: " + ex.Message;
            }

            items.Add(item);
        }

        return items;
    }

    // onResolved приходит после каждого файла: список из двадцати модов не должен ждать
    // последнего запроса, чтобы показать хоть что-то.
    public static async Task CheckAsync(List<ModUpdate> items, string gameVersion, string? buildLoader,
        Action<ModUpdate>? onResolved, CancellationToken ct)
    {
        foreach (ModUpdate item in items)
        {
            if (item.State == ModState.Failed)
            {
                onResolved?.Invoke(item);
                continue;
            }

            try
            {
                await IdentifyAsync(item, gameVersion, buildLoader, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                item.Available = null;
                item.AvailableFile = null;
                item.State = ModState.Failed;
                item.Note = ex.Message;
            }

            onResolved?.Invoke(item);
        }
    }

    private static async Task IdentifyAsync(ModUpdate item, string gameVersion, string? buildLoader, CancellationToken ct)
    {
        ModrinthVersion? local = await Modrinth.GetVersionByHashAsync(item.Sha1, ct);
        if (local?.ProjectId == null)
        {
            item.State = ModState.Unrecognized;
            item.Note = "Файла с такой контрольной суммой в базе Modrinth нет: он скачан с CurseForge, " +
                        "собран руками или это не мод. По имени файла панель ничего не угадывает, " +
                        "поэтому и обновлений не обещает.";
            return;
        }

        item.ProjectId = local.ProjectId;
        item.Installed = local.VersionNumber;

        ModrinthProjectInfo? project = await Modrinth.GetProjectAsync(local.ProjectId, ct);
        if (project != null)
        {
            if (!string.IsNullOrWhiteSpace(project.Title)) item.Title = project.Title!;
            item.IconUrl = Modrinth.ResolveIconUrl(project.IconUrl);
            item.PageUrl = Modrinth.ProjectPageUrl(project.Slug, project.ProjectType);
        }

        // Один загрузчик у файла — фильтруем по нему; несколько (fabric + quilt) — оставляем фильтр
        // снятым и предупреждаем, что выбирать придётся человеку.
        string? loader = buildLoader ?? (local.Loaders?.Count == 1 ? local.Loaders![0] : null);
        List<ModrinthVersion> fresh = await Modrinth.GetVersionsAsync(local.ProjectId, gameVersion, loader, ct);
        Decide(item, local, fresh, gameVersion, loader);

        if (buildLoader != null && local.Loaders is { Count: > 0 } &&
            !local.Loaders.Any(l => string.Equals(l, buildLoader, StringComparison.OrdinalIgnoreCase)))
        {
            item.Note += $" Лежащий файл помечен загрузчиками {string.Join(", ", local.Loaders)}, " +
                         $"а сборка на {buildLoader} — сверь это со списком модов сервера.";
        }
    }

    private static void Decide(ModUpdate item, ModrinthVersion local, List<ModrinthVersion> fresh,
        string gameVersion, string? loader)
    {
        string where = $"под Minecraft {gameVersion}" + (loader == null ? "" : $", загрузчик {loader}");
        int at = fresh.FindIndex(v => SameRecord(v, local));

        if (fresh.Count == 0)
        {
            item.State = ModState.NotForThisBuild;
            item.Note = $"На Modrinth нет файлов этого проекта {where}. Версии Minecraft у лежащего файла: " +
                        $"{List(local.GameVersions)} — обновлять нечего, пока сборка не переедет на одну из них.";
            return;
        }

        if (at == 0)
        {
            item.State = ModState.Current;
            item.Note = $"Это самый свежий файл проекта {where}.";
            return;
        }

        // Совпадений в списке нет: лежащий файл рассчитан на другую версию игры или снят с Modrinth.
        // Тогда «новее» считаем по дате публикации, чтобы не предлагать откат под видом обновления.
        if (at < 0 && fresh[0].DatePublished <= local.DatePublished)
        {
            item.State = ModState.NotForThisBuild;
            item.Note = $"Свежее лежащего файла ({item.Installed}) {where} на Modrinth ничего нет. " +
                        "Возможно, этот jar рассчитан на другую версию игры.";
            return;
        }

        ModrinthVersion best = fresh[0];
        item.Behind = at > 0 ? at : 0;
        item.Available = best;
        item.AvailableFile = PickFile(best);
        item.State = item.AvailableFile == null ? ModState.NotForThisBuild : ModState.Outdated;

        if (item.AvailableFile == null)
        {
            item.Note = $"Модrinth прислал версию {best.VersionNumber} без файла для скачивания.";
            return;
        }

        item.Note = at > 0
            ? $"Лежащий файл идёт в списке {where} {at + 1}-м — свежее него {best.VersionNumber}."
            : $"Лежащий файл не встречается в списке версий {where}, а {best.VersionNumber} новее его по дате публикации.";
    }

    // Та же ли это запись, что лежит в mods: совпадает id версии или контрольная сумма её файла.
    private static bool SameRecord(ModrinthVersion candidate, ModrinthVersion local)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Id) &&
            string.Equals(candidate.Id, local.Id, StringComparison.Ordinal)) return true;

        string mine = Sha1(PickFile(local));
        string theirs = Sha1(PickFile(candidate));
        return mine.Length > 0 && string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase);
    }

    private static ModrinthFile? PickFile(ModrinthVersion version) =>
        version.Files == null || version.Files.Count == 0
            ? null
            : version.Files.FirstOrDefault(f => f.Primary) ?? version.Files[0];

    private static string Sha1(ModrinthFile? file) => file?.Hashes?.Sha1 ?? "";

    // Обновление одного файла. Порядок выбран так, чтобы в mods никогда не осталось половины:
    // сначала докачка и проверка SHA-1 во временной папке, потом переезд прежнего jar'а в копии,
    // и только затем новый кладём в mods. Сбой на последнем шаге возвращает прежний файл.
    public static async Task<AppliedUpdate> ApplyAsync(ModUpdate item, string buildFolder, string backupRoot,
        IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        if (item.AvailableFile == null || item.Available == null)
            throw new IOException("Панель не знает, какой файл качать: обновления для этого файла нет.");

        string mods = ModsFolder(buildFolder);
        string old = Path.Combine(mods, item.FileName);
        if (!File.Exists(old))
            throw new IOException($"Файла в папке больше нет: {old}. Перечисли моды заново.");

        string current = Sha1OfFile(old);
        if (!string.Equals(current, item.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{item.FileName} изменился с момента проверки (SHA-1 другой). " +
                                  "Перечисли моды заново, чтобы панель предлагала актуальное.");

        string stage = Path.Combine(backupRoot, Staging);
        TryDeleteTree(stage);
        Directory.CreateDirectory(stage);

        string downloaded;
        try
        {
            downloaded = await Modrinth.DownloadAsync(item.AvailableFile, stage, progress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDeleteTree(stage);
            throw new IOException($"Новый файл не докачался: {ex.Message}", ex);
        }
        catch (OperationCanceledException)
        {
            // Огрызок недокачанного архива не должен остаться нигде, где сервер читает файлы:
            // временная папка наша, убираем её целиком.
            TryDeleteTree(stage);
            throw;
        }

        string backup = Path.Combine(backupRoot, DateTime.Now.ToString(StampFormat, CultureInfo.InvariantCulture));
        Directory.CreateDirectory(backup);
        string moved = UniqueFile(backup, item.FileName);

        try
        {
            Move(old, moved);
            string target = UniqueFile(mods, Path.GetFileName(downloaded));
            Move(downloaded, target);
            TryDeleteTree(stage);
            return new AppliedUpdate(target, moved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Возвращаем прежний файл только если он действительно уехал в копии: сбой на первом
            // шаге означает, что в mods всё ещё лежит оригинал и трогать его не нужно.
            if (File.Exists(moved) && !File.Exists(old))
            {
                try
                {
                    Move(moved, old);
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"Обновление не доведено ({ex.Message}), и вернуть {item.FileName} на место не удалось: " +
                        $"{back.Message}\nПрежний файл лежит в копиях: {moved}", back);
                }

                TryDeleteTree(stage);
                throw new IOException(
                    $"Новый файл не лёг в {mods}: {ex.Message}. Прежний {item.FileName} вернулся на место.", ex);
            }

            TryDeleteTree(stage);
            throw new IOException(
                $"{item.FileName} не обновлён: {ex.Message}. Прежний файл в mods не тронут.", ex);
        }
    }

    // File.Move не переезжает между дисками: если сборка на другом томе, копируем и снимаем
    // оригинал — к этому моменту его целая копия уже лежит в адресе назначения.
    private static void Move(string source, string target)
    {
        try
        {
            File.Move(source, target);
        }
        catch (IOException)
        {
            File.Copy(source, target, overwrite: false);
            File.Delete(source);
        }
    }

    private static string UniqueFile(string folder, string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string candidate = Path.Combine(folder, fileName);
        int counter = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(folder, $"{baseName} ({counter++}){extension}");

        return candidate;
    }

    private static string Sha1OfFile(string path)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[92160];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) sha.AppendData(buffer, 0, read);

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static string List(List<string>? values) =>
        values == null || values.Count == 0 ? "не указаны" : string.Join(", ", values.Take(6));

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Временная папка панели: не убралось — не перекрывает главную ошибку.
        }
    }

    private static string SafeName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
