using System;
using System.IO;
using System.Text;

namespace ForgePanel;

// Журнал падений панели. Нужен затем, чтобы у человека была одна строка, которую можно показать
// тому, кто чинит: без файла «оно упало» превращается в гадание по памяти.
// Метод принципиально не бросает наружу ничего — вызывается уже в момент аварии, и вторая авария
// из-за журнала была бы худшим из исходов.
public static class CrashLog
{
    private const long CapBytes = 512 * 1024;

    public static void Write(Exception? error, string origin)
    {
        try
        {
            string file = PanelPaths.CrashLogFile;
            Directory.CreateDirectory(PanelPaths.DataDir);

            // Ротация грубая: вместо хвоста недельной давности остаётся пустой файл и новая запись.
            // История падений ценна ровно до того момента, как начинает стоить места.
            var info = new FileInfo(file);
            if (info.Exists && info.Length > CapBytes) File.WriteAllText(file, "");

            var text = new StringBuilder();
            text.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" · ").Append(origin)
                .Append(" · ").Append(error?.GetType().Name ?? "без исключения").Append('\n');
            if (error != null)
            {
                text.Append(error.Message).Append('\n');
                if (error.InnerException != null)
                    text.Append("внутри: ").Append(error.InnerException.GetType().Name)
                        .Append(" · ").Append(error.InnerException.Message).Append('\n');
                text.Append(error.StackTrace).Append('\n');
            }

            text.Append('\n');
            File.AppendAllText(file, text.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // Ловим всё подряд, включая нехватку места и закрытый на чтение диск: здесь умирать
            // уже нечем, панель падает независимо от того, записался журнал или нет.
        }
    }
}
