using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ForgePanel;

// Forge и NeoForge отдают не готовый сервер, а установщик: он сам докачивает библиотеки,
// vanilla jar и пишет скрипт запуска. Путь поэтому длиннее, чем у Vanilla/Paper/Fabric:
// скачать installer.jar → прогнать «java -jar installer --installServer» внутри папки сборки →
// запускать сервер уже скриптом установщика, а не прямым «-jar».
public static class ForgeInstaller
{
    // Адрес проверен живым запросом: «{"promos": {"1.21.1-recommended": "52.1.20", …}}».
    private const string Promotions = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    private const string MavenRoot = "https://maven.minecraftforge.net/net/minecraftforge/forge";

    // NeoForge живёт на собственном репозитории: проекта «neoforge» на Modrinth нет (404 проверен
    // живым запросом 2026-10-24), поэтому версии берутся из maven-API самого NeoForge.
    private const string NeoForgeRoot = "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    private const string NeoForgeVersions = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";

    public static bool Supports(string? core) => core is "Forge" or "NeoForge";

    public sealed record Release(string FileName, string Url, string Source, long Size, string? Sha1);

    // pinnedLoader — номер сборки, который назвал модпак. Пусто — брать то, что советует сам
    // загрузчик (recommended для Forge, свежайшую для NeoForge).
    public static async Task<Release> ResolveAsync(string core, string mcVersion, IProgress<string>? stage,
        CancellationToken ct, string? pinnedLoader = null)
    {
        string version = (mcVersion ?? "").Trim();
        string pinned = (pinnedLoader ?? "").Trim();
        if (version.Length == 0) throw new InvalidDataException("Версия Minecraft не указана — адрес сборки построить не из чего.");

        return core == "Forge"
            ? await ResolveForge(version, pinned, stage, ct)
            : await ResolveNeoForge(version, pinned, stage, ct);
    }

    private static async Task<Release> ResolveForge(string version, string pinned, IProgress<string>? stage, CancellationToken ct)
    {
        // Пак назвал точную сборку — адрес строится по той же схеме, что и для рекомендованной
        // (проверено живым установщиком 1.21.1-52.1.20). Если репозиторий её не отдаст,
        // InstallAsync откатится на автоматическую и скажет об этом в строке состояния.
        if (pinned.Length > 0) return ForgeRelease(version, pinned, "maven.minecraftforge.net · сборка из пака");

        stage?.Report("Ищу версию Forge в promotions…");
        JsonNode promos = await CoreInstaller.GetJson(Promotions, ct);
        JsonNode? node = promos["promos"];
        if (node is null) throw new InvalidDataException("promotions_slim.json изменился: блока «promos» в нём нет.");

        // Сначала recommended — это стабильная сборка, которую советует сам Forge; latest берём,
        // только если по версии ничего не рекомендовано (например, 1.21.6).
        string? loader = Text(node, version + "-recommended") ?? Text(node, version + "-latest");
        if (string.IsNullOrWhiteSpace(loader))
            throw new InvalidDataException($"Forge не публикует сборку для «{version}» — проверь номер версии Minecraft.");

        return ForgeRelease(version, loader, "promotions + maven.minecraftforge.net");
    }

    private static Release ForgeRelease(string version, string loader, string source)
    {
        string maven = version + "-" + loader.Trim();
        string name = "forge-" + maven + "-installer.jar";
        return new Release(name, $"{MavenRoot}/{CoreInstaller.Esc(maven)}/{name}", source, 0, null);
    }

