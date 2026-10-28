using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ForgePanel;

public enum ServerPhase
{
    Running,
    Stopping,
    Stopped
}

// Один запущенный сервер: java с перенаправленными stdin и stdout, чтобы журнал и команды
// жили внутри ForgePanel, а не в отдельном чёрном окне. Процесс остаётся дочерним, поэтому
// панель при закрытии останавливает его сама — иначе остался бы висящий сервер с оторванной pipe.
public sealed class ServerInstance : IDisposable
{
    // Буфер журнала ограничен: сервер может напечатать десятки тысяч строк, а держать всё в памяти
    // смысла нет — отрезаем голову, когда список перерастает примерно 400 КБ текста.
    private const int BufferLimit = 400_000;

    private readonly Process process;
    private readonly object gate = new();
    private readonly StringBuilder buffer = new();

    private ServerPhase phase = ServerPhase.Running;
    private bool disposed;

    // Номер последней записанной строки. Страница консоли запоминает, до какого номера уже
    // вывела буфер, и по номеру же отбрасывает события, которые успели встать в очередь
    // Dispatcher до её постройки — иначе первые строки журнала появляются дважды.
    private long written;

    private ServerInstance(ServerProfile profile, string folder, string coreJar, Process process,
        string arguments, bool viaScript)
    {
        Profile = profile;
        Folder = folder;
        CoreJar = coreJar;
        Arguments = arguments;
        this.process = process;
        ProcessId = process.Id;
        StartedAt = DateTime.UtcNow;

        // Запуск через run.bat — это cmd.exe, внутри которого уже своя java: по такому процессу
        // нагрузка считалась бы как ноль, поэтому график для него не строим.
        MeasuresJvmProcess = !viaScript;

        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) => Receive(e.Data, "stdout");
        process.ErrorDataReceived += (_, e) => Receive(e.Data, "stderr");
        process.Exited += (_, _) => RaiseFinished(ExitCode());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Если java умерла до подписки на события (битый jar, не хватило памяти) — Exited уже
        // не придёт, и процесс висел бы в состоянии «запущен» вечно.
        if (process.HasExited) RaiseFinished(ExitCode());
    }

    public ServerProfile Profile { get; }
    public string Folder { get; }
    public string CoreJar { get; }
    public string Arguments { get; }

    // Идентификатор снимаем сразу: после завершения процесса он уже недоступен.
    public int ProcessId { get; private set; }

    // Момент старта — по нему панель решает, было ли завершение «сбойным циклом» или сервер
    // отработал нормально и упал уже по другой причине.
    public DateTime StartedAt { get; }

    // Просил ли человек этот процесс остановить. Именно по этому флагу, а не по коду возврата,
    // панель отличает штатную остановку от падения: «stop» даёт код 0, но и упавший на пустом
    // месте сервер иногда выходит с нулём, а кнопка «Остановить» обязана означать остановку.
    public bool StopRequested { get; private set; }

    // False, когда запускали не java напрямую, а скрипт run.bat (внутри него своя java) —
    // по такому процессу измерять нагрузку бессмысленно, и панель об этом говорит.
    public bool MeasuresJvmProcess { get; }

    // Строка от самой панели, а не от сервера: тоже попадает и в буфер, и в журнал на экране.
    public void Note(string text) => Append("[ForgePanel] " + text);

    // События приходят из потоков чтения процесса, то есть не из потока интерфейса:
    // подписчик обязан сам перейти в Dispatcher, иначе WPF запретит трогать элементы.
    // Номер строки нужен, чтобы экран не выводил дважды то, что уже попало в буфер до постройки страницы.
    public event Action<ServerInstance, long, string>? Line;
    public event Action<ServerInstance, int>? Finished;

    public long Written
    {
        get { lock (gate) return written; }
    }

    public ServerPhase Phase
    {
        get { lock (gate) return phase; }
    }

    public static ServerInstance Start(ServerProfile p, string folder, string javaPath, string coreJar, string? argsFile = null)
    {
        // nogui обязателен: без него сервер поднимает своё Swing-окно и пишет журнал в него,
        // а не в stdout — тогда в панели остались бы только ошибки запуска.
        // Кодировку задаём с обеих сторон. Без этого stdin идёт кодовой страницей консоли (.NET
        // берёт её по умолчанию), а JVM после JEP 400 читает UTF-8: «say Привет» доходило до сервера
        // мусором и возвращалось в журнал белыми ромбиками. sun.* — то же самое для Java 17,
        // где свойств stdout.encoding/stdin.encoding ещё нет (неизвестные -D аргументы JVM игнорирует).
        //
        // Forge и NeoForge 1.17+ идут другим путём: classpath лежит в файле аргументов, который
        // написал установщик, и мы скармливаем java именно его (запись «@путь» — тот же механизм,
        // что в run.bat). -Xss1M берём из скрипта установщика: загрузчику Forge нужен глубокий стек.
        bool viaScript = argsFile == null && ForgeInstaller.IsScript(coreJar);

        string arguments = viaScript
            ? $"/c \"{Path.GetFileName(coreJar)}\""
            : "-Xmx" + Math.Max(1, p.MemoryGb) + "G " +
              "-Dfile.encoding=UTF-8 " +
              "-Dstdout.encoding=UTF-8 -Dstderr.encoding=UTF-8 -Dstdin.encoding=UTF-8 " +
              "-Dsun.stdout.encoding=UTF-8 -Dsun.stderr.encoding=UTF-8 " +
              (argsFile == null
                  ? $"-jar \"{Path.GetFileName(coreJar)}\" nogui"
                  : $"-Xss1M @\"{argsFile}\" nogui");

        var startInfo = new ProcessStartInfo(viaScript ? "cmd.exe" : javaPath, arguments)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Без BOM: сервер спотыкается о первые байты потока, а в середине строки он не нужен.
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        Process process;

        if (viaScript)
        {
            // run.bat вызывает просто «java» — ту, которая стоит в системном PATH, а не ту, которую
            // панель подобрала под версию. Переставляем её в начало PATH дочернего процесса: так
            // скрипт находит нужный JDK, а сам файл запуска остаётся нетронутым.
            string? bin = Path.GetDirectoryName(javaPath);
            if (!string.IsNullOrWhiteSpace(bin))
                startInfo.EnvironmentVariables["PATH"] =
                    bin + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
        }

        try
        {
            process = Process.Start(startInfo) ?? throw new IOException("Java не стартовала: Process.Start вернул пустой ответ.");
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException($"Не удалось запустить «{Path.GetFileName(javaPath)}»: {ex.Message}", ex);
        }

        return new ServerInstance(p, folder, coreJar, process, arguments, viaScript);
    }

    public string Snapshot()
    {
        lock (gate) return buffer.ToString();
    }

    public void ClearLog()
    {
        lock (gate) buffer.Clear();
    }

    // Перенести в новый журнал хвост предыдущего процесса. Без этого авто-перезапуск стирает
    // причину падения ровно в тот момент, когда человек её ищет: на экране остаётся только
    // поднимающийся сервер. Строки идут как есть, без пометки панели, — это текст сервера.
    public void GraftTail(string text, int lines)
    {
        string[] all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int from = Math.Max(0, all.Length - Math.Max(1, lines));
        if (from >= all.Length) return;

        Append($"[ForgePanel] --- {all.Length - from} строк журнала до этого запуска ---");
        for (int i = from; i < all.Length; i++) Append(all[i].TrimEnd('\r'));
    }

    // Команда из окна консоли. Пустые строки серверу не нужны, а перевод строки он ждёт всегда.
    public void Send(string command)
    {
        string text = command.Trim();
        if (text.Length == 0) return;

        lock (gate)
        {
            if (phase != ServerPhase.Running)
                throw new InvalidOperationException("Сервер уже завершается и команд не примет.");
        }

        try
        {
            process.StandardInput.WriteLine(text);
            process.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            throw new IOException($"Канал ввода сервера закрыт: {ex.Message}", ex);
        }
    }

    // Штатная остановка — та же команда stop, что и в обычной консоли: сервер успевает сохранить мир.
    public void Stop()
    {
        lock (gate)
        {
            // Отмечаем до проверки фазы: вызов мог прийти и после того, как процесс уже умер,
            // но намерение человека при этом было именно «остановить», а не «перезапустить».
            StopRequested = true;
            if (phase != ServerPhase.Running) return;
            phase = ServerPhase.Stopping;
        }

        // Первым делом — save-all. Сам stop тоже сохраняет мир, но если сервер до своего сохранения
        // не доживёт (20 секунд вышли, процесс снят принудительно), на диске останется то состояние,
        // которое успел записать этот save-all. Команды сервер читает по порядку, поэтому stop
        // начнётся уже после записи.
        string save = SaveCommand(Profile.Version);
        Append($"[ForgePanel] Сначала «{save}» — мир ляжет на диск до того, как сервер начнёт закрываться.");
        if (!Write(save))
        {
            Kill();
            return;
        }

        Append("[ForgePanel] Отправляю серверу команду stop…");
        if (!Write("stop")) Kill();
    }

    // Слово «flush» у save-all старые ядра знали и раньше, но поручиться за него на любой сборке
    // нельзя, а лишнее аргумент-слово — это ошибка синтаксиса в журнале ровно там, где человек
    // читает, сохранился ли мир. Поэтому флаг даём только с 1.20.6, где описано его реальное
    // поведение («дождаться записи, а не поставить задачу в очередь»); остальным — голое save-all:
    // сохранение он запускает и без флага, а stop допишет всё сам.
    private static string SaveCommand(string? mcVersion) => AtLeast(mcVersion, 1, 20, 6) ? "save-all flush" : "save-all";

    private static bool AtLeast(string? version, int major, int minor, int patch)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var parts = version.Trim().Split('.', '-', ' ');

        int Read(int index) => parts.Length > index && int.TryParse(parts[index], out int n) ? n : -1;
        int first = Read(0);
        if (first < 0) return false;
        if (first != major) return first > major;

        int second = Read(1);
        if (second < 0) return false;
        if (second != minor) return second > minor;

        int third = Read(2);
        return third < 0 || third >= patch;
    }

    // Одна строка в stdin. Отдельно от Send: при остановке канал уже может быть закрыт, и это
    // не ошибка пользователя, а повод снять процесс — молча кидать исключение здесь нечем.
    private bool Write(string command)
    {
        try
        {
            process.StandardInput.WriteLine(command);
            process.StandardInput.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Append("[ForgePanel] Записать в stdin не вышло (" + ex.Message + "), снимаю процесс.");
            return false;
        }
    }

    // Счётчики процесса для графика нагрузки. Refresh() обязателен: без него .NET отдаёт значения,
    // снятые при предыдущем обновлении, и линия стояла бы на месте.
    // false — процесс уже нельзя опросить (завершился или снят), опрос больше не повторяем.
    public bool TrySampleLoad(out TimeSpan cpuTime, out long workingSetBytes)
    {
        cpuTime = TimeSpan.Zero;
        workingSetBytes = 0;
        try
        {
            process.Refresh();
            cpuTime = process.TotalProcessorTime;
            workingSetBytes = process.WorkingSet64;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    // Ожидание даётся с таймаутом: зависший на сохранении мира процесс убиваем только после него.
    public async Task<bool> WaitForShutdownAsync(int graceSeconds)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(graceSeconds));
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        finally
        {
            cts.Dispose();
        }
    }

    public void Kill()
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            Append("[ForgePanel] Процесс уже нельзя снять: " + ex.Message);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }

        try { process.Kill(entireProcessTree: true); } catch { /* процесс уже завершился */ }
        process.Dispose();
    }

    private int ExitCode()
    {
        try { return process.ExitCode; } catch { return -1; }
    }

    private void Receive(string? line, string channel)
    {
        if (line == null) return; // признак конца потока, а не содержимое
        Append(channel == "stderr" ? "[stderr] " + line : line);
    }

    private void RaiseFinished(int code)
    {
        lock (gate) phase = ServerPhase.Stopped;
        Append($"[ForgePanel] Процесс завершился, код возврата {code}.");
        Finished?.Invoke(this, code);
    }

    private void Append(string line)
    {
        long sequence;
        lock (gate)
        {
            if (buffer.Length > BufferLimit)
            {
                buffer.Remove(0, buffer.Length / 2);
                buffer.AppendLine("[ForgePanel] Начало журнала обрезано — он слишком длинный.");
            }

            buffer.AppendLine(line);
            sequence = ++written;
        }

        Line?.Invoke(this, sequence, line);
    }
}

