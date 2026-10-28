using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ForgePanel;

public sealed class CurseMod
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string WebsiteUrl { get; init; } = "";
    public string LogoUrl { get; init; } = "";
    public string Summary { get; init; } = "";
    public long Downloads { get; init; }
    public bool IsAvailable { get; init; }
}

public sealed class CurseFile
{
    public int Id { get; init; }
    public int ModId { get; init; }
    public string FileName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public long Length { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string? Sha1 { get; init; }
    public List<string> GameVersions { get; init; } = new();
    public List<string> Loaders { get; init; } = new();
}

// CurseForge отдаёт v1-ответы в обёртке { "data": ... }, но форма data разная: у актуального
// /mods/search это массив модов (см. docs.curseforge.com/rest-api, «Search Mods Response»),
// у прежней версии — объект с results. Имена полей тоже менялись: downloadCount вместо
// downloads, ссылка на страницу — вложенным links. Поэтому разбираем JSON вручную, а не через
// жёсткие записи, и перед чтением поля проверяем форму элемента: неподходящая форма не должна
// ронять приложение.
public static class CurseForge
{
    private const string ApiRoot = "https://api.curseforge.com/v1";
    private const int MinecraftGameId = 432;

    // Разделы (primary categories) Minecraft. Числа взяты из состояния самой страницы
    // curseforge.com: поиск «class=data-packs» держит список категорий, где
    // { id 6, slug mc-mods } и { id 6945, slug data-packs }, а дочерние категории датапаков
    // (mod-support 6946, utility 6953 …) указывают classId 6945. Публичного эндпоинта
    // категорий без ключа нет, поэтому id здесь константы, а не запрос.
    private const int ModsClassId = 6;
    private const int DataPacksClassId = 6945;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<List<CurseMod>> SearchAsync(
        string query, string? gameVersion, string apiKey, bool dataPacks = false, CancellationToken ct = default)
    {
        var parts = new List<string>
        {
            "gameId=" + MinecraftGameId,
            // Без classId сервис ищет по всем разделам сразу, и датапаки тонут среди модов.
            "classId=" + (dataPacks ? DataPacksClassId : ModsClassId),
            "pageSize=20",
            // Актуальная схема /v1/mods/search (docs.curseforge.com/rest-api): текст ищется
            // параметром searchFilter — старый search сервис больше не читает и отдаёт первую
            // страницу вообще без фильтра. Направление сортировки — sortOrder asc|desc (sortType
            // тоже упразднён), sortField 1 = Featured, то есть «рекомендуемые» как на сайте.
            "searchFilter=" + Uri.EscapeDataString(query ?? ""),
            "sortField=1",
            "sortOrder=desc"
        };
        if (!string.IsNullOrWhiteSpace(gameVersion))
            parts.Add("gameVersion=" + Uri.EscapeDataString(gameVersion));

        using var document = await RequestAsync($"/mods/search?{string.Join("&", parts)}", apiKey, ct);
        var mods = new List<CurseMod>();
        JsonElement results = ResultsOf(document.RootElement);

        foreach (var item in results.EnumerateArray())
        {
            mods.Add(new CurseMod
            {
                Id = item.GetPropertyOrZero("id"),
                Name = item.GetStringOrEmpty("name"),
                Author = FirstAuthor(item),
                WebsiteUrl = WebsiteOf(item),
                LogoUrl = FirstLogoUrl(item),
                Summary = item.GetStringOrEmpty("summary"),
                Downloads = item.GetPropertyOrLong("downloadCount"),
                IsAvailable = item.Field("isAvailable").ValueKind != JsonValueKind.False
            });
        }

        return mods.Where(m => m.Id > 0 && m.Name.Length > 0).ToList();
    }

    public static async Task<List<CurseFile>> GetFilesAsync(
        int modId, string? gameVersion, string apiKey, CancellationToken ct = default)
    {
        string path = $"/mods/{modId}/files";
        if (!string.IsNullOrWhiteSpace(gameVersion))
            path += "?gameVersion=" + Uri.EscapeDataString(gameVersion);

        using var document = await RequestAsync(path, apiKey, ct);
        var files = new List<CurseFile>();
        JsonElement data = ResultsOf(document.RootElement);

        foreach (var item in data.EnumerateArray())
        {
            if (item.Field("isExternal").ValueKind == JsonValueKind.True) continue;
            files.Add(ReadFile(item, modId, 0));
        }

        // Номера файлов CurseForge растут со временем, поэтому самый свежий — с наибольшим id.
        // Фильтр по версии в параметрах запроса больше не документирован, поэтому дополнительно
        // отбираем сами: без этого «свежим» мог оказаться файл под другую Minecraft.
        return files.Where(f => f.Id > 0)
            .OrderByDescending(f => MatchesVersion(f, gameVersion) ? 1 : 0)
            .ThenByDescending(f => f.Id)
            .ToList();
    }