    private static async Task<Release> ResolveNeoForge(string version, string pinned, IProgress<string>? stage, CancellationToken ct)
    {
        // Нумерация NeoForge повторяет номер Minecraft без ведущей «1.»: 1.20.6 → 20.6.x,
        // 1.21 → 21.0.x (у версии без патча патч ноль), 26.3 → 26.3.0.x. Соответствие проверено
        // живым списком версий и HEAD-запросами к установщикам под каждым из этих префиксов.
        stage?.Report("Ищу сборки NeoForge на maven.neoforged.net…");
        JsonNode list = await CoreInstaller.GetJson(NeoForgeVersions, ct);
        List<string> published = (list["versions"] as JsonArray)?.OfType<JsonValue>().Select(v => v.ToString())
                                    .ToList() ?? new List<string>();

        string prefix = NeoPrefix(version) + ".";
        string? best = published
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal) && !v.Contains('-') && IsNumber(v))
            .OrderByDescending(SortKey, StringComparer.Ordinal)
            .FirstOrDefault();

        if (best is null)
            throw new InvalidDataException(
                $"NeoForge не публикует сборок вида «{prefix}…» — под версию «{version}» его нет. " +
                "Для Minecraft 1.20.1 и старше есть только Forge.");

        // Список выше — это весь реестр репозитория, так что «сборка из пака есть или нет»
        // решается без лишнего запроса. Нет её — ставим свежую и говорим об этом вслух.
        string chosen = published.Contains(pinned, StringComparer.Ordinal) ? pinned : best;
        if (pinned.Length > 0)
            stage?.Report(chosen == pinned
                ? $"NeoForge {chosen} — ровно та сборка, которую просит пак."
                : $"Сборки NeoForge {pinned} на maven нет — ставлю {chosen}.");

        string name = $"neoforge-{chosen}-installer.jar";
        string url = $"{NeoForgeRoot}/{CoreInstaller.Esc(chosen)}/{name}";

        // Репозиторий кладёт рядом .sha1 и .md5 от самого файла: если отдают, проверяем, если нет —
        // ставим без проверки, но не считаем это ошибкой.
        string sha1 = await CoreInstaller.TryGetText(url + ".sha1", ct);
        return new Release(name, url, "maven.neoforged.net", 0, sha1.Length == 40 ? sha1 : null);
    }

    private static string NeoPrefix(string mcVersion)
    {
        string value = mcVersion.StartsWith("1.", StringComparison.Ordinal) ? mcVersion[2..] : mcVersion;
        return value.Contains('.') ? value : value + ".0";
    }

    private static bool IsNumber(string version) =>
        version.Length > 0 && version.All(c => char.IsDigit(c) || c == '.');

    // Ключ сортировки: части номера приводятся к одинаковой ширине, чтобы «21.1.7» не обогнал
    // «21.1.65» при обычном сравнении строк.
    private static string SortKey(string version) => string.Join('.', version
        .Split('.')
        .Select(part => int.TryParse(part, out int number) ? number.ToString("D6") : part));

    // Полный путь установки. Возвращает то, что стало запускателем сборки — скрипт установщика
    // или universal jar, — чтобы вызывающий код мог показать пользователю, чем теперь стартует
    // сервер в этой папке.
    public static async Task<string> InstallAsync(ServerProfile p, string folder,
        IProgress<string>? stage, IProgress<(long Done, long Total)>? progress, CancellationToken ct,
        string? pinnedLoader = null)
    {
        string pinned = (pinnedLoader ?? "").Trim();
        Release release = await ResolveAsync(p.Core, p.Version, stage, ct, pinned);
        string java = JavaFor(p);

        string installer;
        try
        {
            installer = await DownloadInstallerAsync(release, folder, stage, progress, ct);
        }
        catch (InvalidDataException ex) when (pinned.Length > 0 && ex.Message.StartsWith("404", StringComparison.Ordinal))
        {
            // Сборку из пака репозиторий не отдал (сняли или номер мёртвый). Рвать из-за этого
            // установку целого мода-пака нельзя: берём то, что советует сам загрузчик, и говорим
            // об этом вслух — человек увидит, что ядро у него не то, что просил автор пака.
            stage?.Report($"Сборки {p.Core} {pinned} в репозитории нет — беру автоматическую.");
            release = await ResolveAsync(p.Core, p.Version, stage, ct);
            installer = await DownloadInstallerAsync(release, folder, stage, progress, ct);
        }

        stage?.Report("Разворачиваю установщик: он докачает библиотеки и vanilla jar, это несколько минут…");
        await RunInstallerAsync(installer, folder, java, stage, ct);

        return DescribeLaunch(folder)?.Target ?? throw new InvalidDataException(
            "Установщик отработал без ошибки, но ни run.bat, ни universal jar в папке не появились — " +
            "чем запускать, непонятно.");
    }

    private static Task<string> DownloadInstallerAsync(Release release, string folder, IProgress<string>? stage,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        stage?.Report($"{release.Source}: {release.FileName} · начинаю загрузку…");
        return CoreInstaller.InstallAsync(
            new CoreFile(release.FileName, release.Url, release.Size, null, release.Sha1, null, release.Source),
            folder, progress, ct);
    }

    // Java для установщика берётся та же логика, что и для запуска: сервер на неподходящей JDK
    // умирает молча, и лучше узнать об этом до скачивания ста мегабайт. Если у сборки указана
    // своя Java — установщик идёт именно ею: библиотеки и vanilla jar распаковывает та же
    // версия, что потом запустит сервер.
    private static string JavaFor(ServerProfile p)
    {
        JavaChoice choice = JavaChoice.For(p);
        if (!choice.Ok) throw new InvalidDataException(choice.Error);
        return choice.Install!.Path;
    }

    private static async Task RunInstallerAsync(string installer, string folder, string javaPath,
        IProgress<string>? stage, CancellationToken ct)
    {
        // Только «--installServer»: установка идёт в текущую папку, то есть в папку сборки, а
        // клиентских артефактов серверная цель и так не качает. Прочих флагов не выдумываем:
        // у разных версий установщика они называются по-разному.
        var info = new ProcessStartInfo(javaPath,
            $"-jar \"{Path.GetFileName(installer)}\" --installServer")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        using var process = Process.Start(info)
            ?? throw new IOException("Установщик не стартовал: Process.Start вернул пустой ответ.");

        var tail = new StringBuilder();
        int lines = 0;

        void Capture(object? sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            lock (tail)
            {
                tail.AppendLine(e.Data);
                // Хвост нужен только чтобы показать причину отказа — весь журнал установщика
                // держит в себе сам установщик, нам он не перепишется в память на гигабайт.
                if (tail.Length > 6000) tail.Remove(0, tail.Length - 3000);
            }

            // Каждую стрку в интерфейс — это тысячи постов в Dispatcher на пустое место.
            if (++lines % 25 == 0) stage?.Report("Установщик: " + Cut(e.Data));
        }

        process.OutputDataReceived += Capture;
        process.ErrorDataReceived += Capture;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Отмена или закрытие окна загрузки обязаны снять и дочерние процессы: установщик держит
        // java, а та держит соединения.
        using (ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        }))
        {
            await process.WaitForExitAsync(ct);
        }

        if (process.ExitCode != 0)
        {
            string reason;
            lock (tail) reason = tail.ToString().Trim();
            throw new InvalidDataException(
                $"Установщик вернул код {process.ExitCode} — ядро не развёрнуто.\n\n{Cut(reason, 900)}");
        }
    }

    // 1.17+ запускается скриптом установщика (в нём собран classpath из libraries/), более
    // старые сборки — прямым universal jar. Если ни того ни другого нет, считаем что ядра нет.
    public static string? LaunchFile(string folder)
    {
        if (!Directory.Exists(folder)) return null;

        string bat = Path.Combine(folder, "run.bat");
        if (File.Exists(bat)) return bat;

        return Directory.GetFiles(folder, "*-universal.jar")
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    // Установщик держит classpath не в командной строке, а в файле аргументов java:
    // libraries\net\minecraftforge\forge\<версия>\win_args.txt. Раскладка и имя меняются от
    // версии к версии, поэтому путь не строим по шаблону: берём его из самого run.bat, где он
    // написан ровно в том виде, в каком его подставляет java.
    public static string? ArgsFile(string folder)
    {
        string bat = Path.Combine(folder, "run.bat");
        if (!File.Exists(bat)) return null;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(bat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (string line in lines)
        {
            foreach (string raw in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string token = raw.Trim('"');
                if (!token.StartsWith("@", StringComparison.Ordinal) ||
                    !token.EndsWith("_args.txt", StringComparison.OrdinalIgnoreCase)) continue;

                string relative = token[1..].Replace('\\', Path.DirectorySeparatorChar)
                                            .Replace('/', Path.DirectorySeparatorChar);
                string full = Path.GetFullPath(Path.Combine(folder, relative));

                // user_jvm_args.txt — это args-файл того же формата, но в нём только пользовательские
                // опции JVM, а не classpath: подставлять его вместо библиотек нельзя.
                if (Path.GetFileName(full).Equals("user_jvm_args.txt", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(full)) return full;
            }
        }

        return null;
    }

    public static bool IsScript(string file) =>
        file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

    // Чем именно запускать сборку после установщика. Файл аргументов важнее скрипта: run.bat
    // кончается строкой «pause», и после остановки сервера cmd ждал бы нажатия клавиши — панель
    // считала бы сервер запущенным. Если classpath можно попросить у файла аргументов, java
    // стартует напрямую, без cmd.
    public sealed record Launch(string Target, string? ArgsFile, string Note);

    public static Launch? DescribeLaunch(string folder)
    {
        string? args = ArgsFile(folder);
        string bat = Path.Combine(folder, "run.bat");

        // Скрипт из папки — это запускатель только тогда, когда рядом лежит install_profile.json:
        // его пишет установщик Forge/NeoForge. Модпак же мог принести свой run.bat в сборку на
        // Fabric, и запускать его — значит отдать cmd чужой classpath вместо ядра, которое панель
        // поставила. Файл аргументов из самого скрипта — такой же верный признак.
        if (args != null || (File.Exists(bat) && File.Exists(Path.Combine(folder, "install_profile.json"))))
        {
            string? file = LaunchFile(folder);
            if (file is null) return null;

            return args != null
                ? new Launch(file, args,
                    "Ядро Forge-family: java с classpath из «" + Path.GetRelativePath(folder, args) + "»")
                : new Launch(file, null, "Ядро Forge-family: запуск скриптом «" + Path.GetFileName(file) + "»");
        }

        // Universal jar — старая раскладка Forge (до 1.17): скрипта и install_profile там нет.
        string? universal = LaunchFile(folder);
        return universal is null || IsScript(universal)
            ? null
            : new Launch(universal, null, "Ядро Forge-family: запуск jar «" + Path.GetFileName(universal) + "»");
    }

    // У запуска через run.bat нет способа передать аргументы извне: единственное штатное место
    // для своих — user_jvm_args.txt, который скрипт подмешивает в командную строку java. Кроме
    // памяти туда попадает кодировка: без неё русская строка, отправленная в консоль панели,
    // доходит до сервера мусором — .NET берёт кодовую страницу консоли, а JVM читает UTF-8.
    public static void ApplyMemoryArgs(string folder, int gb)
    {
        string file = Path.Combine(folder, "user_jvm_args.txt");
        var lines = new List<string>();

        if (File.Exists(file))
            lines.AddRange(File.ReadAllLines(file)
                .Where(l => !Own(l, "-Xm") && !Own(l, "-Dfile.encoding") && !Own(l, "-Dstdout")
                         && !Own(l, "-Dstderr") && !Own(l, "-Dsun.stdout") && !Own(l, "-Dsun.stderr")));

        lines.Add($"-Xmx{Math.Max(1, gb)}G");
        lines.Add("-Dfile.encoding=UTF-8");
        lines.Add("-Dstdout.encoding=UTF-8");
        lines.Add("-Dstderr.encoding=UTF-8");
        lines.Add("-Dsun.stdout.encoding=UTF-8");
        lines.Add("-Dsun.stderr.encoding=UTF-8");
        File.WriteAllLines(file, lines, new UTF8Encoding(false));

        static bool Own(string line, string prefix) => line.TrimStart().StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string? Text(JsonNode node, string key) => node[key]?.ToString();

    private static string Cut(string value, int max = 120) =>
        value.Length <= max ? value : value[..max] + "…";
}
