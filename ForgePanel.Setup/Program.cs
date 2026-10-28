using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;

namespace ForgePanel.Setup
{
    static class Program
    {
        private static bool _silent;

        [STAThread]
        static int Main(string[] args)
        {
            _silent = Has(args, "/silent");
            try
            {
                if (Has(args, "/pack")) return Pack(args);
                if (Has(args, "/uninstall") || LooksLikeUninstaller()) return Uninstall(args);
                return Install(args);
            }
            catch (Exception ex)
            {
                Fail(ex);
                return 1;
            }
        }

        // Голый stub без payload — это и есть install\uninstall.exe. Имя файла, а не «нет футера»:
        // оборванный при скачивании мастер тоже не читается, и притворяться деинсталлятором
        // он не должен — иначе пользователь ненароком удалит установку.
        private static bool LooksLikeUninstaller()
        {
            return string.Equals(Path.GetFileNameWithoutExtension(Installer.SelfPath()),
                Path.GetFileNameWithoutExtension(Product.UninstallerExe), StringComparison.OrdinalIgnoreCase);
        }

        private static int Install(string[] args)
        {
            Payload payload = Container.Open(Installer.SelfPath());
            try
            {
                if (_silent)
                {
                    string dir = Product.NormalizePath(After(args, "/dir") ?? Product.DefaultInstallDir);
                    Installer.EnsureTargetNotRunning(dir);
                    Directory.CreateDirectory(dir);
                    payload.ExtractTo(dir, null, null);
                    payload.WriteStubTo(Path.Combine(dir, Product.UninstallerExe));
                    Installer.CreateShortcuts(dir, !Has(args, "/nodesktop"));
                    RegistryStore.Register(dir);
                    return 0;
                }

                Application app = new Application();
                app.Run(new Wizard(payload));
                return 0;
            }
            catch
            {
                payload.Dispose();
                throw;
            }
        }

        private static int Uninstall(string[] args)
        {
            string moved = After(args, "/moved");
            string dir = moved ?? RegistryStore.ReadInstallLocation() ?? Product.DefaultInstallDir;

            // Сам себя из удаляемой папки не вырвать: переезжаем в Temp и продолжаем оттуда.
            if (moved == null && Installer.MoveSelfToTempAndRestart(dir, _silent) != null) return 0;

            if (_silent)
            {
                Installer.EnsureTargetNotRunning(dir);
                Installer.DeleteProgramFolder(dir, null);
                Installer.RemoveShortcuts();
                RegistryStore.Unregister();
                Installer.ScheduleSelfDelete();
                return 0;
            }

            Application app = new Application();
            app.Run(new UninstallWindow(dir));
            Installer.ScheduleSelfDelete();
            return 0;
        }

        // ---------- упаковка: /pack <payloadDir> <stubExe> <outFile> ----------

