using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ForgePanel.Setup
{
    static class Installer
    {
        // Блокируем только ту копию, которую сами собираемся трогать: панель, запущенная
        // из другой папки, установке не мешает. Заканчивать чужой процесс мастер не имеет
        // права — только попросить.
        public static void EnsureTargetNotRunning(string installDir)
        {
            string root = Path.GetFullPath(installDir + Path.DirectorySeparatorChar);
            foreach (Process process in Process.GetProcessesByName(Product.Name))
            {
                string path = null;
                try { path = process.MainModule.FileName; }
                catch (Exception) { /* 32-битный доступ к 64-битному модулю — считаем, что не наш */ }
                if (path != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "ForgePanel из этой папки уже запущен (процесс " + process.Id + ").\n\n" +
                        "Закройте панель и повторите — мастер не будет ничего убивать сам.");
            }
        }

        public static void Install(Payload payload, string installDir, Action<string, long, long> progress, Func<bool> isCanceled)
        {
            EnsureTargetNotRunning(installDir);
            installDir = Path.GetFullPath(installDir);
            Directory.CreateDirectory(installDir);

            payload.ExtractTo(installDir, progress, isCanceled);

            // Рядом с панелью кладём её же копию без payload: ~20 КБ вместо 73 МБ,
            // и удаление работает даже когда файла-мастера уже нет.
            payload.WriteStubTo(Path.Combine(installDir, Product.UninstallerExe));

            CreateShortcuts(installDir, true);
            CreateShortcuts(installDir, false);
            RegistryStore.Register(installDir);
        }

        public static void CreateShortcuts(string installDir, bool desktop)
        {
            string exe = Path.Combine(installDir, Product.Exe);
            string uninstaller = Path.Combine(installDir, Product.UninstallerExe);

            Directory.CreateDirectory(Product.StartMenuDir);
            Shortcut.Create(Path.Combine(Product.StartMenuDir, Product.Name + ".lnk"),
                exe, installDir, "", "ForgePanel — запуск Minecraft-серверов", exe);
            Shortcut.Create(Path.Combine(Product.StartMenuDir, "Удалить ForgePanel.lnk"),
                uninstaller, installDir, "/uninstall", "Удалить ForgePanel с этого компьютера", uninstaller);

            string desktopLink = Path.Combine(Product.DesktopDir, Product.Name + ".lnk");
            if (desktop)
                Shortcut.Create(desktopLink, exe, installDir, "", "ForgePanel — запуск Minecraft-серверов", exe);
            else
                Shortcut.Remove(desktopLink);
        }

        public static void RemoveShortcuts()
        {
            Shortcut.Remove(Path.Combine(Product.StartMenuDir, Product.Name + ".lnk"));
            Shortcut.Remove(Path.Combine(Product.StartMenuDir, "Удалить ForgePanel.lnk"));
            Shortcut.Remove(Path.Combine(Product.DesktopDir, Product.Name + ".lnk"));
            try
            {
                if (Directory.Exists(Product.StartMenuDir) && Directory.GetFileSystemEntries(Product.StartMenuDir).Length == 0)
                    Directory.Delete(Product.StartMenuDir);
            }
            catch (Exception) { /* пустая папка в меню «Пуск» — не причина сообщать об ошибке */ }
        }

        // Удаляет папку программы. Папка с данными (UserDataDir) под запретом: там сборки,
        // миры, настройки и личный ключ CurseForge — их мастер не создавал и удалять не вправе.
        public static bool DeleteProgramFolder(string installDir, Action<string> note)
        {
            string dir = Path.GetFullPath(installDir);
            if (!Directory.Exists(dir)) return true;

            if (Product.IsUserDataPath(dir))
            {
                if (note != null) note("Папка совпала с папкой данных — файлы оставлены нетронутыми.");
                return false;
            }

            EnsureTargetNotRunning(dir);

            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                // Файл деинсталлятора снимаем отдельно и последним: его образ ещё доли секунды
                // держит родительский процесс, который нас сюда и перезапустил.
                if (string.Equals(Path.GetFileName(file), Product.UninstallerExe, StringComparison.OrdinalIgnoreCase))
                    continue;
                DeleteFileWithRetry(file, note);
            }
            DeleteFileWithRetry(Path.Combine(dir, Product.UninstallerExe), null);

            // Каталоги снимаем от вложенных к внешним: непустую папку Directory.Delete не берёт.
            string[] directories = Directory.GetDirectories(dir, "*", SearchOption.AllDirectories);
            Array.Sort(directories, (a, b) => b.Length - a.Length);
            foreach (string sub in directories) DeleteFolderWithRetry(sub);
            DeleteFolderWithRetry(dir);
            return !Directory.Exists(dir);
        }

        private static void DeleteFileWithRetry(string path, Action<string> note)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (!File.Exists(path)) return;
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    if (note != null) note(path);
                    File.Delete(path);
                    return;
                }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { Thread.Sleep(200); }
            }
            File.Delete(path);
        }

        private static void DeleteFolderWithRetry(string path)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, false);
                    return;
                }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { Thread.Sleep(200); }
            }
            Directory.Delete(path, false);
        }

        // Деинсталлятор лежит внутри папки, которую сам же удаляет, поэтому он переезжает
        // в Temp и продолжает работу оттуда.
        public static string MoveSelfToTempAndRestart(string installDir, bool silent)
        {
            string current = SelfPath();
            if (!Path.GetFullPath(current).StartsWith(Path.GetFullPath(installDir + Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return null;

            string temp = Path.Combine(Path.GetTempPath(), Product.UninstallerExe + "-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(current, temp, true);

            // /moved передаёт ПАПКУ, а не путь к своему файлу: продолжателю нужно знать, что удалять,
            // и заодно не перезапускаться ещё раз.
            ProcessStartInfo start = new ProcessStartInfo(temp,
                "/uninstall /moved \"" + installDir + "\"" + (silent ? " /silent" : ""));
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WorkingDirectory = Path.GetTempPath();
            Process.Start(start);
            return temp;
        }

        public static string SelfPath()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 0 && !string.IsNullOrEmpty(args[0])) return Path.GetFullPath(args[0]);
            return System.Reflection.Assembly.GetExecutingAssembly().Location;
        }

        // Копия в Temp удаляет саму себя уже после закрытия окна: cmd ждёт секунду и стирает файл.
        public static void ScheduleSelfDelete()
        {
            string self = SelfPath();
            if (!self.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                ProcessStartInfo start = new ProcessStartInfo("cmd.exe",
                    "/c ping -n 3 127.0.0.1 >nul & del /q /f \"" + self + "\"");
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                Process.Start(start);
            }
            catch (Exception) { /* файл в Temp: не критично */ }
        }
    }
}
