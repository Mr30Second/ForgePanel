using System;
using System.IO;
using System.Text.Json;

namespace ForgePanel;

// Как человек смотрит на библиотеку: плитками или списком, всё разом или только избранное.
// Отдельный файл, а не поле в settings.json — в том лежит персональный ключ CurseForge, и лишний
// повтор записи при переключении вида не должен его касаться. Потеря этого файла не стоит ничего:
// вернутся обычные плитки, ровно как при первом запуске.
public sealed class LibraryView
{
    public bool AsList { get; set; }
    public bool OnlyFavorites { get; set; }

    private static string FileName(string dataDir) => Path.Combine(dataDir, "view.json");

    public static LibraryView Load(string dataDir)
    {
        try
        {
            string file = FileName(dataDir);
            return File.Exists(file)
                ? JsonSerializer.Deserialize<LibraryView>(File.ReadAllText(file)) ?? new LibraryView()
                : new LibraryView();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new LibraryView();
        }
    }

    // Пишем сразу по клику, а не при закрытии панели: следующий запуск должен застать тот вид,
    // который человек выбрал, даже если панель после этого упала.
    public void Save(string dataDir)
    {
        try
        {
            SafeWrite.Json(FileName(dataDir), this, keepPrevious: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write(ex, "память вида библиотеки");
        }
    }
}