    // Модпак задаёт файл парой projectID/fileID и не знает ни имени, ни размера — их даёт этот
    // запрос. Берём ровно тот номер, который назвал пак: «самый свежий файл проекта» для пака
    // не подходит, автор собирал и проверял конкретную сборку файла.
    public static async Task<CurseFile> GetFileAsync(
        int modId, int fileId, string apiKey, CancellationToken ct = default)
    {
        using var document = await RequestAsync($"/mods/{modId}/files/{fileId}", apiKey, ct);
        JsonElement data = document.RootElement.Field("data");
        if (data.ValueKind != JsonValueKind.Object)
            throw new IOException($"CurseForge вернул файл {fileId} проекта {modId} непривычной формой — " +
                                  "список файлов пака разобрать нечем.");

        if (data.Field("isExternal").ValueKind == JsonValueKind.True)
            throw new IOException($"Файл {fileId} проекта {modId} размещён вне CurseForge: автор ссылается на " +
                                  "сторонний хост. Панель такие файлы не качает — положи его в mods сам.");

        CurseFile file = ReadFile(data, modId, fileId);
        if (file.Id != fileId)
            throw new IOException($"CurseForge на запрос файла {fileId} проекта {modId} прислал другой файл " +
                                  $"({file.Id}) — ставить его вместо требуемого панель не будет.");

        return file;
    }

    private static CurseFile ReadFile(JsonElement item, int modId, int fallbackId)
    {
        int id = item.GetPropertyOrZero("id");
        List<string> versions = ReadGameVersions(item, out List<string> loaders).ToList();

        return new CurseFile
        {
            Id = id == 0 ? fallbackId : id,
            ModId = modId,
            FileName = item.GetStringOrEmpty("fileName"),
            DisplayName = item.GetStringOrEmpty("displayName"),
            Length = item.GetPropertyOrLong("fileLength"),
            DownloadUrl = item.GetStringOrEmpty("downloadUrl"),
            Sha1 = ReadSha1(item),
            GameVersions = versions,
            Loaders = loaders
        };
    }

    private static bool MatchesVersion(CurseFile file, string? gameVersion) =>
        gameVersion != null && file.GameVersions.Any(v => string.Equals(v, gameVersion, StringComparison.OrdinalIgnoreCase));

