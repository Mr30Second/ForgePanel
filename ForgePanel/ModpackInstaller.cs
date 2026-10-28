using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ForgePanel;

// Установка модпака Modrinth в новую сборку. Файлы берутся строго из индекса и строго с CDN
// Modrinth (см. ModpackFile.FromModrinthCdn), каждый проверяется по SHA-1 тем же кодом, что
// качает одиночные моды.
//
// Порядок: сначала список файлов, потом переопределения — так jar'ы, названные в индексе, не
// оказываются перекрытыми таким же jar'ом из overrides. Ничего не перезаписывается: сборка
// создаётся под пак, а если файл уже стоит, его оставляем как есть.
//
// Качаем во временную папку вне сборки и переносим готовый файл на место: отмена или битая
// контрольная сумма не должны оставить в mods огрызок, который сервер попытается загрузить.
public static class ModpackInstaller
{
    public sealed record Result(int Downloaded, int Skipped, long Bytes, List<string> Unpacked);

    // Временная папка скачивания — вне сборки, рядом с её копиями: сервер читает только папку
    // сборки, и огрызок недокачанного файла не должен попасться ему на глаза.
    public static string StagingRoot(string dataDir, string profileName)
    {
        string safe = profileName;
        foreach (char bad in Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');
        return Path.Combine(dataDir, "modpack-staging", safe);
    }

    public static async Task<Result> InstallAsync(ModpackIndex index, string buildFolder, string stagingRoot,
        IProgress<(long Done, long Total)> progress, IProgress<string>? stage, CancellationToken ct)
    {
        Directory.CreateDirectory(buildFolder);

        var wanted = index.Files.Where(f => f.Installable).ToList();
        int skipped = index.Files.Count - wanted.Count;
        long declared = index.TotalBytes;
        long baseDone = 0;
        int number = 0;

        string staging = Path.Combine(stagingRoot, "staging");
        PackFiles.TryDeleteTree(staging);
        Directory.CreateDirectory(staging);

        // Прогресс складываем в одну шкалу на весь пак: сто отдельных полос одна за другой
        // выглядят как зависшая установка, а не как медленная.
        var step = new PackFiles.Inline(report => progress.Report(
            (baseDone + report.Done, Math.Max(declared, baseDone + report.Total))));

        try
        {
            foreach (ModpackFile file in wanted)
            {
                ct.ThrowIfCancellationRequested();
                number++;
                stage?.Report($"Файл {number} из {wanted.Count}: {file.FileName}");

                var source = new ModrinthFile(
                    file.Url, file.FileName, true, file.Size,
                    new ModrinthHashes(file.Sha1.Length > 0 ? file.Sha1 : null, null));

                string saved = await Modrinth.DownloadAsync(source, staging, step, ct);
                long length = new FileInfo(saved).Length;
                string target = Path.Combine(buildFolder, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                PackFiles.Place(saved, target);
                baseDone += length;
            }

            List<string> unpacked = ExtractOverrides(index, buildFolder, ct);
            return new Result(wanted.Count, skipped, baseDone, unpacked);
        }
        finally
        {
            PackFiles.TryDeleteTree(staging);
            // И папку временного скачивания этого профиля: после удаления staging от неё остаётся
            // пустая директория, а огрызкам каталогов в dataDir копиться не за чем.
            PackFiles.TryDeleteTree(stagingRoot);
        }
    }

    // Переопределения — общие (действуют и для клиента, и для сервера) и серверные; клиентская
    // папка серверу не нужна. Размещением и защитой от выхода за сборку занимается PackFiles.
    private static List<string> ExtractOverrides(ModpackIndex index, string buildFolder, CancellationToken ct)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(index.ServerOverrideFolder)) roots.Add(index.ServerOverrideFolder);
        if (!string.IsNullOrWhiteSpace(index.OverrideFolder)) roots.Add(index.OverrideFolder);
        if (roots.Count == 0) return new List<string>();

        return PackFiles.ExtractOverrides(index.Archive, roots, buildFolder, ct);
    }
}
