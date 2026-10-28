using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ForgePanel;

// Журнал ведёт само ядро: Java-сервер пишет в logs/latest.log внутри папки сборки. Панель держит
// в памяти только последнюю сессию, а файл живёт между запусками, поэтому после перезапуска
// лаунчера причину вчерашнего падения можно прочитать только оттуда.
public static class ServerLogs
{
    // Хвост, а не весь файл: у сервера с включённым debug лог набежает на десятки мегабайт,
    // и читать его целиком ради окна в 500 строк незачем.
    private const long MaxBytes = 512_000;

    public sealed record Tail(string Path, string Text, int Lines);

    public static Tail? Read(string buildFolder, int maxLines = 400)
    {
        string? file = FindLatest(Path.Combine(buildFolder, "logs"));
        if (file == null) return null;

        try
        {
            // FileShare.ReadWrite обязателен: сервер держит свой лог открытым на дозапись,
            // и обычный File.OpenRead бросил бы IOException ровно тогда, когда журнал нужнее всего.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = stream.Length;
            if (length == 0) return new Tail(file, "", 0);

            int take = (int)Math.Min(length, MaxBytes);
            stream.Seek(-take, SeekOrigin.End);
            var raw = new byte[take];
            int read = Fill(stream, raw, take);
            if (read == 0) return new Tail(file, "", 0);

            var lines = new List<string>(
                Encoding.UTF8.GetString(raw, 0, read).Split('\n').Select(TrimReturn));

            // Если читали не с начала файла, первая строка — обрезок предыдущей.
            if (take < length && lines.Count > 0) lines.RemoveAt(0);
            if (lines.Count > maxLines) lines = lines.GetRange(lines.Count - maxLines, maxLines);
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
                lines.RemoveAt(lines.Count - 1);

            return new Tail(file, string.Join("\n", lines), lines.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string TrimReturn(string line) =>
        line.Length > 0 && line[line.Length - 1] == '\r' ? line[..^1] : line;

    // Stream.Read не обязан отдавать запрошенное за один вызов.
    private static int Fill(Stream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int got = stream.Read(buffer, read, count - read);
            if (got <= 0) break;
            read += got;
        }
        return read;
    }

    // latest.log — константа log4j у Java-ядер. Bedrock складывает файлы с датой в имени,
    // поэтому для него берём самый свежий .log в той же папке.
    private static string? FindLatest(string logsFolder)
    {
        if (!Directory.Exists(logsFolder)) return null;

        string current = Path.Combine(logsFolder, "latest.log");
        if (File.Exists(current)) return current;

        try
        {
            return Directory.EnumerateFiles(logsFolder, "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
