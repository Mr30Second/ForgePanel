using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ForgePanel;

// Одна копия мира в каталоге резервных копий панели.
public sealed record WorldCopy(string Path, string WorldName, DateTime Created, long Bytes, int Files)
{
    public string Label => Created.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
}

// Резервные копии мира. Мир — обычная папка (её имя сервер берёт из level-name), поэтому копия
// её — рекурсивное копирование файлов; серверные плагины-бэкапщики здесь не нужны.
//
// Правила, которые соблюдаем:
// · копию делаем только на остановленном сервере: на ходу region-файлы меняются в любой момент,
//   и копия оказалась бы набором кусков из разных состояний мира;
// · при откате не удаляем ничего: текущий мир переименовываем рядом с сборкой
//   (<имя>__before-restore-<дата>), и он лежит на диске, пока человек сам не решит, что
//   откат удался;
// · из старых копий удаляем только свои — папки с нашим форматом имени, и только сверх
//   настроенного числа.
public static class WorldBackup
{
    private const string StampFormat = "yyyyMMdd-HHmmss";
    private const string Separator = "__";

    // Пометка на папке мира, которую отодвинули при откате. По ней же видно, что чистить
    // придётся руками: лишние папки в сборке панель не удаляет.
    public const string RollbackMark = "before-restore";

    // Запас сверх самого копирования: мир растёт, сервер пишет логи и temp, и копирование
    // «впритык» обрывается на середине.
    private const long HeadroomBytes = 2L * 1024 * 1024 * 1024;

    // Копии лежат рядом со сборками, но не внутри них: сервер не должен найти копию как мир.
    public static string RootFor(string dataDir, string profileName) =>
        Path.Combine(dataDir, "backups", SafeName(profileName));

    // Папка текущего мира сборки. Мира может ещё не быть — первый запуск создаст.
    public static string WorldPath(string buildFolder) =>
        Path.Combine(buildFolder, ServerProperties.WorldFolder(buildFolder));

    public static long SizeOf(string path)
    {
        long total = 0;
        foreach (FileInfo file in EnumerateFiles(path)) total += file.Length;
        return total;
    }

    // Копии этой сборки, свежие в начале. Имена, не похожие на наши, не показываем: в каталоге
    // человек может держать что угодно своё, и «копия мира» для панели — только то, что создала она.
    public static List<WorldCopy> List(string root)
    {
        var copies = new List<WorldCopy>();
        if (!Directory.Exists(root)) return copies;

        string[] folders;
        try { folders = Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return copies; }

        foreach (string folder in folders)
        {
            string name = Path.GetFileName(folder);
            if (!TryParse(name, out DateTime created, out string world)) continue;

            int files = 0;
            long bytes = 0;
            foreach (FileInfo file in EnumerateFiles(folder))
            {
                files++;
                bytes += file.Length;
            }

            copies.Add(new WorldCopy(folder, world, created, bytes, files));
        }

        return copies.OrderByDescending(c => c.Created).ToList();
    }

    public static async Task<WorldCopy> CreateAsync(string buildFolder, string root,
        IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        string world = WorldPath(buildFolder);
        if (!Directory.Exists(world))
            throw new IOException($"Папки мира «{Path.GetFileName(world)}» в сборке ещё нет — копировать нечего.");

        long total = SizeOf(world);
        EnsureSpace(root, total);
        Directory.CreateDirectory(root);

        var now = DateTime.Now;
        string worldName = Path.GetFileName(world);
        string dest = Path.Combine(root, now.ToString(StampFormat, CultureInfo.InvariantCulture) + Separator + worldName);

        int copied;
        try
        {
            copied = await Task.Run(() => CopyTree(world, dest, total, progress, ct), ct);
        }
        catch (OperationCanceledException)
        {
            // Половина скопированного мира в списке «резервных копий» хуже, чем отсутствие копии.
            TryDelete(dest);
            throw;
        }

        return new WorldCopy(dest, worldName, now, total, copied);
    }

    // Откат: текущий мир отодвигаем, на его место кладём копию. Возвращает путь, куда переехал
    // прежний мир (пусто — если отодвигать было нечего).
    public static async Task<string> RestoreAsync(WorldCopy copy, string buildFolder,
        IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        if (!Directory.Exists(copy.Path))
            throw new IOException($"Копии нет на диске: {copy.Path}");

        string world = WorldPath(buildFolder);
        string name = Path.GetFileName(world);
        long total = SizeOf(copy.Path);
        EnsureSpace(buildFolder, total);

        string moved = "";
        if (Directory.Exists(world))
        {
            moved = Path.Combine(buildFolder, name + Separator + RollbackMark + "-" +
                DateTime.Now.ToString(StampFormat, CultureInfo.InvariantCulture));
            Directory.Move(world, moved);
        }

        try
        {
            await Task.Run(() => CopyTree(copy.Path, world, total, progress, ct), ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            // Копию не дописали — убираем огрызок и ставим на место прежний мир.
            TryDelete(world);
            if (moved.Length > 0)
            {
                try
                {
                    Directory.Move(moved, world);
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        $"Откат прервался ({ex.Message}), и прежний мир на место вернуть не удалось: {back.Message}\n" +
                        $"Он лежит здесь: {moved}", back);
                }
            }

            throw;
        }

        return moved;
    }