// Какую Java требует сервер этой версии Minecraft. Проверка до запуска дешевле, чем
// UnsupportedClassVersionError в журнале: так выглядит запуск 1.21 на восьмой java —
// «class file version 65.0, this version only recognizes up to 52.0».
public static class JavaRequirement
{
    public static int MinMajor(string? mcVersion)
    {
        var (first, second, third) = Parse(mcVersion);
        if (first == 0) return 21;                        // номер не разобран — считаем, что сборка свежая
        // Календарная нумерация Mojang. 26.x меряли руками на реальном сервере: и
        // MinecraftServer.class из 26.2, и классы NeoForge под него имеют class file 69.0,
        // то есть Java 25. На 21 такой сервер не стартует в принципе.
        if (first >= 26) return 25;
        if (first > 1) return 21;                         // остальная календарная нумерация — как у 1.21
        if (second >= 21) return 21;
        if (second == 20) return third >= 5 ? 21 : 17;
        if (second >= 18) return 17;
        if (second == 17) return 16;
        return 8;
    }

    private static (int, int, int) Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (0, 0, 0);
        var parts = value.Trim().Split('.', '-', ' ');
        int Read(int index) => parts.Length > index && int.TryParse(parts[index], out int n) ? n : 0;
        return (Read(0), Read(1), Read(2));
    }
}

