using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace ForgePanel;

// Карта — это папка мира с level.dat, а не файл в mods. Лаунчер распаковывает zip рядом со
// server.properties и подписывает новую папку в level-name, чтобы сервер стартовал именно её.
public static class MapInstaller
{
    public sealed record Installed(string Folder, string LevelName, int Files);

    public static async Task<Installed> InstallAsync(string archive, string buildFolder, CancellationToken ct)
    {
        if (!File.Exists(archive)) throw new FileNotFoundException("Архив с картой не найден.", archive);
        if (!Directory.Exists(buildFolder)) throw new DirectoryNotFoundException("Папка сборки не существует: " + buildFolder);

        return await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(archive);

            string root = FindWorldRoot(zip);
            string target = FreePath(buildFolder, WorldName(root, archive));
            // Хвостовой разделитель важен: без него путь world2 прошёл бы проверку как «внутри» world.
            string boundary = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

            int written = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                string fullName = entry.FullName.Replace('\\', '/');
                if (fullName.Length == 0 || fullName.EndsWith("/")) continue;
                if (!fullName.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                string destination = Path.GetFullPath(Path.Combine(target, fullName[root.Length..]));
                // Zip-slip: запись вида ../../anything не должна выбраться из папки мира.
                if (!destination.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false);
                written++;
            }

            if (written == 0) throw new InvalidDataException("В архиве нет файлов, которые лаунчер умеет распаковать.");

            ServerProperties.Set(buildFolder, "level-name", Path.GetFileName(target));
            return new Installed(target, Path.GetFileName(target), written);
        }, ct);
    }

    // level.dat ищем на самой верхней глубине: карты кладут его то в корень архива, то внутрь
    // одной папки с названием мира. Всё, что выше найденной папки, в расчёт не берём.
    private static string FindWorldRoot(ZipArchive zip)
    {
        int bestDepth = int.MaxValue;
        string root = "";

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string fullName = entry.FullName.Replace('\\', '/');
            int slash = fullName.LastIndexOf('/');
            if (!string.Equals(fullName[(slash + 1)..], "level.dat", StringComparison.OrdinalIgnoreCase)) continue;

            int depth = fullName.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
            if (depth >= bestDepth) continue;

            bestDepth = depth;
            root = slash < 0 ? "" : fullName[..(slash + 1)];
        }

        if (bestDepth == int.MaxValue)
            throw new InvalidDataException("Похоже, это не карта: в архиве не нашлось level.dat.");

        return root;
    }

    private static string WorldName(string root, string archive)
    {
        string raw;
        if (root.Length > 0)
        {
            string trimmed = root.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            raw = slash < 0 ? trimmed : trimmed[(slash + 1)..];
        }
        else
        {
            raw = Path.GetFileNameWithoutExtension(archive);
        }

        foreach (char bad in Path.GetInvalidFileNameChars()) raw = raw.Replace(bad, '_');
        raw = raw.Trim().Trim('.');

        return raw.Length > 0 ? raw : "map";
    }

    // Существующий мир не трогаем: удалить чужие постройки молча — не то, что должен делать лаунчер.
    private static string FreePath(string buildFolder, string name)
    {
        string candidate = Path.Combine(buildFolder, name);
        int counter = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(buildFolder, $"{name} ({counter++})");
        }
        return candidate;
    }
}
