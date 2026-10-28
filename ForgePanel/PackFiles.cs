using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace ForgePanel;

// Куски, общие для всех модпаков: куда положить скачанный файл, как распаковать переопределения,
// какой jar считать запускатором. Modrinth и CurseForge приносят пак по-разному, но правила
// размещения одни, и защита от zip-slip обязана быть одной копией кода, а не двумя.
internal static class PackFiles
{
    // Файл лёг и проверен — переносим его по пути из пака. Если такой уже стоит (пак мог назвать
    // один файл дважды), оставляем лежащий: молча затирать чужое панель не умеет.
    public static void Place(string source, string target)
    {
        string? directory = Path.GetDirectoryName(target);
        if (directory != null) Directory.CreateDirectory(directory);

        if (File.Exists(target))
        {
            File.Delete(source);
            return;
        }

        try
        {
            File.Move(source, target);
        }
        catch (IOException)
        {
            // Разные диски: File.Move между ними не работает — копируем и снимаем оригинал.
            File.Copy(source, target, overwrite: false);
            File.Delete(source);
        }
    }

    // Переопределения — это config/, options.txt и прочее, что автор положил в архив рядом с
    // индексом. Путь за пределами сборки отбрасывается: пак — чужой файл, и запись вида
    // «../../…» не имеет права положить файл куда-то вне папки сборки.
    public static List<string> ExtractOverrides(string archive, IEnumerable<string> rootFolders,
        string buildFolder, CancellationToken ct)
    {
        var touched = new List<string>();
        string boundary = Path.GetFullPath(buildFolder) + Path.DirectorySeparatorChar;

        using var zip = ZipFile.OpenRead(archive);
        foreach (string raw in rootFolders)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string root = raw.Replace('\\', '/').Trim('/') + "/";

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                string name = entry.FullName.Replace('\\', '/');
                if (name.Length == 0 || name.EndsWith("/")) continue;
                if (!name.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                string relative = name[root.Length..];
                if (relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..")) continue;

                string destination = Path.GetFullPath(Path.Combine(buildFolder, relative));
                if (!destination.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) continue;

                // Поверх уже стоящего не пишем: пак мог положить тот же файл и в список, и в overrides.
                if (File.Exists(destination)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false);

                string top = relative.Split('/')[0];
                if (!touched.Contains(top, StringComparer.OrdinalIgnoreCase)) touched.Add(top);
            }
        }

        return touched;
    }

    // Есть ли в архиве такая папка вовсе — нужно, чтобы в сводке не обещать распаковку того,
    // чего в паке нет.
    public static bool HasFolder(string archive, string folder)
    {
        string prefix = folder.Trim('/') + "/";
        if (prefix.Length < 2) return false;
        using var zip = ZipFile.OpenRead(archive);
        return zip.Entries.Any(e => e.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    // Верхние папки внутри переопределений с числом файлов — чтобы человек до подтверждения увидел,
    // чем именно пак наполнит сборку.
    public static List<(string Folder, int Files)> OverrideRoots(string archive, string rootFolder)
    {
        string root = rootFolder.Replace('\\', '/').Trim('/') + "/";
        if (root.Length < 2) return new List<(string, int)>();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(archive);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(root, StringComparison.OrdinalIgnoreCase) || name.EndsWith("/")) continue;

            string relative = name[root.Length..];
            if (relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..")) continue;

            int slash = relative.IndexOf('/');
            string top = slash < 0 ? "корень сборки" : relative[..slash];
            counts.TryGetValue(top, out int seen);
            counts[top] = seen + 1;
        }

        return counts.OrderByDescending(pair => pair.Value).Select(pair => (pair.Key, pair.Value)).ToList();
    }

    // Тот же выбор, что у BuildChecker.PickCoreJar: .jar в корень, со словом «server» и без
    // «installer» — скачанный установщик лежит рядом и выглядит как сервер, но запускать его нельзя.
    public static bool LooksLikeServerJar(string name) =>
        name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
        name.Contains("server", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("installer", StringComparison.OrdinalIgnoreCase);

    // Запускаторы, которые пак принёс готовыми файлами в переопределениях (а не ссылкой в списке).
    // Смотрим только самый верх папок переопределений: jar в config\ или mods\ — это не запускатель.
    public static List<string> FindRootServerJars(string archive, IEnumerable<string> rootFolders)
    {
        var names = new List<string>();
        using var zip = ZipFile.OpenRead(archive);

        foreach (string raw in rootFolders)
        {
            string prefix = raw.Replace('\\', '/').Trim('/') + "/";
            if (prefix.Length < 2) continue;

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string full = entry.FullName.Replace('\\', '/');
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                string rest = full[prefix.Length..];
                if (rest.Contains('/') || rest.Length == 0) continue;
                if (LooksLikeServerJar(rest)) names.Add(rest);
            }
        }

        return names;
    }

    // Точное server.jar важнее имени со словом «server»: в сводке надо назвать тот файл, который
    // сборка реально соберётся запускать.
    public static string PickServerJar(IEnumerable<string> names) =>
        names.FirstOrDefault(n => n.Equals("server.jar", StringComparison.OrdinalIgnoreCase))
        ?? names.FirstOrDefault() ?? "";

    public static void TryDeleteTree(string path)
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

    // Прогресс, у которого шкала одна на весь пак: отдельные полосы, идущие друг за другом,
    // выглядят как зависшая установка. Отдельный класс вместо Progress<T>, потому что отчёт должен
    // дойти сразу: с асинхронной публикацией запоздалый сигнал предыдущего файла попал бы уже
    // в новую базу отсчёта.
    internal sealed class Inline : IProgress<(long Done, long Total)>
    {
        private readonly Action<(long Done, long Total)> _send;
        public Inline(Action<(long Done, long Total)> send) => _send = send;
        public void Report((long Done, long Total) value) => _send(value);
    }
}
