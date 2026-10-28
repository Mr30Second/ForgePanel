using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ForgePanel;

public sealed record MinecraftVersion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Kind,
    [property: JsonPropertyName("releaseTime")] DateTime Released);

public sealed record VersionLoadResult(IReadOnlyList<MinecraftVersion> Versions, bool FromCache, string Note);

// Список версий берётся из официального манифеста Mojang. При недоступности сети подключается
// кэш на диске, чтобы список не исчезал у офлайне.
public static class MinecraftVersionList
{
    private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string CacheFileName = "minecraft-versions.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static List<MinecraftVersion>? memory;

    private static string CachePath(string dataDir) => Path.Combine(dataDir, CacheFileName);

    public static async Task<VersionLoadResult> LoadAsync(string dataDir, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && memory is { Count: > 0 })
            return new VersionLoadResult(memory, true, "список уже был загружен в этой сессии");

        try
        {
            using var stream = await Http.GetStreamAsync(ManifestUrl, ct);
            var manifest = await JsonSerializer.DeserializeAsync<ManifestDocument>(stream, JsonOptions, ct)
                           ?? throw new InvalidDataException("Манифест версий вернулся пустым.");

            var versions = manifest.Versions ?? new List<MinecraftVersion>();
            if (versions.Count == 0)
                throw new InvalidDataException("В манифесте нет ни одной версии.");

            memory = versions;
            await SaveCacheAsync(dataDir, versions, ct);
            return new VersionLoadResult(versions, false, "официальный манифест Mojang");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cached = await ReadCacheAsync(dataDir);
            if (cached is { Count: > 0 })
            {
                memory = cached;
                return new VersionLoadResult(cached, true, "из кэша на диске, потому что сеть недоступна: " + ex.Message);
            }

            throw new IOException($"Список версий не получен ({ex.Message}), а кэша на диске ещё нет — введи версию вручную.", ex);
        }
    }

    private static async Task SaveCacheAsync(string dataDir, List<MinecraftVersion> versions, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            await File.WriteAllTextAsync(CachePath(dataDir), JsonSerializer.Serialize(versions, JsonOptions), ct);
        }
        catch (IOException)
        {
            // Кэш — только ускоритель: без него список всё равно работает.
        }
    }

    private static async Task<List<MinecraftVersion>?> ReadCacheAsync(string dataDir)
    {
        try
        {
            var file = CachePath(dataDir);
            if (!File.Exists(file)) return null;
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<List<MinecraftVersion>>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private sealed record ManifestDocument([property: JsonPropertyName("versions")] List<MinecraftVersion>? Versions);
}
