using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ForgePanel;

public sealed record ModrinthProject(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("downloads")] long Downloads,
    [property: JsonPropertyName("icon_url")] string? IconUrl);

public sealed record ModrinthHashes(
    [property: JsonPropertyName("sha1")] string? Sha1,
    [property: JsonPropertyName("sha512")] string? Sha512);

public sealed record ModrinthFile(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("primary")] bool Primary,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("hashes")] ModrinthHashes? Hashes);

// id и project_id нужны разбору обновлений: по ним панель понимает, что лежащий в mods файл и
// предложенный свежий — одна и та же запись (или разная), и к какому проекту идти за списком версий.
public sealed record ModrinthVersion(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version_number")] string VersionNumber,
    [property: JsonPropertyName("date_published")] DateTime DatePublished,
    [property: JsonPropertyName("loaders")] List<string>? Loaders,
    [property: JsonPropertyName("game_versions")] List<string>? GameVersions,
    [property: JsonPropertyName("files")] List<ModrinthFile>? Files);

internal sealed record ModrinthSearchResponse(
    [property: JsonPropertyName("hits")] List<ModrinthProject>? Hits);

// Ответ /v2/project/{id}: поля называются иначе, чем в выдаче поиска (там project_id, тут id),
// поэтому запись отдельная. Из проекта берём только вывеску, иконку и адрес страницы.
public sealed record ModrinthProjectInfo(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("slug")] string? Slug,
    [property: JsonPropertyName("project_type")] string? ProjectType,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("icon_url")] string? IconUrl);

// Публичный API Modrinth живёт на api.modrinth.com (не на modrinth.com: там только сайт).
// Сервис требует уникально идентифицирующий User-Agent, иначе отвечает 403.
public static class Modrinth
{
    private const string ApiRoot = "https://api.modrinth.com/v2";
    private const string CdnRoot = "https://cdn.modrinth.com";

    // Иконка может прийти и полным адресом, и путём без домена («/PROMOTIONAL/banner.png»).
    // RemoteImage принимает только http(s), поэтому во втором случае молча оставлял бы заглушку
    // с первой буквой названия — дотягиваем адрес до абсолютного.
    public static string ResolveIconUrl(string? iconUrl)
    {
        string value = (iconUrl ?? "").Trim();
        if (value.Length == 0) return "";
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return value;
        if (value.StartsWith("//")) return "https:" + value;

        // Форма «cdn.modrinth.com/data/…/icon.png» без схемы: домен узнаём по точке в первом
        // сегменте. Путь же вроде /Promotional/banner.png точки в начале не имеет.
        int slash = value.IndexOf('/');
        if ((slash < 0 ? value : value[..slash]).Contains('.')) return "https://" + value;

        return CdnRoot + (value.StartsWith("/") ? value : "/" + value);
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        WriteUserAgent(null, client);
        return client;
    }

    // Modrinth просит указать контакт, чтобы сервис мог написать об абузе: "ForgePanel/2.0 (contact)".
    public static void ApplyUserAgent(string? contact) => WriteUserAgent(contact, Http);

    private static void WriteUserAgent(string? contact, HttpClient client)
    {
        string value = string.IsNullOrWhiteSpace(contact)
            ? "ForgePanel/2.0 (Minecraft server manager for Windows)"
            : $"ForgePanel/2.0 ({contact.Trim()})";

        // TryAddWithoutValidation: пользователь может вписать контакт со скобками — падать из-за этого нельзя.
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", value);
    }