// Какую Java брать для сборки: свою, указанную в её настройках, или подобранную по версии
// Minecraft. Решение в одном месте принципиально — запуск, установщик Forge и «Проверить сборку»
// обязаны называть один и тот же файл: иначе проверка показывает ✓ на одну java, а стартует другая.
public sealed class JavaChoice
{
    private JavaChoice(JavaInstall? install, bool manual, int needed, string error, string requested)
    {
        Install = install;
        Manual = manual;
        Needed = needed;
        Error = error;
        Requested = requested;
    }

    public JavaInstall? Install { get; }

    // True — путь человек выбрал сам в настройках сборки; false — подобрала панель по версии Minecraft.
    public bool Manual { get; }

    // Какая масть нужна серверу этой версии — то, ради чего всё это и проверяется.
    public int Needed { get; }

    // Пусто, если всё в порядке. Иначе — готовый текст для человека: вызывающий код показывает
    // его как есть, чтобы формулировка ошибки не разъезжалась между запуском и проверкой.
    public string Error { get; }

    // Что было указано в настройках (пусто — «Автоподбор»).
    public string Requested { get; }

    public bool Ok => Install != null;

    public string Detail => Install == null
        ? Error
        : $"{Install.Version} · {Install.Path} · " + (Manual ? "выбрана для этой сборки" : "подобрана по версии Minecraft");

