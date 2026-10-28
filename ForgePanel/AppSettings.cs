using System;
using System.IO;
using System.Text.Json;

namespace ForgePanel;

// Настройки приложения лежат рядом с servers.json: ключ CurseForge, контакт для Modrinth,
// локальный профиль, акцент и отображение снапшотов. Тема одна — тёмная, переключать нечего.
public sealed class AppSettings
{
    public string CurseForgeApiKey { get; set; } = "";
    public string ModrinthContact { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string Accent { get; set; } = Theme.Accents[0].Key;
    public bool ShowSnapshots { get; set; } = true;

    // Отсутствие файла — это обычные значения по умолчанию, а не авария. Нечитаемый файл тоже
    // ронять панель не заставляем, но возвращаем причину: молча сброшенный ключ CurseForge человек
    // иначе ищет полчаса, думая, что его украли.
    public static (AppSettings Settings, string Error) Load(string dataDir)
    {
        string file = Path.Combine(dataDir, "settings.json");
        if (!File.Exists(file)) return (new AppSettings(), "");

        try
        {
            return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? new AppSettings(), "");
        }
        catch (Exception ex)
        {
            return (new AppSettings(), ex.Message);
        }
    }

    // Копию .bak здесь не ведём нарочно: в этом файле лежит персональный ключ CurseForge, и второй
    // его экземпляр в папке — это ещё одно место, где секрет можно найти или забыть удалить.
    public void Save(string dataDir) =>
        SafeWrite.Json(Path.Combine(dataDir, "settings.json"), this, keepPrevious: false);
}
