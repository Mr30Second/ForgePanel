using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ForgePanel;

// Все сведения читаются с самого компьютера: реестр, Win32 API, диски и запуск java -version.
// Ссылок на сторонние сервисы здесь нет.
public static class SystemInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public int dwLength;
        public int dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceKey;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

    public static string FormatGb(long bytes) => $"{bytes / 1073741824.0:0.##} ГБ";

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024L => $"{bytes} Б",
        < 1048576L => $"{bytes / 1024.0:0.#} КБ",
        < 1073741824L => $"{bytes / 1048576.0:0.#} МБ",
        _ => $"{bytes / 1073741824.0:0.##} ГБ"
    };

    public static string CpuName()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Процессор не определён";
        }
        catch (Exception ex)
        {
            return "Не удалось прочитать: " + ex.Message;
        }
    }

    public static int CpuMhz()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return Convert.ToInt32(key?.GetValue("~MHz") ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    public static (long Total, long Free) Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        return ((long)status.ullTotalPhys, (long)status.ullAvailPhys);
    }

    public static string GpuName()
    {
        var names = new List<string>();
        for (uint index = 0; index < 16; index++)
        {
            var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            if ((device.StateFlags & 1) != 0 && !string.IsNullOrWhiteSpace(device.DeviceString))
                names.Add(device.DeviceString.Trim());
        }

        return names.Count == 0 ? "Определить не удалось" : string.Join(" · ", names.Distinct());
    }

    public static List<(string Name, long Total, long Free)> Drives()
    {
        var list = new List<(string, long, long)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                list.Add((drive.Name, drive.TotalSize, drive.TotalFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Съёмные и сетевые тома могут быть недоступны — просто пропускаем.
            }
        }
        return list;
    }

    public static string WindowsName()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = key?.GetValue("ProductName") as string ?? "Windows";
            string display = (key?.GetValue("DisplayVersion") as string)?.Trim() ?? "";
            string build = key?.GetValue("CurrentBuildNumber") as string ?? "";
            return $"{product} {display} (сборка {build})".Replace("  ", " ").Trim();
        }
        catch
        {
            return RuntimeInformation.OSDescription;
        }
    }

    private static List<JavaInstall>? installed;

    // Все найденные java, от самой свежей к старой. Перебираем не одну первую, потому что
    // сервер 1.21 на Java 8 не стартует в принципе, а на машине рядом с ней обычно лежит
    // ещё и нормальная JDK из Program Files.
    public static List<JavaInstall> JavaInstalls()
    {
        if (installed != null) return installed;

        var found = new List<JavaInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var candidate in JavaCandidates())
            {
                if (!File.Exists(candidate) || !seen.Add(candidate)) continue;
                string? version = ReadJavaVersion(candidate);
                if (version == null) continue;
                found.Add(new JavaInstall(candidate, version, MajorOf(version)));
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Что нашли до исключения — то и покажем.
        }

        installed = found
            .OrderByDescending(j => j.Major)
            .ThenBy(j => j.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return installed;
    }

    private static readonly Dictionary<string, JavaInstall?> probed =
        new(StringComparer.OrdinalIgnoreCase);

    // Версия конкретной java.exe — в том числе той, которую человек указал сам и которая не
    // попалась в автоматический поиск (JDK на втором диске, версия, поставленная уже после
    // запуска панели). Без этого шага панель не знает, что за файл у сборки, и не может сказать
    // «подходит» или «нет».
    // Ответ кэшируется: «java -version» — это запуск процесса, а один и тот же файл за сессию
    // спрашивают и проверка сборки, и запуск, и установщик Forge. Обновлённая на том же пути JDK
    // редкость, а перезапуск панели чистит кэш вместе со списком.
    public static JavaInstall? Probe(string javaPath)
    {
        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath)) return null;

        foreach (JavaInstall install in JavaInstalls())
            if (string.Equals(install.Path, javaPath, StringComparison.OrdinalIgnoreCase)) return install;

        if (probed.TryGetValue(javaPath, out JavaInstall? cached)) return cached;

        JavaInstall? read = null;
        string? version = ReadJavaVersion(javaPath);
        if (version != null) read = new JavaInstall(javaPath, version, MajorOf(version));

        probed[javaPath] = read;
        return read;
    }

    // "1.8.0_504" → 8 (до девятой минор жил во второй части), "17.0.10" → 17, "21" → 21.
    public static int MajorOf(string version)
    {
        var parts = version.Split('.', '_');
        if (parts.Length == 0 || !int.TryParse(parts[0], out int first)) return 0;
        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out int second)) return second;
        return first;
    }

    private static IEnumerable<string> JavaCandidates()
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home)) yield return Path.Combine(home, "bin", "java.exe");

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string path = entry.Trim().Trim('"');
            if (path.Length == 0) continue;
            string candidate;
            try { candidate = Path.Combine(path, "java.exe"); }
            catch (ArgumentException) { continue; }
            yield return candidate;
        }

        foreach (var root in new[]
        {
            @"C:\Program Files\Java",
            @"C:\Program Files\Eclipse Adoptium",
            @"C:\Program Files\Microsoft",
            @"C:\Program Files\BellSoft",
            @"C:\Program Files (x86)\Java"
        })
        {
            if (!Directory.Exists(root)) continue;
            string[] folders;
            try { folders = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var folder in folders)
                yield return Path.Combine(folder, "bin", "java.exe");
        }
    }

    private static string? ReadJavaVersion(string javaPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo(javaPath, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            // java -version пишет в stderr, поэтому читаем оба потока.
            string output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* процесс уже завершился */ }
                return null;
            }

            var match = Regex.Match(output, "version \"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }
}

// Одна установленная Java: путь, строка версии из вывода «java -version» и её мажор — им
// сравнивают то, что требует версия Minecraft.
public sealed record JavaInstall(string Path, string Version, int Major);