    public static JavaChoice For(ServerProfile p)
    {
        int needed = JavaRequirement.MinMajor(p.Version);
        string requested = (p.JavaPath ?? "").Trim();
        return requested.Length == 0 ? Auto(p, needed) : FromPath(p, requested, needed);
    }

    private static JavaChoice Auto(ServerProfile p, int needed)
    {
        var installs = SystemInfo.JavaInstalls();

        // Берём ближайшую к требованию, а не самую свежую: сервер 1.12 на Java 8 работает, а на
        // двадцать второй падает, хотя формально «>= 8» его устраивает. Старые Forge и Bukkit
        // умеют ровно ту Java, под которую собраны, поэтому «новее» тут не «лучше».
        JavaInstall? match = installs
            .Where(i => i.Major >= needed)
            .OrderBy(i => i.Major)
            .ThenBy(i => i.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (match != null) return new(match, false, needed, "", "");

        string found = installs.Count == 0
            ? "на этом компьютере Java не найдена вовсе"
            : "самая свежая из найденных — " + string.Join(", ", installs.Take(3).Select(i => $"{i.Version} ({i.Path})"));

        return new(null, false, needed,
            $"Серверу {p.Version} нужна Java {needed} или новее, а {found}.\n\n" +
            "Поставь подходящую JDK — например, с https://adoptium.net — и перезапусти панель: список " +
            "установленных Java она читает один раз за запуск. Без перезапуска тот же результат даёт " +
            "выбор в настройках сборки: «Java для этой сборки» → «Найти java.exe…».", "");
    }

    private static JavaChoice FromPath(ServerProfile p, string requested, int needed)
    {
        if (!File.Exists(requested))
            return new(null, true, needed,
                "В настройках сборки указана Java, которой нет на диске:\n" + requested +
                "\n\nПоставь её на это место или выбери другую в «Изменить» → «Java для этой сборки».", requested);

        JavaInstall? install = SystemInfo.Probe(requested);
        if (install == null)
            return new(null, true, needed,
                "Файл на месте, но «java -version» от него ничего не ответил:\n" + requested +
                "\n\nПохоже, это не исполняемая java или она битая. Выбери другой файл " +
                "или верни «Автоподбор».", requested);

        if (install.Major < needed)
            return new(null, true, needed,
                $"Серверу {p.Version} нужна Java {needed} или новее, а в настройках сборки выбрана " +
                $"{install.Version}:\n{install.Path}\n\n" +
                "Запуск на такой JDK кончается UnsupportedClassVersionError, поэтому панель даже не " +
                "начинает. Поменяй выбор в «Изменить» → «Java для этой сборки».", requested);

        return new(install, true, needed, "", requested);
    }
}

// server.properties: до первого запуска файла в папке нет, и порт из конфигурации никуда не
// девается сам. Правим точечно — только те ключи, которые заданы в конфигурации; остальные
// строки и комментарии файла остаются нетронутыми, включая правки, сделанные руками.
public static class ServerProperties
{
    public static string Ensure(ServerProfile p, string folder)
    {
        string file = Path.Combine(folder, "server.properties");
        bool creating = !File.Exists(file);
        var wanted = Wanted(p, creating);

        if (creating)
        {
            var created = new List<string>
            {
                "# Создан ForgePanel перед первым запуском. Остальные значения сервер дополнит сам.",
            };
            created.AddRange(wanted.Select(w => w.Key + "=" + w.Value));
            File.WriteAllLines(file, created, new UTF8Encoding(false));

            string players = p.MaxPlayers == null ? "по умолчанию" : p.MaxPlayers.Value.ToString();
            string drawn = p.ViewDistance == null ? "по умолчанию" : p.ViewDistance.Value.ToString();
            string diff = p.Difficulty.Length == 0 ? "по умолчанию" : p.Difficulty;
            string shown = wanted.FirstOrDefault(w => w.Key == "motd").Value;
            return $"server.properties создан: порт {p.Port}, в списке — «{shown}», мест {players}, " +
                   $"прорисовка {drawn}, сложность {diff}, онлайн-режим {OffOn(p.OnlineMode)}.";
        }

        var lines = File.ReadAllLines(file).ToList();
        var changed = new List<string>();

        foreach ((string key, string value) in wanted)
        {
            int index = lines.FindIndex(line => Key(line) == key);
            if (index < 0)
            {
                lines.Add(key + "=" + value);
                changed.Add($"{key} = {value} (ключа в файле не было)");
                continue;
            }

            string current = Value(lines[index]);
            if (current == value) continue;

            lines[index] = key + "=" + value;
            changed.Add($"{key}: было {current}, стало {value}");
        }

        if (changed.Count == 0) return "";

        File.WriteAllLines(file, lines, new UTF8Encoding(false));
        return "server.properties обновлён: " + string.Join("; ", changed) + ".";
    }

