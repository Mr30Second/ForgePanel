using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ForgePanel;

// server.properties Bedrock Dedicated Server. Набор ключей здесь свой, а не общий с Java:
// у Bedrock нет motd — список серверов показывает server-name; view-distance начинается с 5,
// а не с 2, и означает потолок для клиента, а не то, сколько сервер отдаёт сам. Сложность,
// наоборот, совпала: те же четыре слова. Проверено по официальному справочнику свойств BDS.
//
// Запуска Bedrock-сборок в панели нет, поэтому файл правится в момент сохранения настроек:
// значение, которое некому применить при старте, лежало бы в конфигурации мёртвым грузом.
// Отсюда и порядок другой, чем у Java, — панель сначала показывает, что именно изменится, и
// спрашивает разрешение.
public static class BedrockProperties
{
    // Одна правка: ключ, что было в файле и что в него встанет. «Ключа в файле нет» — не значение,
    // а честное «добавим строку»: дописывать нечего человек должен увидеть отдельно.
    public sealed record Change(string Key, string Was, string Will);

    // Файл создаёт сам bedrock_server при первом запуске, поэтому «файла нет» — не ошибка,
    // а «применять пока не к чему». Второй признак в этом случае не имеет смысла: пустой список.
    public static (bool Found, List<Change> Changes) Diff(ServerProfile p, string folder)
    {
        string file = Path.Combine(folder, "server.properties");
        if (!File.Exists(file)) return (false, new());

        string[] lines = File.ReadAllLines(file);
        var changes = new List<Change>();

        foreach ((string key, string value) in Wanted(p))
        {
            int index = Array.FindIndex(lines, line => ServerProperties.Key(line) == key);
            if (index < 0)
            {
                changes.Add(new Change(key, "ключа в файле нет", value));
                continue;
            }

            string current = ServerProperties.Value(lines[index]);
            if (current != value) changes.Add(new Change(key, current, value));
        }

        return (true, changes);
    }

    // Строки и комментарии файла не трогаем нигде, кроме самой правки: в server.properties
    // нередко живут значения, которые человек выставлял руками, и они его и остаются.
    public static void Apply(ServerProfile p, string folder)
    {
        string file = Path.Combine(folder, "server.properties");
        var lines = File.ReadAllLines(file).ToList();

        foreach ((string key, string value) in Wanted(p))
        {
            int index = lines.FindIndex(line => ServerProperties.Key(line) == key);
            if (index < 0) lines.Add(key + "=" + value);
            else lines[index] = key + "=" + value;
        }

        SafeWrite.Text(file, string.Join(Environment.NewLine, lines) + Environment.NewLine, keepPrevious: false);
    }

    // Пустое поле конфигурации сюда не попадает: «не задано» для панели значит «в файле своё»,
    // и возвращать чужие правки к значениям лаунчера при каждом сохранении — способ их потерять.
    private static List<(string Key, string Value)> Wanted(ServerProfile p)
    {
        var wanted = new List<(string Key, string Value)>();

        string name = OneLine(p.Motd);
        if (name.Length > 0) wanted.Add(("server-name", name));
        if (p.MaxPlayers != null) wanted.Add(("max-players", p.MaxPlayers.Value.ToString()));
        if (p.OnlineMode != null) wanted.Add(("online-mode", p.OnlineMode.Value ? "true" : "false"));
        if (p.ViewDistance != null) wanted.Add(("view-distance", p.ViewDistance.Value.ToString()));
        if (p.Difficulty.Length > 0) wanted.Add(("difficulty", p.Difficulty));

        return wanted;
    }

    // Значение читается одной строкой, а точка с запятой в server-name не разрешена самим
    // форматом Bedrock: с ней сервер перестаёт понимать файл.
    private static string OneLine(string value) =>
        new string(value.Replace(";", " ").Replace("\r", " ").Replace("\n", " ").ToArray()).Trim();
}
