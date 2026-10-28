using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ForgePanel;

// Ключ allow-end живёт в bukkit.yml и есть только у ядер на Bukkit (Paper и его ответвления).
// Vanilla, Fabric, Forge и NeoForge этот файл не читают вовсе, поэтому закрыть Энд «одной кнопкой»
// можно лишь там, где ключ существует, — остальным ядрам поле не показываем.
// Формат правим вручную: файл маленький, вложенность одна, а тянуть ради этого YAML-библиотеку
// значило бы переписать серверу комментарии, которые он сам и оставил для человека.
public static class BukkitYaml
{
    private const string Section = "settings";
    private const string Key = "allow-end";

    public static bool Supports(string? core) =>
        core is "Paper" or "Purpur" or "Folia" or "Leaf" or "Spigot" or "Bukkit";

    public static string FilePath(string folder) => Path.Combine(folder, "bukkit.yml");

    // true/false — что реально записано, null — файла нет или ключ в нём не встречается.
    public static bool? ReadEnd(string folder)
    {
        string file = FilePath(folder);
        if (!File.Exists(file)) return null;

        try
        {
            bool inSettings = false;
            foreach (string line in File.ReadLines(file))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                if (!inSettings)
                {
                    if (KeyOf(trimmed) == Section) inSettings = true;
                    continue;
                }

                // Строка без отступа — начало следующего раздела: ключа в settings не было.
                if (!char.IsWhiteSpace(line[0])) return null;
                if (KeyOf(trimmed) != Key) continue;

                string value = ValueOf(trimmed);
                if (value == "true") return true;
                if (value == "false") return false;
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    // Правка одного ключа с сохранением всего остального файла, включая комментарии сервера.
    public static void SetEnd(string folder, bool allow)
    {
        string file = FilePath(folder);
        var lines = File.Exists(file) ? new List<string>(File.ReadAllLines(file)) : new List<string>();
        string entry = "  " + Key + ": " + (allow ? "true" : "false");

        int section = lines.FindIndex(l => KeyOf(l.Trim()) == Section && l.Trim().Length > 0);
        if (section < 0)
        {
            if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
            lines.Add(Section + ":");
            lines.Add(entry);
        }
        else
        {
            int at = section + 1;
            while (at < lines.Count)
            {
                string trimmed = lines[at].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    at++;
                    continue;
                }
                if (!char.IsWhiteSpace(lines[at][0])) break;
                if (KeyOf(trimmed) == Key) break;
                at++;
            }

            if (at < lines.Count && KeyOf(lines[at].Trim()) == Key) lines[at] = entry;
            else lines.Insert(at, entry);
        }

        File.WriteAllLines(file, lines, new UTF8Encoding(false));
    }

    // «allow-end: false» → «allow-end». Ключ YAML-пары всегда до первого двоеточия.
    private static string KeyOf(string trimmed)
    {
        int colon = trimmed.IndexOf(':');
        return colon <= 0 ? "" : trimmed[..colon].Trim();
    }

    private static string ValueOf(string trimmed)
    {
        int colon = trimmed.IndexOf(':');
        return colon < 0 ? "" : trimmed[(colon + 1)..].Trim();
    }
}
