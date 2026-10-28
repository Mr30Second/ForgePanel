using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ForgePanel;

// Единственный путь, где панель хранит своё: конфиг сборок, настройки, кэш версий, копии миров и
// журнал падений. Статический, потому что за ним ходит и App — в момент, когда главного окна ещё нет.
public static class PanelPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgePanelV2");

    public static string CrashLogFile => Path.Combine(DataDir, "crash.log");
}

// Файл, который человек не хочет потерять, пишется в два шага: сначала временный рядом, потом
// перенос поверх прежнего. Обрыв на полпути оставляет старый целый файл, а не половину нового.
// Предыдущую копию держим только там, где её нечем восстановить иначе: в settings.json лежит
// персональный ключ CurseForge, заводить второй экземпляр секрета в папке незачем.
public static class SafeWrite
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static void Json(string file, object value, bool keepPrevious) =>
        Text(file, JsonSerializer.Serialize(value, Pretty), keepPrevious);

    public static void Text(string file, string content, bool keepPrevious)
    {
        string full = Path.GetFullPath(file);
        string? dir = Path.GetDirectoryName(full);
        if (dir != null) Directory.CreateDirectory(dir);

        string temp = full + ".new";
        try
        {
            File.WriteAllText(temp, content, Utf8NoBom);
            if (keepPrevious && File.Exists(full)) File.Copy(full, full + ".bak", overwrite: true);
            File.Move(temp, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Недописанный временный файл не должен оставаться в папке как мусор: человек
            // примет его за конфиг. Удаляем молча — настоящая ошибка всё равно уходит наверх.
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