        private static int Pack(string[] args)
        {
            string payloadDir = After(args, "/payload");
            string stub = After(args, "/stub");
            string output = After(args, "/out");
            StringBuilder log = new StringBuilder();

            if (payloadDir == null || stub == null || output == null)
            {
                File.WriteAllText(FallbackLog(),
                    "/pack требует /payload <папка> /stub <мастер.exe> /out <готовый файл>");
                return 2;
            }

            string report = output + ".log";
            try
            {
                payloadDir = Path.GetFullPath(payloadDir);
                stub = Path.GetFullPath(stub);
                output = Path.GetFullPath(output);

                string exe = Path.Combine(payloadDir, Product.Exe);
                if (!File.Exists(exe)) throw new FileNotFoundException(
                    "В payload нет " + Product.Exe + ": сначала нужно собрать панель. Искали здесь: " + exe);

                string tempZip = Path.Combine(Path.GetTempPath(), "forgepanel-payload-" + Guid.NewGuid().ToString("N") + ".zip");
                string[] exclude = (After(args, "/exclude") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                Log(log, "payload: " + payloadDir + "  " + Fmt.Bytes(DirectorySize(payloadDir)) + "  файлов=" +
                          Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories).Length +
                          (exclude.Length == 0 ? "" : "  исключается: " + string.Join(", ", exclude)));

                try
                {
                    DateTime start = DateTime.Now;
                    int skipped = BuildZip(payloadDir, tempZip, exclude);
                    long zipLength = new FileInfo(tempZip).Length;
                    Log(log, "zip: " + Fmt.Bytes(zipLength) + " за " + (int)(DateTime.Now - start).TotalSeconds +
                              " с, файлов пропущено: " + skipped);

                    using (FileStream target = new FileStream(output, FileMode.Create, FileAccess.Write))
                    {
                        using (FileStream s = new FileStream(stub, FileMode.Open, FileAccess.Read, FileShare.Read)) s.CopyTo(target);
                        long prefix = target.Position;
                        using (FileStream z = new FileStream(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read)) z.CopyTo(target);
                        Container.WriteFooter(target, zipLength);
                        Log(log, "stub: " + Fmt.Bytes(prefix) + "   итог: " + Fmt.Bytes(target.Length));
                    }
                }
                finally
                {
                    if (File.Exists(tempZip)) File.Delete(tempZip);
                }

                // Проверка на месте: открываем готовый файл так же, как это сделает мастер.
                using (Payload packed = Container.Open(output))
                    Log(log, "проверка: читается, записей=" + packed.Entries + ", распакуется в " + Fmt.Bytes(packed.TotalBytes));

                Log(log, "готово: " + output);
                File.WriteAllText(report, log.ToString());
                return 0;
            }
            catch (Exception ex)
            {
                Log(log, "ОШИБКА: " + ex);
                try { File.WriteAllText(report, log.ToString()); } catch (Exception) { }
                return 1;
            }
        }

        private static void Log(StringBuilder log, string line) => log.AppendLine(line);

        private static string FallbackLog()
        {
            string path = Path.Combine(Path.GetTempPath(), "ForgePanel-setup-error.log");
            try { File.WriteAllText(path, DateTime.Now + " " + string.Join(" ", Environment.GetCommandLineArgs())); }
            catch (Exception) { }
            return path;
        }

        // ZipFile.CreateFromDirectory не подходит: в папке publish лежит libSkiaSharp.pdb на 91 МБ,
        // а символы мастеру не нужны вообще. Отсюда собственный обход с списком исключений.
        private static int BuildZip(string sourceDir, string zipFile, string[] exclude)
        {
            string root = Path.GetFullPath(sourceDir + Path.DirectorySeparatorChar);
            int skipped = 0;
            using (ZipArchive zip = ZipFile.Open(zipFile, ZipArchiveMode.Create))
            {
                foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    string full = Path.GetFullPath(file);
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    string relative = full.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/');
                    if (IsExcluded(relative, Path.GetFileName(full), exclude))
                    {
                        skipped++;
                        continue;
                    }
                    zip.CreateEntryFromFile(full, relative, CompressionLevel.Optimal);
                }
            }
            return skipped;
        }

        private static bool IsExcluded(string relativePath, string fileName, string[] patterns)
        {
            foreach (string raw in patterns)
            {
                string pattern = raw.Trim();
                if (pattern.Length == 0) continue;

                if (pattern.StartsWith("*."))
                {
                    // «*.pdb» — все файлы с таким расширением, где бы они ни лежали.
                    if (fileName.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase)) return true;
                    continue;
                }

                string folder = pattern.TrimEnd('/', '\\') + "/";
                if (relativePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static long DirectorySize(string dir)
        {
            long total = 0;
            foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) total += new FileInfo(file).Length;
            return total;
        }

        private static void Fail(Exception ex)
        {
            string text = (ex is InvalidOperationException || ex is InvalidDataException)
                ? ex.Message
                : ex.GetType().Name + ": " + ex.Message;

            if (_silent)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "ForgePanel-setup-error.log"),
                        DateTime.Now + " " + text + Environment.NewLine);
                }
                catch (Exception) { }
                return;
            }

            Application app = new Application();
            app.Run(new PromptWindow(text, "Понятно", ""));
        }

        private static bool Has(string[] args, string flag)
        {
            foreach (string arg in args)
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Следующий за флагом элемент; для /pack и /dir читается ровно так, как пишется в скрипте.
        private static string After(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
