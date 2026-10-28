using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ForgePanel;

// Что именно скачиваем: имя файла, адрес и хотя бы одна контрольная сумма, чтобы проверить
// целостность. Size = 0 означает, что сервис объём не сообщает — прогресс тогда без «из X».
public sealed record CoreFile(
    string FileName,
    string Url,
    long Size,
    string? Sha256,
    string? Sha1,
    string? Md5,
    string Source);

// Установка ядра сервера. Поддержаны только те ядра, которые отдаются ОДНИМ файлом и чьи адреса
// проверены запросами к живым API: Vanilla (манифест Mojang), Paper и Folia (fill.papermc.io v3),
// Fabric (meta.fabricmc.net v2), Leaf (релизы на GitHub).
// Важно: старый api.papermc.io/v2 закрыт (HTTP 410 Gone), поэтому Paper берётся только из v3.
// Purpur из списка убран намеренно: его /download отдаёт Range-запросы, но на теле ответа молчит,
// и с этой сети файл идёт около 1 КБ/с — панель не может это починить (подробности в UnsupportedReason).
public static class CoreInstaller
{
    private const string MojangManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string PaperProject = "https://fill.papermc.io/v3/projects/";
    private const string FabricRoot = "https://meta.fabricmc.net/v2/versions";
    private const string LeafReleases = "https://api.github.com/repos/Winds-Studio/Leaf/releases?per_page=60";