    // Удаление старых копий сверх keep. Возвращает, сколько папок убрали.
    // Список считаем по именам, без обхода файлов: метод вешают сразу после копирования,
    // и висящий на секунду интерфейс здесь ни к чему.
    public static int Trim(string root, int keep)
    {
        if (keep < 0 || !Directory.Exists(root)) return 0;

        var own = new List<(DateTime Created, string Folder)>();
        try
        {
            foreach (string folder in Directory.GetDirectories(root))
            {
                if (TryParse(Path.GetFileName(folder), out DateTime created, out _))
                    own.Add((created, folder));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        int removed = 0;
        foreach ((_, string folder) in own.OrderByDescending(x => x.Created).Skip(keep))
        {
            try
            {
                Directory.Delete(folder, true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Папку держит антивирус или проводник: останавливаемся и честно говорим,
                // сколько убрали.
                break;
            }
        }

        return removed;
    }

    public static void Delete(WorldCopy copy)
    {
        if (Directory.Exists(copy.Path)) Directory.Delete(copy.Path, true);
    }

    // Копия может быть сделана под другим именем, чем мир сервера сейчас: содержимое кладём под
    // текущим именем, иначе сервер мир не найдёт и сгенерирует новый.
    public static bool MatchesCurrentWorld(WorldCopy copy, string buildFolder) =>
        string.Equals(copy.WorldName, Path.GetFileName(WorldPath(buildFolder)), StringComparison.OrdinalIgnoreCase);

    // Копирование всего дерева; возвращает число скопированных файлов.
    private static int CopyTree(string source, string target, long total,
        IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        long done = 0;
        long reported = 0;
        int copied = 0;
        Directory.CreateDirectory(target);

        foreach (DirectoryInfo dir in new DirectoryInfo(source).EnumerateDirectories("*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir.FullName)));

        foreach (FileInfo file in new DirectoryInfo(source).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            string dest = Path.Combine(target, Path.GetRelativePath(source, file.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            file.CopyTo(dest, overwrite: true);

            copied++;
            done += file.Length;
            // Тысячи мелких файлов — это тысячи обращений к интерфейсу, поэтому прогресс
            // обновляем ступеньками, а последние цифры дописываем всегда.
            if (done - reported >= 16L * 1024 * 1024)
            {
                reported = done;
                progress.Report((done, total));
            }
        }

        progress.Report((done, total));
        return copied;
    }

    // Обход своей очередью, а не Directory.EnumerateFiles(AllDirectories): тот падает на всей
    // перечислении, стоит одному подкаталогу оказаться занятым, а нам нужно пропустить один.
    private static IEnumerable<FileInfo> EnumerateFiles(string path)
    {
        if (!Directory.Exists(path)) yield break;

        var queue = new Queue<string>();
        queue.Enqueue(path);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            string[] files = Array.Empty<string>();
            string[] dirs = Array.Empty<string>();

            try
            {
                files = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                var info = new FileInfo(file);
                if (info.Exists) yield return info;
            }

            foreach (string dir in dirs) queue.Enqueue(dir);
        }
    }

    private static void EnsureSpace(string path, long needed)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
            throw new IOException($"Не удалось определить диск для {path} — свободное место не проверить.");

        var drive = new DriveInfo(root);
        long free = drive.TotalFreeSpace;
        if (free > needed + HeadroomBytes) return;

        throw new IOException(
            $"Нужно {SystemInfo.FormatSize(needed)} и ещё 2 ГБ запаса, а на диске {drive.Name} свободно " +
            $"{SystemInfo.FormatSize(free)}. Освободи место — недописанная копия мира хуже, чем её отсутствие.");
    }

    private static bool TryParse(string folderName, out DateTime created, out string world)
    {
        created = default;
        world = "";

        int sep = folderName.IndexOf(Separator, StringComparison.Ordinal);
        if (sep <= 0) return false;
        if (!DateTime.TryParseExact(folderName[..sep], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out created))
            return false;

        world = folderName[(sep + Separator.Length)..];
        return world.Length > 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Неудачное удаление огрызка не должно перекрывать главную ошибку.
        }
    }

    private static string SafeName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
