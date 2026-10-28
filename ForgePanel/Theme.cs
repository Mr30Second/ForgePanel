using System;
using System.Windows;
using System.Windows.Media;

namespace ForgePanel;

// Тема одна — тёмная: значения переписаны с макета. Акцент выбирается на странице «Настройки»
// и меняет Color у тех же кистей, что объявлены в App.xaml. Кисти не заморожены, поэтому
// перекрашивается всё, включая StaticResource внутри шаблонов кнопок и полей ввода.
// Правило отсюда: никаких цветов, записанных числом, ни в XAML, ни в коде.
public static class Theme
{
    private static readonly string[] NeutralKeys =
    {
        "Bg", "Panel", "Panel2", "Line", "Muted", "Text",
        "FieldBg", "HeaderBg", "ToolbarBg", "SearchBg", "SearchLine", "FrameLine", "Ok", "Warn"
    };

    private static readonly string[] Neutrals =
    {
        "#0B0D12", "#14171E", "#1B1F29", "#2A2F3A", "#8290A8", "#F4F5FA",
        "#101319", "#11141B", "#0F1218", "#181C26", "#303747", "#252A34", "#4ADE80", "#FBBF24"
    };

    public sealed record AccentPalette(string Key, string Label, string[] Colors);

    // Порядок кистей: Accent, AccentLine, AccentText, AccentSoft, AccentSoftBg,
    // AccentSoftLine, Hover, Pressed, OnAccent.
    private static readonly string[] AccentKeys =
    {
        "Accent", "AccentLine", "AccentText", "AccentSoft", "AccentSoftBg",
        "AccentSoftLine", "Hover", "Pressed", "OnAccent"
    };

    // Первый акцент — значение по умолчанию и ответ на неизвестный ключ в settings.json.
    public static readonly AccentPalette[] Accents =
    {
        new("blue", "Синий",
            new[] { "#3B82F6", "#2B6CB8", "#93C5FD", "#60A5FA", "#16263F", "#2F5FA8", "#1B2434", "#223A5C", "#FFFFFF" }),

        new("green", "Зелёный",
            new[] { "#22C55E", "#16A34A", "#86EFAC", "#4ADE80", "#102318", "#2F7A4C", "#1B2A22", "#24402F", "#04170B" }),

        new("violet", "Фиолетовый",
            new[] { "#8B5CF6", "#7653B8", "#C4B5FD", "#A78BFA", "#281B43", "#6841B8", "#292338", "#382852", "#FFFFFF" })
    };

    public static AccentPalette FindAccent(string? accent)
    {
        foreach (AccentPalette item in Accents)
            if (string.Equals(item.Key, accent, StringComparison.OrdinalIgnoreCase)) return item;
        return Accents[0];
    }

    public static string NormalizeAccent(string? accent) => FindAccent(accent).Key;

    public static void Apply(string? accent)
    {
        ResourceDictionary? resources = Application.Current?.Resources;
        if (resources == null) return;

        Write(resources, NeutralKeys, Neutrals);
        Write(resources, AccentKeys, FindAccent(accent).Colors);
    }

    private static void Write(ResourceDictionary resources, string[] keys, string[] colors)
    {
        for (int i = 0; i < keys.Length; i++)
        {
            // Если ключ подменили замороженной кистью, кладём в словарь новую: присваивать Color
            // замороженной кисти нельзя, это бросало бы InvalidOperationException.
            if (resources[keys[i]] is SolidColorBrush brush && !brush.IsFrozen) brush.Color = Hex(colors[i]);
            else resources[keys[i]] = new SolidColorBrush(Hex(colors[i]));
        }
    }

    private static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);
}