    // Файл ядра весит десятки мегабайт, и отдают их не всегда ровно: у Purpur проверено живьём —
    // 200 c Content-Length 53 МБ и обрыв потока на первых килобайтах. Поэтому у тела ответа
    // отдельный клиент без общего таймаута: его ограничивает только watchdog тишины ниже,
    // а сам файл приходит кусками по ChunkBytes — короткий диапазон оборвать сложнее, чем
    // часовой поток, и повтор нужен только для потерянных 8 МБ, а не для всех 50.
    private const int HeaderBudgetMs = 30_000;
    private const int IdleBudgetMs = 30_000;
    private const int ChunkBytes = 8 * 1024 * 1024;
    private const int ChunkAttempts = 3;
    private const int Rounds = 2;

    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(30));
    private static readonly HttpClient FileHttp = CreateClient(Timeout.InfiniteTimeSpan);

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        WriteUserAgent(null, client);
        return client;
    }

    // Cloudflare у PaperMC и CDN Mojang режут запросы без внятного User-Agent, поэтому он здесь
    // в том же виде, что у Modrinth: с контактом пользователя, если тот вписан в настройках.
    public static void ApplyUserAgent(string? contact)
    {
        WriteUserAgent(contact, Http);
        WriteUserAgent(contact, FileHttp);
    }

    private static void WriteUserAgent(string? contact, HttpClient client)
    {
        string value = string.IsNullOrWhiteSpace(contact)
            ? "ForgePanel/2.0 (Minecraft server manager for Windows)"
            : $"ForgePanel/2.0 ({contact.Trim()})";

        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", value);
    }

    // Пункт «Своё ядро» есть в списках Java и Bedrock: он означает, что файл берёт человек,
    // а панель только найдёт его в папке и предложит запустить.
    public const string CustomCore = "Своё ядро";

    public static bool IsCustom(string? core) =>
        string.Equals(core, CustomCore, StringComparison.OrdinalIgnoreCase);

    public static bool Supports(string? core) =>
        core is "Vanilla" or "Paper" or "Fabric" or "Folia" or "Leaf";

    // Причина отказа формулируется честно: не «не поддерживается», а что именно мешает.
    // Всё, что здесь написано, проверено запросами к этим сервисам.
    public static string UnsupportedReason(string? core)
    {
        if (IsCustom(core))
            return "Панель не скачивает «Своё ядро»: у файла нет известного адреса, поэтому качать нечего. " +
                   "Положи ядро в папку сборки сам. Для Java это один .jar — панель найдёт его при проверке " +
                   "сборки и предложит запустить; если ядро тянет библиотеки (как Forge), папку libraries " +
                   "кладут рядом с jar. Для Bedrock архив с bedrock_server.exe распаковывают в папку целиком. " +
                   "Версию Minecraft укажи ту, под которую ядро собрано: по ней панель подбирает Java.";

        return core switch
        {
            "Quilt" => "Рабочее API у Quilt есть (meta.quiltmc.org отдаёт списки загрузчиков и установщиков), " +
                       "но готового server jar в нём нет: запрос /server/jar отвечает 404, а loader для свежих " +
                       "версий выходит в статусе beta. Ставится вручную: quiltmc.org/install/server.",
            "Mohist" => "Публичный API mohistmc.com отдаёт только список версий (/api/v2/projects/mohist): запрос "
                        + "сборок по версии (/builds) не отвечает вовсе, поэтому адрес файла получить нечем. Файл "
                        + "берут с сайта и кладут в папку сборки.",
            "Magma" => "api.magmafoundation.org не отвечает (Cloudflare отдаёт 522 — источник недоступен), а релизы " +
                       "Magma на GitHub пустые: сборку скачивают с magmafoundation.org вручную.",
            "Pufferfish" => "Списка сборок получить нечем: релизы Pufferfish на GitHub пусты, а их CI (ci.pufferfish.gg) " +
                            "не отвечает. Файл берут с сайта проекта вручную.",
            "Purpur" => "Purpur панель ставить не берётся: api.purpurmc.org отвечает на Range-запрос честно, но тело " +
                        "файла отдаёт молча — по замеру около 1 КБ/с, а 50 МБ так идут часа четыре. Запасного адреса " +
                        "у проекта нет: релизы на GitHub пустые, repo.purpurmc.org — это maven без серверных jar. " +
                        "Хочется именно Purpur — скачай jar на их сайте и положи в папку сборки.",
            "Spigot" or "Bukkit" => "Spigot и Bukkit одним файлом не отдаются: их собирает BuildTools — " +
                                    "java -jar BuildTools.jar --rev <версия>. Готовый jar кладите в папку сборки.",
            "Forge" or "NeoForge" => "Эти ядра ставит ForgeInstaller: там не один файл, а установщик с докачкой библиотек.",
            "Bedrock Dedicated Server" => "Bedrock-ядро — архив с bedrock_server.exe и библиотеками: его надо " +
                                          "распаковывать, а не скачивать одним файлом.",
            "Nukkit" or "PowerNukkit" => "Bedrock-ядра на Java: свежие релизы Nukkit выкладываются без готового jar " +
                                         "(только исходники), поэтому файл собирают или скачивают сами.",
            "Vanilla" or "Paper" or "Fabric" or "Folia" or "Leaf" => "",
            _ => $"Ядро «{core}» установщику неизвестно: адрес сборки нужно взять из официального источника и проверить вручную."
        };
    }

    // Что показать в списке ядер и в каком порядке: сначала то, что панель ставит сама,
    // ниже — те, где пользователю придётся действовать вручную.
    public static bool IsAuto(string? core) => Supports(core) || core is "Forge" or "NeoForge";

    // stage — текущее действие, чтобы окно не выглядело зависшим, пока сервис ищет сборку.
    public static async Task<CoreFile> ResolveAsync(string core, string? mcVersion, IProgress<string>? stage, CancellationToken ct)
    {
        string version = (mcVersion ?? "").Trim();
        if (version.Length == 0) throw new InvalidDataException("Версия Minecraft не указана — адрес сборки построить не из чего.");
        if (!Supports(core)) throw new NotSupportedException(UnsupportedReason(core));

        return core switch
        {
            "Vanilla" => await ResolveVanilla(version, stage, ct),
            "Paper" => await ResolvePaperProject("paper", "Paper", version, stage, ct),
            "Folia" => await ResolvePaperProject("folia", "Folia", version, stage, ct),
            "Leaf" => await ResolveLeaf(version, stage, ct),
            _ => await ResolveFabric(version, stage, ct)
        };
    }

    private static async Task<CoreFile> ResolveVanilla(string version, IProgress<string>? stage, CancellationToken ct)
    {
        stage?.Report("Ищу версию в манифесте Mojang…");
        JsonNode manifest = await GetJson(MojangManifest, ct);

        string? versionUrl = (manifest["versions"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(v => string.Equals(v["id"]?.ToString(), version, StringComparison.OrdinalIgnoreCase))?["url"]?.ToString();

        if (string.IsNullOrWhiteSpace(versionUrl))
            throw new InvalidDataException($"Версии «{version}» нет в манифесте Mojang — обнови список версий или проверь номер.");

        JsonNode meta = await GetJson(versionUrl, ct);
        JsonNode? server = meta["downloads"]?["server"];
        if (server is null) throw new InvalidDataException("В описании версии нет блока downloads.server — похоже, манифест изменился.");

        return new CoreFile("server.jar", Read(server, "url"), ReadLong(server, "size"),
            null, Read(server, "sha1"), null, "манифест Mojang");
    }

    // Paper и Folia лежат в одном репозитории PaperMC и описываются одним и тем же ответом v3 API,
    // поэтому для них один резолвер: отличается только идентификатор проекта.
    private static async Task<CoreFile> ResolvePaperProject(string project, string label, string version, IProgress<string>? stage, CancellationToken ct)
    {
        stage?.Report($"Запрашиваю последнюю сборку {label}…");
        // Точка v2 (api.papermc.io) отдаёт 410 Gone; рабочая — fill.papermc.io/v3.
        JsonNode build = await GetJson($"{PaperProject}{project}/versions/{Esc(version)}/builds/latest", ct);
        JsonNode? file = build["downloads"]?["server:default"];
        if (file is null) throw new InvalidDataException($"Сборка {label} не вернула блок downloads.server:default — формат ответа изменился.");

        string sha256 = file["checksums"]?["sha256"]?.ToString() ?? "";

        return new CoreFile(Read(file, "name"), Read(file, "url"), ReadLong(file, "size"),
            sha256.Length == 0 ? null : sha256, null, null, "PaperMC v3");
    }

    private static async Task<CoreFile> ResolveFabric(string version, IProgress<string>? stage, CancellationToken ct)
    {
        stage?.Report("Ищу загрузчик Fabric для этой версии…");
        JsonNode loaders = await GetJson($"{FabricRoot}/loader/{Esc(version)}", ct);

        IEnumerable<JsonObject> options = (loaders as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();
        string? loaderVersion = options.FirstOrDefault(o => True(o["loader"]?["stable"]))?["loader"]?["version"]?.ToString()
                                ?? options.FirstOrDefault()?["loader"]?["version"]?.ToString();

        if (string.IsNullOrWhiteSpace(loaderVersion))
            throw new InvalidDataException($"Fabric не возвращает загрузчик для «{version}» — возможно, сборки под неё ещё нет.");

        stage?.Report("Ищу установщик Fabric…");
        JsonNode installers = await GetJson($"{FabricRoot}/installer", ct);
        string? installerVersion = (installers as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["version"]?.ToString();

        if (string.IsNullOrWhiteSpace(installerVersion))
            throw new InvalidDataException("Fabric не вернул список установщиков — загрузку надо взять с meta.fabricmc.net вручную.");

        // Этот адрес отдаёт сразу готовый server jar: запускать установщик не нужно.
        string url = $"{FabricRoot}/loader/{Esc(version)}/{Esc(loaderVersion)}/{Esc(installerVersion)}/server/jar";
        return new CoreFile(SafeName($"fabric-server-mc.{version}-loader.{loaderVersion}-launcher.{installerVersion}.jar"),
            url, 0, null, null, null, "FabricMC meta");
    }

    // Leaf публикует готовые server jar как ассеты релизов GitHub: своего API у проекта нет,
    // поэтому ищем среди последних релизов файл с именем leaf-<версия MC>-<сборка>.jar.
    // Дайджестов GitHub не отдаёт, так что контроль тут — только размер из того же ответа.
    private static async Task<CoreFile> ResolveLeaf(string version, IProgress<string>? stage, CancellationToken ct)
    {
        stage?.Report("Ищу релиз Leaf на GitHub…");
        JsonNode releases = await GetJson(LeafReleases, ct);

        string prefix = "leaf-" + version + "-";

        // GitHub отдаёт релизы от нового к старому, а Leaf выкладывает по одному jar на релиз,
        // поэтому первое совпадение и есть самая свежая сборка под эту версию Minecraft.
        foreach (JsonNode? release in (releases as JsonArray) ?? new JsonArray())
        {
            foreach (JsonNode? asset in (release?["assets"] as JsonArray) ?? new JsonArray())
            {
                string name = asset?["name"]?.ToString() ?? "";
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    continue;

                string url = asset?["browser_download_url"]?.ToString() ?? "";
                if (url.Length == 0) continue;

                long size = asset?["size"] is JsonValue s && s.TryGetValue(out long number) ? number : 0;
                return new CoreFile(name, url, size, null, null, null, "GitHub: Winds-Studio/Leaf");
            }
        }

        throw new InvalidDataException(
            $"Среди свежих релизов Leaf нет сборки под «{version}» — у проекта свои версии Minecraft, " +
            "обычно он отстаёт от Paper на несколько выпусков. Список есть на github.com/Winds-Studio/Leaf/releases.");
    }

    // Скачивание в папку сборки: сначала в .part, проверка суммы, перенос на место. Ядро ищется
    // по расширению .jar, поэтому оборванная загрузка не оставит в папке файл, который панель
    // приняла бы за установленный. Недокачанный .part после неудачной сети оставляем нарочно:
    // следующая установка продолжит с того же места, а не начнёт 50 МБ заново.
    public static async Task<string> InstallAsync(CoreFile file, string folder,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, SafeName(file.FileName));
        string temp = target + ".part";

        string lastError = "Сервер не отдал файл.";
        bool complete = false;

        try
        {
            // Обрыв связи чинится внутри DownloadAsync: он повторяет тот же диапазон с того же места.
            // Второй круг нужен только несовпавшей сумме — её дописывать нельзя, файл собирается заново.
            for (int round = 1; round <= Rounds && !complete; round++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await DownloadAsync(file, temp, progress, ct);
                    Verify(file, temp);
                    complete = true;
                }
                catch (Exception ex) when (Retryable(ex))
                {
                    lastError = ex.Message;
                    if (ex is MismatchException) DeleteQuiet(temp);
                }
            }
        }
        catch
        {
            // Отмена и всё, что повтором не лечится (404, 403, чужой формат ответа):
            // в папке не должно остаться файла, из которого потом непонятно что качать.
            DeleteQuiet(temp);
            throw;
        }

        if (!complete)
            throw new IOException($"{lastError}\n\nЗа {Rounds} захода файл целиком не пришёл. Нажми «Установить ядро» " +
                                  "ещё раз — качалка продолжит с того же места, или скачай ядро сам с официального " +
                                  "адреса и положи в папку сборки.");

        // Перенос вне повторов: файл уже целый, и держать его может только антивирус или открытое
        // окно — перезакачивать из-за этого 50 МБ не нужно.
        try
        {
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Не удалось перенести готовый файл на место ({ex.Message}). " +
                                  $"Проверь, не держит ли его антивирус или открытое окно: {target}", ex);
        }

        return target;
    }

    // Повтор имеет смысл оборванному соединению и таймауту. 404, 403 и изменившийся формат ответа —
    // это не сеть, а правда: гонять по три запроса на каждую такую ошибку бессмысленно.
    private static bool Retryable(Exception ex) =>
        ex is HttpRequestException || (ex is IOException && ex is not InvalidDataException);

    // Отдельный тип для «качается не то»: позволяет отличить битый файл от оборванной связи
    // и не пытаться дописывать чужие байты.
    private sealed class MismatchException : IOException
    {
        public MismatchException(string message) : base(message) { }
    }

    // Файл собирается по кускам: каждый запрос просит диапазон в ChunkBytes байт и, если оборвался,
    // повторяется до ChunkAttempts раз подряд с того же места. Счётчик повторов обнуляется всякий раз,
    // когда байты пришли, поэтому рваное соединение не сожрёт весь бюджет на первом же куске.
    private static async Task DownloadAsync(CoreFile file, string temp,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        long total = file.Size;
        int fails = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Сколько лежит на диске — с того места и продолжаем; длина файла и есть наша позиция.
            long done = File.Exists(temp) ? new FileInfo(temp).Length : 0;
            if (total > 0 && done >= total) return;

            long got;
            bool wholeFile;
            try
            {
                (got, long announced, bool partial) = await FetchChunkAsync(file, temp, done, total, progress, ct);
                if (announced > 0) total = announced;

                // Ответ 200 на запрос с Range: сервер Range не понимает и отдаёт файл целиком.
                // Второй такой же запрос переписал бы файл с нуля — просить больше не нужно.
                wholeFile = !partial;
            }
            catch (Exception ex) when (Retryable(ex))
            {
                if (++fails >= ChunkAttempts)
                    throw new IOException($"{ex.Message}\n\nКусок файла не дошёл за {ChunkAttempts} попытки подряд.", ex);
                continue;
            }

            if (got == 0 || wholeFile) return;
            fails = 0;
        }
    }

    // Один диапазон файла. Верхняя граница называется всегда: тогда оборванный кусок виден по
    // несовпадению длины ответа, а не по вечному ожиданию. Возвращает число дописанных байт, общий
    // объём, который сервер сам назвал (0 — не назвал), и был ли ответ частичным (206).
    private static async Task<(long Got, long Total, bool Partial)> FetchChunkAsync(CoreFile file, string temp,
        long from, long knownTotal, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        long upper = knownTotal > 0 ? Math.Min(from + ChunkBytes - 1, knownTotal - 1) : from + ChunkBytes - 1;

        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        request.Headers.Range = new RangeHeaderValue(from, upper);

        // Таймер только на заголовки: тело читается своим watchdog'ом тишины,
        // и общий Timeout оборвал бы его ровно на середине, как это делает HttpClient по умолчанию.
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(HeaderBudgetMs);

        HttpResponseMessage response;
        try
        {
            response = await FileHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new IOException($"{Host(file.Url)} не ответил за {HeaderBudgetMs / 1000} с: {ex.Message}");
        }

        timer.CancelAfter(Timeout.InfiniteTimeSpan);
        using (response)
        {
            // Диапазон за концом файла означает, что он уже целиком лежит на диске.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                return (0, File.Exists(temp) ? new FileInfo(temp).Length : 0, true);

            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException(StatusText((int)response.StatusCode, response.ReasonPhrase, file.Url));

            long announced = response.Content.Headers.ContentRange?.Length ?? 0;
            long head = response.Content.Headers.ContentLength ?? 0;
            bool appended = response.StatusCode == HttpStatusCode.PartialContent;

            // 200 вместо 206 — Range игнорируется: файл пишем заново с нуля и считаем его целым.
            long start = appended ? from : 0;

            // Что показывать в прогрессе: названный сервером объём, при 200 — длина ответа,
            // если и она молчит — то, что сервер сообщил прошлым куском.
            long shown = appended ? (announced > 0 ? announced : knownTotal) : head;

            using var source = await response.Content.ReadAsStreamAsync(ct);
            using var destination = appended
                ? File.Open(temp, FileMode.Append, FileAccess.Write)
                : File.Create(temp);

            var buffer = new byte[92160];
            long got = 0;
            progress?.Report((start, shown));

            while (true)
            {
                int read;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(IdleBudgetMs);
                    try
                    {
                        read = await source.ReadAsync(buffer, idle.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        throw new IOException($"{Host(file.Url)} замолчал на {start + got} байте: {ex.Message}");
                    }
                }

                if (read <= 0) break;

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                got += read;
                progress?.Report((start + got, shown));
            }

            await destination.FlushAsync(ct);

            if (head > 0 && got != head)
                throw new IOException($"{Host(file.Url)} оборвал соединение: кусок пришёл {got} байт из {head}.");

            // Объём называет сам сервер: для 206 это длина из Content-Range, для 200 — длина ответа.
            // 206 без Content-Range (самописные раздачи) даёт 0 — тогда следующий диапазон будет
            // запрошен так же, и цикл закончится, когда сервер скажет, что дальше читать нечего.
            return (got, appended ? announced : head, appended);
        }
    }

    private static void DeleteQuiet(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Считаем суммы уже готового файла с диска, а не на лету: после докачки с середины поток
    // байтов складывается из двух соединений, и посимвольный счётчик стал бы лишней магией.
    private static void Verify(CoreFile file, string path)
    {
        bool needSha256 = !string.IsNullOrWhiteSpace(file.Sha256);
        bool needSha1 = !string.IsNullOrWhiteSpace(file.Sha1);
        bool needMd5 = !string.IsNullOrWhiteSpace(file.Md5);
        if (!needSha256 && !needSha1 && !needMd5) return;

        string sha256 = "", sha1 = "", md5 = "";
        using (var stream = File.OpenRead(path))
        {
            using var h256 = needSha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            using var h1 = needSha1 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
            using var h5 = needMd5 ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;

            var buffer = new byte[92160];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                h256?.AppendData(buffer, 0, read);
                h1?.AppendData(buffer, 0, read);
                h5?.AppendData(buffer, 0, read);
            }

            sha256 = Hex(h256);
            sha1 = Hex(h1);
            md5 = Hex(h5);
        }

        if (needSha256 && !Equal(file.Sha256!, sha256))
            throw new MismatchException($"SHA-256 не совпал: получили {sha256}, {file.Source} ждал {file.Sha256}.");

        if (needSha1 && !Equal(file.Sha1!, sha1))
            throw new MismatchException($"SHA-1 не совпал: получили {sha1}, {file.Source} ждал {file.Sha1}.");

        if (needMd5 && !Equal(file.Md5!, md5))
            throw new MismatchException($"MD5 не совпал: получили {md5}, {file.Source} ждал {file.Md5}.");
    }

    private static string Hex(IncrementalHash? hash) =>
        hash is null ? "" : Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

    private static bool Equal(string expected, string actual) =>
        string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);

    // internal, а не private: ForgeInstaller ходит в maven-адреса Forge тем же запросом
    // с теми же разборчивыми ошибками по коду ответа.
    internal static async Task<JsonNode> GetJson(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(text) ?? throw new InvalidDataException($"Сервис {Host(url)} вернул пустой ответ.");
    }

    // Побочный файл репозитория (.sha1 или .md5 рядом с jar). Отказывается отвечать — не повод
    // срывать установку: возвращаем пустую строку, и проверка суммы просто пропускается.
    internal static async Task<string> TryGetText(string url, CancellationToken ct)
    {
        try
        {
            using var response = await SendAsync(url, HttpCompletionOption.ResponseContentRead, ct);
            return (await response.Content.ReadAsStringAsync(ct)).Trim();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            (ex is InvalidDataException or IOException or HttpRequestException or TaskCanceledException))
        {
            return "";
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(string url, HttpCompletionOption option, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException("Адрес сборки не http/https — скачивание отклонено.");

        var response = await Http.GetAsync(parsed, option, ct);
        if (response.IsSuccessStatusCode) return response;

        int code = (int)response.StatusCode;
        string reason = response.ReasonPhrase ?? "";
        response.Dispose();

        throw new InvalidDataException(StatusText(code, reason, url));
    }

    // Ошибка формулируется так, чтобы человек понял, что делать: 404 — это не «сбой сети»,
    // а нет такой версии; 403 — чаще всего User-Agent или блокировщик.
    private static string StatusText(int code, string? reason, string url) => code switch
    {
        404 => $"404: на {Host(url)} сборки с таким номером нет — проверь версию Minecraft.",
        403 => $"403: {Host(url)} отклонил запрос (обычно это User-Agent или включённый блокировщик).",
        410 => $"410: {Host(url)} закрыл эту версию API — нужен другой адрес.",
        429 => $"429: {Host(url)} просит не спешить — слишком много запросов подряд.",
        _ => $"{code} {reason} от {Host(url)}."
    };

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? p) ? p.Host : url;

    private static bool True(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool value) && value;

    // Номер версии пользователь вписывает руками, поэтому в URL он попадает только экранированным.
    internal static string Esc(string value) => Uri.EscapeDataString(value.Trim());

    private static string Read(JsonNode node, string field) =>
        node[field]?.ToString() ?? throw new InvalidDataException($"В ответе нет поля «{field}».");

    private static long ReadLong(JsonNode node, string field) =>
        node[field] is JsonValue value && value.TryGetValue(out long number) ? number : 0;

    private static string SafeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (char c in name) builder.Append(invalid.Contains(c) ? '_' : c);
        return builder.ToString();
    }
}