    // Сначала пробуем официальный эндпоинт загрузки, потом — прямой downloadUrl из ответа.
    public static async Task<string> ResolveDownloadUrlAsync(
        CurseFile file, string apiKey, CancellationToken ct = default)
    {
        try
        {
            // Get Mod File Download URL: ответ — {"data":"https://…"}, ссылка лежит в самом data.
            using var document = await RequestAsync($"/mods/{file.ModId}/files/{file.Id}/download-url", apiKey, ct);
            JsonElement data = document.RootElement.Field("data");
            string resolved = data.ValueKind == JsonValueKind.String
                ? data.GetString() ?? ""
                : data.GetStringOrEmpty("downloadUrl");
            if (resolved.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return resolved;
        }
        catch (IOException)
        {
            // Эндпоинт мог быть недоступен — ниже пробуем прямой ссылку из списка файлов.
        }

        if (file.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return file.DownloadUrl;
        throw new IOException("CurseForge не вернул ссылку на файл.");
    }

    public static async Task<string> DownloadAsync(
        CurseFile file, string folder, IProgress<(long Done, long Total)>? progress,
        string apiKey, CancellationToken ct = default)
    {
        string url = await ResolveDownloadUrlAsync(file, apiKey, ct);
        Directory.CreateDirectory(folder);
        string name = string.IsNullOrWhiteSpace(file.FileName) ? $"curseforge-{file.Id}.jar" : file.FileName;
        string target = UniquePath(folder, name);
        string actualSha1;

        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url)))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Отменённую загрузку человек должен видеть как отмену, а не как сетевую ошибку:
                // установка пака различает эти два исхода.
                throw;
            }
            catch (Exception ex)
            {
                throw new IOException("Не удалось скачать файл: " + NetworkReason(ex), ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                response.Dispose();
                throw new IOException(code switch
                {
                    401 or 403 => "CurseForge не пустил к файлу (401/403): проверь API-ключ в Настройках.",
                    429 => "Слишком много запросов к CurseForge — подожди минуту.",
                    >= 500 => "У CurseForge временная проблема на сервере.",
                    _ => "CurseForge ответил с кодом " + code + "."
                });
            }

            using var httpResponse = response;
            long total = httpResponse.Content.Headers.ContentLength ?? 0;
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var source = await httpResponse.Content.ReadAsStreamAsync(ct);
            using var destination = File.Create(target);

            var buffer = new byte[92160];
            long done = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, ct);
                if (read <= 0) break;
                sha1.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Report((done, total));
            }

            actualSha1 = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(file.Sha1) &&
            !string.Equals(actualSha1, file.Sha1, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new IOException(
                $"Файл докачался повреждённым: SHA1 {actualSha1}, CurseForge ожидал {file.Sha1}. Повтори скачивание.");
        }

        return target;
    }

    // «An error occurred while sending the request» — это фраза .NET, а не причина. Человеку
    // полезно знать, что проверить, поэтому сетевые неполадки переводим сами.
    private static string NetworkReason(Exception ex) =>
        ex is TaskCanceledException
            ? "CurseForge не ответил за 30 секунд — проверь интернет."
            : "Нет связи с CurseForge — проверь интернет или попробуй позже.";

    // CurseForge рвёт соединение на ровном месте чаще, чем действительно отказывает файлу: одна
    // повторная попытка через пару секунд закрывает почти все такие осечки. Повторяем именно
    // скачивание, а не отказ по коду 401/403 — без ключа вторая попытка дала бы то же самое.
    public static async Task<string> DownloadWithRetryAsync(
        CurseFile file, string folder, IProgress<(long Done, long Total)>? progress,
        string apiKey, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await DownloadAsync(file, folder, progress, apiKey, ct);
            }
            catch (IOException) when (attempt < 2)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private static async Task<JsonDocument> RequestAsync(string path, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new IOException("API-ключ CurseForge не заполнен — добавь его в разделе «Настройки».");

        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoot + path);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
        request.Headers.UserAgent.ParseAdd("ForgePanel/2.0 (Minecraft server manager for Windows)");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new IOException(NetworkReason(ex), ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                throw new IOException(code switch
                {
                    401 or 403 => "CurseForge отклонил запрос (401/403). Обычно значит, что API-ключ неверный или не активирован для Minecraft.",
                    404 => "CurseForge не нашёл такой мод или файл (404).",
                    429 => "Слишком много запросов к CurseForge — подожди минуту.",
                    >= 500 => "У CurseForge временная проблема на сервере.",
                    _ => "CurseForge ответил с кодом " + code + "."
                });
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
    }

    // Список элементов ищем по обеим формам, которыми CurseForge отвечает: {"data":[…]} и
    // {"data":{"results":[…]}}. Пустая выборка приходит массивом, поэтому форма — не признак
    // ошибки: если массива нет ни в одном из мест, формат ответа изменился, и это лучше назвать
    // словами, чем показать пользователю внутреннюю ошибку System.Text.Json.
    private static JsonElement ResultsOf(JsonElement root)
    {
        JsonElement data = root.Field("data");
        if (data.ValueKind == JsonValueKind.Undefined) data = root;
        if (data.ValueKind == JsonValueKind.Array) return data;

        JsonElement results = data.Field("results");
        if (results.ValueKind == JsonValueKind.Array) return results;

        throw new IOException("CurseForge вернул ответ непривычной формы — список проектов разобрать нечем. " +
            "Попробуй позже; если повторяется каждый раз — сервис изменил формат ответа.");
    }

    private static string FirstAuthor(JsonElement mod)
    {
        JsonElement authors = mod.Field("authors");
        if (authors.ValueKind != JsonValueKind.Array) return "";
        foreach (var author in authors.EnumerateArray())
        {
            string name = author.GetStringOrEmpty("name");
            if (name.Length > 0) return name;
        }
        return "";
    }

    // Ссылка на страницу мода в актуальной схеме лежит в вложенном links, а не наверху.
    private static string WebsiteOf(JsonElement mod)
    {
        string link = mod.Field("links").GetStringOrEmpty("websiteUrl");
        return link.Length > 0 ? link : mod.GetStringOrEmpty("websiteUrl");
    }

    // Логотив лежит во вложенном объекте logo, а имена полей в API менялись: в текущей схеме
    // это thumbnailUrl/url, в прежних встречались thumbUrl/logoUrl. Поиск по имени в JSON
    // чувствителен к регистру, поэтому перебираем точные варианты и берём первый непустой.
    private static string FirstLogoUrl(JsonElement mod)
    {
        JsonElement logo = mod.Field("logo");
        if (logo.ValueKind != JsonValueKind.Object) return "";
        foreach (string name in new[] { "thumbnailUrl", "thumbUrl", "logoUrl", "url" })
        {
            string value = logo.GetStringOrEmpty(name);
            if (value.Length > 0) return value;
        }
        return "";
    }

    private static string? ReadSha1(JsonElement file)
    {
        JsonElement hashes = file.Field("hashes");
        if (hashes.ValueKind != JsonValueKind.Array) return null;
        foreach (var hash in hashes.EnumerateArray())
        {
            int algo = hash.GetPropertyOrZero("algo");
            string value = hash.GetStringOrEmpty("value");
            if (algo == 1 && value.Length > 0) return value.ToLowerInvariant();
        }
        return null;
    }

    private static IEnumerable<string> ReadGameVersions(JsonElement file, out List<string> loaders)
    {
        var versions = new List<string>();
        loaders = new List<string>();
        JsonElement array = file.Field("gameVersions");
        if (array.ValueKind != JsonValueKind.Array) return versions;

        foreach (var entry in array.EnumerateArray())
        {
            // Текущая схема помечает и версию, и загрузчик строками в gameVersions: ["1.20.1","Fabric"].
            // Старый формат был объектом { gameVersion, versionTypeName }. Оба разбираем: из объекта
            // загрузчик видно явно, из строки — только по имени из списка LoaderNames.
            if (entry.ValueKind == JsonValueKind.String)
            {
                string text = entry.GetString() ?? "";
                versions.Add(text);
                if (IsLoaderType(text)) loaders.Add(text);
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object) continue;
            string version = entry.GetStringOrEmpty("gameVersion");
            string type = entry.GetStringOrEmpty("versionTypeName");
            if (version.Length > 0) versions.Add(version);
            if (IsLoaderType(type)) loaders.Add(type);
        }

        versions.RemoveAll(string.IsNullOrWhiteSpace);
        loaders = loaders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return versions;
    }

    // В gameVersions у файлов встречаются и типы вроде "Java"/"Client"/"Server" — грузчиками они не являются.
    private static readonly HashSet<string> LoaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "forge", "fabric", "neoforge", "quilt", "paper", "spigot", "bukkit",
        "velocity", "bungeecord", "sponge", "arclight", "magma", "mohist", "optifine", "liteloader"
    };

    private static bool IsLoaderType(string type) => LoaderNames.Contains(type);

    private static string UniquePath(string folder, string filename)
    {
        string baseName = Path.GetFileNameWithoutExtension(filename);
        string extension = Path.GetExtension(filename);
        string candidate = Path.Combine(folder, filename);
        int counter = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(folder, $"{baseName} ({counter++}){extension}");
        return candidate;
    }

    // Чтение поля у элемента, который объектом не является, в System.Text.Json бросается, а не
    // возвращает false — поэтому сначала проверяем форму. Ответы CurseForge местами приходят то
    // объектом, то массивом (пустая выборка — это {"data":[]}, а не {"data":{"results":[]}}).
    private static JsonElement Field(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        return element.TryGetProperty(name, out JsonElement value) ? value : default;
    }

    private static int GetPropertyOrZero(this JsonElement element, string name)
    {
        JsonElement value = element.Field(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;
    }

    private static long GetPropertyOrLong(this JsonElement element, string name)
    {
        JsonElement value = element.Field(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;
    }

    private static string GetStringOrEmpty(this JsonElement element, string name)
    {
        JsonElement value = element.Field(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }
}