    // Ключи, заданные в конфигурации сборки. Пустые значения сюда не попадают — их править не нужно,
    // иначе запуск каждый раз возвращал бы к значениям лаунчера правки, сделанные руками в файле.
    // Исключение — создание файла: там motd по умолчанию берём из названия конфигурации.
    private static List<(string Key, string Value)> Wanted(ServerProfile p, bool creating)
    {
        var wanted = new List<(string Key, string Value)>
        {
            ("server-port", p.Port.ToString()),
        };

        string motd = Sanitize(p.Motd);
        if (motd.Length == 0 && creating) motd = Sanitize(p.Name);
        if (motd.Length > 0) wanted.Add(("motd", motd));

        if (p.MaxPlayers != null) wanted.Add(("max-players", p.MaxPlayers.Value.ToString()));
        if (p.OnlineMode != null) wanted.Add(("online-mode", p.OnlineMode.Value ? "true" : "false"));

        // Дистанцию прописываем только когда её задали явно: сервер сам выставляет это значение
        // при генерации server.properties, и возвращать его к «10» при каждом запуске незачем.
        if (p.ViewDistance != null) wanted.Add(("view-distance", p.ViewDistance.Value.ToString()));

        // Сложность — по тому же правилу: пустое поле значит «в файле стоит своя», и подменять её
        // значением по умолчанию при каждом запуске не за чем.
        if (p.Difficulty.Length > 0) wanted.Add(("difficulty", p.Difficulty));
        return wanted;
    }

