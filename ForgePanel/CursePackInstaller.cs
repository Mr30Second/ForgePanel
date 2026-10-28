using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ForgePanel;

// Установка модпака CurseForge (обычный .zip с manifest.json) в новую сборку.
//
// Имени файла в манифесте нет — есть пара номеров проекта и файла, поэтому перед скачиванием
// каждый файл спрашивается у API: оттуда берутся настоящее имя, размер и SHA-1, и дальше качается
// тем же кодом, что качает одиночные моды с CurseForge (сверка контрольной суммы — там же).
//
// Ставится ровно тот номер файла, который назвал автор пака, а не «самый свежий»: пак собран и
// проверен на конкретной сборке файла, а свежая версия могла разойтись с остальными модами пака.
//
// Один пропавший файл не бросает установку на середине: пак на двести файлов спотыкается об
// удалённый автором jar чаще, чем обрывается по-настоящему, а начинать заново из-за одного —
// значит перекачать сотни мегабайт. Пропущенные панель называет в итоге списком.
public static class CursePackInstaller
{
    public sealed record Result(
        int Downloaded, int Failed, int OptionalSkipped, long Bytes, List<string> Unpacked, List<string> Failures);

    public static async Task<Result> InstallAsync(CursePackIndex index, string buildFolder, string stagingRoot,
        string apiKey, IProgress<(long Done, long Total)> progress, IProgress<string>? stage, CancellationToken ct)
    {
        Directory.CreateDirectory(buildFolder);

        var wanted = index.Files.Where(f => f.Required).ToList();
        int optionalSkipped = index.Files.Count - wanted.Count;
        string modsFolder = Path.Combine(buildFolder, "mods");

        string staging = Path.Combine(stagingRoot, "staging");
        PackFiles.TryDeleteTree(staging);
        Directory.CreateDirectory(staging);

        long bytes = 0;
        int done = 0;
        var failures = new List<string>();

        try
        {
            foreach (CursePackFile file in wanted)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                string label = $"Мод {done} из {wanted.Count}";

                try
                {
                    stage?.Report($"{label}: {file.Label}…");
                    CurseFile meta = await CurseForge.GetFileAsync(file.ProjectId, file.FileId, apiKey, ct);
                    stage?.Report($"{label}: {meta.FileName}");

                    // Скачивание идёт в staging вне сборки: недокачанный или побитый огрызок не
                    // должен попасться серверу на глаза при первом запуске.
                    string saved = await CurseForge.DownloadWithRetryAsync(meta, staging, null, apiKey, ct);
                    bytes += new FileInfo(saved).Length;

                    // Имя файла берём из ответа API и обезвреживаем: Path.GetFileName отсекает всё,
                    // что автор вздумал бы положить в поле fileName вместе с именем.
                    string name = Path.GetFileName(string.IsNullOrWhiteSpace(meta.FileName)
                        ? $"curseforge-{meta.Id}.jar"
                        : meta.FileName);
                    PackFiles.Place(saved, Path.Combine(modsFolder, name));
                }
                catch (Exception ex)
                {
                    // Отмена, замаскированная под сетевую ошибку, — всё равно отмена: иначе цикл
                    // доскакивал бы до конца списка уже после нажатия «Отменить».
                    ct.ThrowIfCancellationRequested();
                    failures.Add($"{file.Label}: {ex.Message}");
                }

                // Шкала пофайловая: размер файла становится известен только после запроса к API,
                // поэтому «12 из 168» честнее полосы, растянутой по догадкам.
                progress.Report((done, wanted.Count));
            }

            // Переопределения — после списка файлов, как у Modrinth: так jar из манифеста не
            // окажется перекрытым таким же jar'ом из overrides.
            List<string> unpacked = index.HasOverrides
                ? PackFiles.ExtractOverrides(index.Archive, new[] { index.OverrideFolder }, buildFolder, ct)
                : new List<string>();

            return new Result(wanted.Count - failures.Count, failures.Count, optionalSkipped, bytes, unpacked, failures);
        }
        finally
        {
            PackFiles.TryDeleteTree(staging);
            PackFiles.TryDeleteTree(stagingRoot);
        }
    }
}
