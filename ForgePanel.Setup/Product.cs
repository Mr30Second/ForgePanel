using System;
using System.IO;
using System.Reflection;

namespace ForgePanel.Setup
{
    static class Product
    {
        public const string Name = "ForgePanel";
        public const string Exe = "ForgePanel.exe";
        public const string UninstallerExe = "uninstall.exe";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ForgePanel";

        public static string Version
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Build <= 0 && v.Revision <= 0 ? v.Major + "." + v.Minor : v.ToString(3);
            }
        }

        public static string LocalAppData =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Установка в свою учётную запись: не нужен администратор и не светит UAC.
        public static string DefaultInstallDir => Path.Combine(LocalAppData, "Programs", Name);

        // Папка, где панель держит сборки, миры (saves), mod-backups, window.json и settings.json
        // с личным ключом CurseForge. Ни мастер, ни деинсталлятор в неё не заглядывают:
        // проверка на это вшита в удаление отдельным условием, а не оставлена на авось.
        public static string UserDataDir => Path.Combine(LocalAppData, "ForgePanelV2");

        public static string StartMenuDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name);

        public static string DesktopDir =>
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        public static bool IsUserDataPath(string path)
        {
            string data = Path.GetFullPath(UserDataDir + Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(path + Path.DirectorySeparatorChar);
            return candidate.StartsWith(data, StringComparison.OrdinalIgnoreCase);
        }

        // «C:/Games/ForgePanel» Windows принимает, но в реестре и в UninstallString такие
        // пути выглядят лохматее, чем надо, поэтому наружу отдаём только обратные слэши.
        public static string NormalizePath(string path)
        {
            return path == null ? null : path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }
    }
}