    private static string OffOn(bool? value) => value == null ? "не задан" : (value.Value ? "включён" : "выключен");

    // Значение одного ключа из уже существующего файла — например level-name, чтобы понять,
    // как сервер называет папку текущего мира.
    public static string Read(string folder, string key, string fallback)
    {
        string file = Path.Combine(folder, "server.properties");
        if (!File.Exists(file)) return fallback;

        try
        {
            foreach (string line in File.ReadLines(file))
                if (Key(line) == key) { string value = Value(line); return value.Length > 0 ? value : fallback; }
        }
        catch
        {
            // Файл мог оказаться занятым или битым — на поведение лаунчера влиять это не должно.
        }

        return fallback;
    }

    // Папка текущего мира: сервер создаёт её по level-name, а датапаки ищет внутри неё.
    public static string WorldFolder(string folder) => Read(folder, "level-name", "world");

    // Одна правка в файле, который сервер уже создал сам (level-name после установки карты).
    // В отличие от Ensure, сюда не попадают значения из конфигурации сборки — только запрошенный ключ.
    public static void Set(string folder, string key, string value)
    {
        string file = Path.Combine(folder, "server.properties");
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();

        int index = lines.FindIndex(line => Key(line) == key);
        if (index < 0) lines.Add(key + "=" + value);
        else lines[index] = key + "=" + value;

        File.WriteAllLines(file, lines, new UTF8Encoding(false));
    }

    // Ключ строки «ключ=значение»; у комментария и пустой строки ключа нет — совпадения не будет.
    // Разбор строки свойств нужен и Bedrock-файлу (см. BedrockProperties): формат тот же, и
    // держать второй экземпляр парсера незачем.
    public static string Key(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#') return "";
        int separator = trimmed.IndexOf('=');
        return (separator < 0 ? trimmed : trimmed[..separator]).Trim();
    }

    public static string Value(string line)
    {
        int separator = line.IndexOf('=');
        return separator < 0 ? "" : line[(separator + 1)..].Trim();
    }

    // motd сервер читает одной строкой и не любит переводы строк и символ |, который он
    // использует как разделитель форматов.
    private static string Sanitize(string value) =>
        new string(value.Replace("|", " ").Replace("\r", " ").Replace("\n", " ").ToArray()).Trim();
}

// Иконка в списке серверов: сервер сам читает server-icon.png рядом со server.properties,
// картинка должна быть квадратным PNG 64×64. Лаунчер приводит любой выбранный файл к этому виду.
public static class ServerIcon
{
    private const string FileName = "server-icon.png";
    private const int Side = 64;

    // Возвращает текст для журнала консоли: что сделали или почему не получилось. Пустая строка —
    // иконку не просили либо свежая версия уже лежит в папке.
    public static string Apply(string folder, string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile)) return "";
        if (!File.Exists(sourceFile)) return $"Иконку не применили: файла {sourceFile} больше нет.";

        string target = Path.Combine(folder, FileName);
        // Копировали уже — исходник с тех пор не менялся. Перезапись каждый запуск трогала бы
        // файл, который сервер держит открытым при работе.
        if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(sourceFile))
            return "";

        try
        {
            BitmapSource source = Decode(sourceFile);
            int side = Math.Min(source.PixelWidth, source.PixelHeight);
            var square = new CroppedBitmap(source,
                new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
            var scaled = new TransformedBitmap(square, new ScaleTransform((double)Side / side, (double)Side / side));

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using (var output = File.Create(target)) encoder.Save(output);

            return $"Иконка: {Path.GetFileName(sourceFile)} {source.PixelWidth}×{source.PixelHeight} → " +
                   $"{FileName} {Side}×{Side}.";
        }
        catch (Exception ex)
        {
            return $"Иконку не применили: {ex.Message}";
        }
    }

    private static BitmapSource Decode(string file)
    {
        using var stream = File.OpenRead(file);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        if (image.PixelWidth < 1 || image.PixelHeight < 1) throw new InvalidDataException("Файл не похож на картинку.");
        return image;
    }
}