    // Тип проекта задаёт вызывающий код одним словом: «datapack» или всё остальное — моды.
    // Проверено на живом API: отдельного типа «карта» у Modrinth нет, а плагины там лежат в
    // project_type:mod и отличаны только категориями загрузчика (paper, spigot, bukkit).
    public static async Task<List<ModrinthProject>> SearchAsync(
        string query, string? loader, string? gameVersion, string kind = "mod",
        int limit = 20, CancellationToken ct = default)
    {
        bool datapack = string.Equals(kind, "datapack", StringComparison.OrdinalIgnoreCase);

        var facets = new List<List<string>> { new() { datapack ? "project_type:datapack" : "project_type:mod" } };
        // У датапаков загрузчика не бывает: фильтр по fabric/forge отсёк бы весь результат.
        if (!string.IsNullOrWhiteSpace(loader) && !datapack)
            facets.Add(new List<string> { "categories:" + loader });
        if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add(new List<string> { "versions:" + gameVersion });

        var url = new StringBuilder(ApiRoot)
            .Append("/search?limit=").Append(limit)
            .Append("&index=relevance")
            .Append("&query=").Append(Uri.EscapeDataString(query ?? ""))
            .Append("&facets=").Append(Uri.EscapeDataString(JsonSerializer.Serialize(facets)))
            .ToString();

        using var response = await GetAsync(url, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var parsed = await JsonSerializer.DeserializeAsync<ModrinthSearchResponse>(stream, cancellationToken: ct);
        return parsed?.Hits ?? new();
    }

    public static async Task<List<ModrinthVersion>> GetVersionsAsync(
        string projectSlug, string? gameVersion, string? loader, CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
            parts.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion })));
        if (!string.IsNullOrWhiteSpace(loader))
            parts.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader })));

        string url = $"{ApiRoot}/project/{Uri.EscapeDataString(projectSlug)}/version" +
                     (parts.Count > 0 ? "?" + string.Join("&", parts) : "");

        using var response = await GetAsync(url, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var versions = await JsonSerializer.DeserializeAsync<List<ModrinthVersion>>(stream, cancellationToken: ct) ?? new();
        return versions
            .Where(v => v.Files != null && v.Files.Count > 0)
            .OrderByDescending(v => v.DatePublished)
            .ToList();
    }

    public static async Task<string> DownloadAsync(
        ModrinthFile file, string folder, IProgress<(long Done, long Total)>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        string target = UniquePath(folder, file.Filename);
        string actualSha1;

        using (var response = await GetAsync(file.Url, ct, HttpCompletionOption.ResponseHeadersRead))
        {
            long total = response.Content.Headers.ContentLength ?? 0;
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var source = await response.Content.ReadAsStreamAsync(ct);
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

        // SHA1 здесь — проверка целостности загрузки, а не защита от подделки.
        string? expected = file.Hashes?.Sha1;
        if (!string.IsNullOrWhiteSpace(expected) &&
            !string.Equals(actualSha1, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new IOException(
                $"Файл докачался повреждённым: SHA1 {actualSha1}, Modrinth ожидал {expected}. Повтори скачивание.");
        }

        return target;
    }

    // Файл ищется по контрольной сумме: /v2/version_file/{sha1} отдаёт версию целиком (проверено
    // живым запросом — /v2/version/{hash} на тот же SHA-1 отвечает 400, так что путь именно такой).
    // Отсутствие ответа здесь — не поломка, а смысл: «этого файла в базе Modrinth нет», поэтому
    // 404 превращается в null, а не в исключение с текстом ошибки.
    public static async Task<ModrinthVersion?> GetVersionByHashAsync(string sha1, CancellationToken ct = default)
    {
        using var response = await RequestAsync($"{ApiRoot}/version_file/{Uri.EscapeDataString(sha1)}", ct);
        if (response == null) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<ModrinthVersion>(stream, cancellationToken: ct);
    }

    // Краткие сведения о проекте по его id (или slug): название, иконка, тип для адреса страницы.
    public static async Task<ModrinthProjectInfo?> GetProjectAsync(string idOrSlug, CancellationToken ct = default)
    {
        using var response = await RequestAsync($"{ApiRoot}/project/{Uri.EscapeDataString(idOrSlug)}", ct);
        if (response == null) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<ModrinthProjectInfo>(stream, cancellationToken: ct);
    }

    // Страница проекта на сайте: сегмент пути совпадает с project_type из API, неизвестный тип
    // ведём на mod — по нему живёт подавляющее большинство проектов.
    public static string ProjectPageUrl(string? slug, string? projectType)
    {
        string name = (slug ?? "").Trim();
        if (name.Length == 0) return "";

        string segment = projectType?.Trim().ToLowerInvariant() switch
        {
            "datapack" => "datapack",
            "resourcepack" => "resourcepack",
            "modpack" => "modpack",
            _ => "mod"
        };
        return $"https://modrinth.com/{segment}/{Uri.EscapeDataString(name)}";
    }

    private static async Task<HttpResponseMessage> GetAsync(
        string url, CancellationToken ct, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead) =>
        await RequestAsync(url, ct, option) ?? throw new IOException("Modrinth не нашёл такой проект или версию.");

    // Ответ 404 отдаётся как null, если вызывающий готов к «не найдено»; остальные ошибки — всегда
    // IOException с человеческим текстом.
    private static async Task<HttpResponseMessage?> RequestAsync(
        string url, CancellationToken ct, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(url, option, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            string reason = ex is TaskCanceledException
                ? "Modrinth не ответил за 30 секунд — проверь интернет."
                : "Нет связи с Modrinth: " + ex.Message;
            throw new IOException(reason, ex);
        }

        if (response.IsSuccessStatusCode) return response;

        int code = (int)response.StatusCode;
        response.Dispose();
        if (code == 404) return null;

        throw new IOException(code switch
        {
            403 => "Modrinth отклонил запрос (обычно помогает корректный User-Agent или пауза после частых запросов).",
            429 => "Слишком много запросов к Modrinth — подожди минуту и повтори.",
            >= 500 => "У Modrinth временная проблема на стороне сервера.",
            _ => "Modrinth ответил с кодом " + code + "."
        });
    }

    // Существующий файл не перезаписываем: мод с таким именем уже лежит в папке.
    private static string UniquePath(string folder, string filename)
    {
        string baseName = Path.GetFileNameWithoutExtension(filename);
        string extension = Path.GetExtension(filename);
        string candidate = Path.Combine(folder, filename);
        int counter = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(folder, $"{baseName} ({counter++}){extension}");
        }
        return candidate;
    }
}
