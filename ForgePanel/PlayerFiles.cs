using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForgePanel;

// Файлы игроков в папке Java-сервера: whitelist.json, ops.json, banned-players.json и
// banned-ips.json. Читаем их как массивы объектов и пишем обратно теми же объектами: у сервера
// в записи есть поля, которых мы не ждём (дата бана, причина, IP), и терять их нельзя.
//
// Почему добавление игрока — не «дописать ник в json»: сервер сверяет игроков по uuid, а не по
// имени, и знает его только сам (в online-mode по профилю Mojang, в offline-mode по схеме
// OfflinePlayer:<ник>). Придумать uuid панель не может, поэтому нового ника без сервера в
// списках не появляется — для этого есть команда whitelist add в консоли запуска.
public sealed class PlayerFile
{
    public const string Whitelist = "whitelist.json";
    public const string Ops = "ops.json";
    public const string BannedPlayers = "banned-players.json";
    public const string BannedIps = "banned-ips.json";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private PlayerFile(string fullPath, string fileName, List<JsonObject> entries, bool exists, string? error)
    {
        FullPath = fullPath;
        FileName = fileName;
        Entries = entries;
        Exists = exists;
        Error = error;
    }

    public string FullPath { get; }
    public string FileName { get; }

    // false — файла ещё нет: сервер создаёт его при первой команде, и пустой список это норма.
    public bool Exists { get; }

    // Файл есть, но не разобрался: показываем текст ошибки и не трогаем его содержимое.
    public string? Error { get; }

    public List<JsonObject> Entries { get; }

    public static PlayerFile Load(string folder, string fileName)
    {
        string full = Path.Combine(folder, fileName);
        if (!File.Exists(full)) return new(full, fileName, new(), false, null);

        try
        {
            var entries = new List<JsonObject>();
            if (JsonNode.Parse(File.ReadAllText(full)) is JsonArray array)
                foreach (JsonNode? node in array)
                    if (node is JsonObject obj) entries.Add(obj);

            return new(full, fileName, entries, true, null);
        }
        catch (Exception ex)
        {
            return new(full, fileName, new(), true, ex.Message);
        }
    }

    public static string? NameOf(JsonObject entry) => Text(entry, "name");
    public static string? IpOf(JsonObject entry) => Text(entry, "ip");
    public static string? UuidOf(JsonObject entry) => Text(entry, "uuid");

    public static int LevelOf(JsonObject entry) =>
        entry.TryGetPropertyValue("level", out JsonNode? node) && node is JsonValue value && value.TryGetValue(out int level)
            ? level
            : 0;

    // Короткий вид uuid: целиком он не читается, а различить два ника по первым символам обычно можно.
    public static string ShortUuid(string? uuid) =>
        string.IsNullOrEmpty(uuid) ? "без uuid" : uuid.Length <= 8 ? uuid : uuid[..8];

    // Запись с тем же ником в другом файле — единственный честный источник uuid для добавления.
    public JsonObject? FindByName(string name) => Entries.FirstOrDefault(entry =>
        string.Equals(NameOf(entry), name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<JsonObject> Without(string name) => Entries.Where(entry =>
        !string.Equals(NameOf(entry), name, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(IpOf(entry), name, StringComparison.OrdinalIgnoreCase));

    // Перезапись всего массива. Клоны обязательны: нода уже привязана к прочитанному массиву,
    // и второй раз в дерево она не встанет.
    public void Rewrite(IEnumerable<JsonObject> entries)
    {
        var array = new JsonArray();
        foreach (JsonObject entry in entries) array.Add((JsonNode)entry.DeepClone());

        // Перенос вместо перезаписи: оборванная запись whitelist.json сервер прочитал бы как пустой
        // список и не пустил бы никого, включая самого владельца сервера.
        SafeWrite.Text(FullPath, array.ToJsonString(Pretty), keepPrevious: false);
    }

    // Новая запись в whitelist или ops: поля те же, что пишет сервер, и ничего лишнего.
    public static JsonObject NewWhitelistEntry(string uuid, string name) =>
        new() { ["uuid"] = uuid, ["name"] = name };

    public static JsonObject NewOpEntry(string uuid, string name) =>
        new()
        {
            ["uuid"] = uuid,
            ["name"] = name,
            ["level"] = 4,
            ["bypassesPlayerLimit"] = false
        };

    private static string? Text(JsonObject entry, string key) =>
        entry.TryGetPropertyValue(key, out JsonNode? node) && node is JsonValue value && value.TryGetValue(out string? text)
            ? text
            : null;
}
