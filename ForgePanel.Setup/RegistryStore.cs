using System;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace ForgePanel.Setup
{
    // Только HKCU: администратор не нужен, и запись видна именно тому пользователю,
    // которому принадлежит установка.
    static class RegistryStore
    {
        public static void Register(string installDir)
        {
            string uninstaller = Path.Combine(installDir, Product.UninstallerExe);
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Product.UninstallKey))
            {
                if (key == null)
                    throw new InvalidOperationException(
                        "Не удалось открыть раздел реестра «Установка и удаление программ». ForgePanel установлен, " +
                        "но в списке программ он не появится.");

                key.SetValue("DisplayName", Product.Name);
                key.SetValue("DisplayVersion", Product.Version);
                key.SetValue("Publisher", "ForgePanel");
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", Path.Combine(installDir, Product.Exe));
                key.SetValue("UninstallString", Quote(uninstaller) + " /uninstall");
                key.SetValue("QuietUninstallString", Quote(uninstaller) + " /uninstall /silent");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(SizeOf(installDir) / 1024), RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }
        }

        public static void Unregister()
        {
            Registry.CurrentUser.DeleteSubKey(Product.UninstallKey, false);
        }

        // Деинсталлятор должен знать, что удалять, когда его запустили не из папки установки.
        public static string ReadInstallLocation()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Product.UninstallKey))
            {
                object value = key?.GetValue("InstallLocation");
                string text = value as string;
                return string.IsNullOrWhiteSpace(text) ? null : Product.NormalizePath(Path.GetFullPath(text));
            }
        }

        public static string ReadDisplayVersion()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Product.UninstallKey))
            {
                string text = key?.GetValue("DisplayVersion") as string;
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
        }

        // Деинсталлятор вызывается через cmd-строку, поэтому путь в кавычках обязателен:
        // без них папка с пробелом («C:\Users\Alex Ivan\...») развалится на два аргумента.
        private static string Quote(string path) => "\"" + path + "\"";

        private static long SizeOf(string dir)
        {
            long total = 0;
            try
            {
                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    total += new FileInfo(file).Length;
            }
            catch (Exception)
            {
                // Оценка размера — украшение карточки в списке программ, а не часть установки.
            }
            return total;
        }
    }
}
