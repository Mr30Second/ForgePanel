using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace ForgePanel.Setup
{
    public sealed class Wizard : ShellWindow
    {
        private readonly Payload _payload;
        private TextBox _dir;
        private ToggleChip _desktop;
        private ProgressBarView _bar;
        private TextBlock _currentFile;
        private TextBlock _volume;
        private volatile bool _canceled;
        private Thread _worker;
        private int _lastPaint;

        public Wizard(Payload payload) : base("ForgePanel", "мастер установки", 640, 330)
        {
            _payload = payload;
            Closing += GuardAgainstHalfInstall;
            Closed += (s, e) => _payload.Dispose();
            ShowWelcome();
        }

        private void GuardAgainstHalfInstall(object sender, CancelEventArgs e)
        {
            if (_worker == null || !_worker.IsAlive) return;
            if (Prompt.Confirm(this,
                    "Идёт распаковка. Если закрыть мастер сейчас, в папке останется недописанный набор файлов.\n\n" +
                    "Отменить установку и закрыть?", "Отменить", "Вернуться"))
            {
                _canceled = true;
            }
            else
            {
                e.Cancel = true;
            }
        }

        // ---------- шаг 1 ----------

        private void ShowWelcome()
        {
            string previous = FindExistingInstall();
            _dir = Chrome.Field(previous ?? Product.DefaultInstallDir);

            FlatButton browse = Chrome.Button("Обзор…", false);
            browse.MinWidth = 0;
            browse.Width = 100;
            browse.Height = 32;
            browse.Click += (s, e) => PickFolder();

            Grid folderRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_dir, 0);
            Grid.SetColumn(browse, 1);
            folderRow.Children.Add(_dir);
            folderRow.Children.Add(browse);

            StackPanel about = new StackPanel();
            about.Children.Add(new TextBlock
            {
                Text = previous == null
                    ? "Мастер скопирует " + Fmt.Bytes(_payload.TotalBytes) + " файлов в папку ниже и создаст ярлыки."
                    : "Здесь уже лежит ForgePanel — мастер обновит файлы программы. " +
                      "Сборки, миры и настройки живут в своей папке и отсюда не удаляются.",
                FontSize = 12.5,
                Foreground = Palette.Text,
                TextWrapping = TextWrapping.Wrap
            });
            about.Children.Add(new TextBlock
            {
                Text = Product.UserDataDir + " — папка данных панели. Мастер в неё не заглядывает.",
                FontSize = 12,
                Foreground = Palette.Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });

            _desktop = Chrome.Chip("Ярлык на рабочем столе", true);
            _desktop.HorizontalAlignment = HorizontalAlignment.Left;
            _desktop.Margin = new Thickness(0, 0, 0, 4);

            FlatButton cancel = Chrome.Button("Отмена", false);
            cancel.Click += (s, e) => Close();

            FlatButton go = Chrome.Button("Установить", true);
            go.IsDefault = true;
            go.Click += (s, e) => StartInstall();

            Show(Column(
                Chrome.Title("Установка ForgePanel " + Product.Version),
                Chrome.Muted("Панель для запуска Minecraft-серверов. Прав администратора не потребуется: " +
                             "всё ставится в вашу учётную запись."),
                Chrome.Body("Папка установки", Palette.AccentText, 12.5, false),
                folderRow,
                Chrome.Card(about),
                _desktop),
                cancel, go);
        }

        private string FindExistingInstall()
        {
            string fromRegistry = RegistryStore.ReadInstallLocation();
            if (fromRegistry != null && File.Exists(Path.Combine(fromRegistry, Product.Exe))) return fromRegistry;
            string byDefault = Product.DefaultInstallDir;
            return File.Exists(Path.Combine(byDefault, Product.Exe)) ? byDefault : null;
        }

        private void PickFolder()
        {
            using (Forms.FolderBrowserDialog dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Куда положить ForgePanel";
                dialog.ShowNewFolderButton = true;
                string current = _dir.Text;
                if (Directory.Exists(current)) dialog.SelectedPath = current;
                if (dialog.ShowDialog() == Forms.DialogResult.OK) _dir.Text = dialog.SelectedPath;
            }
        }

        private void StartInstall()
        {
            string target;
            try
            {
                target = ResolveTarget(_dir.Text);
            }
            catch (Exception ex)
            {
                Prompt.Notice(this, ex.Message);
                return;
            }

            if (Product.IsUserDataPath(target))
            {
                Prompt.Notice(this,
                    "Эта папка — хранилище сборок и настроек панели (" + Product.UserDataDir + ").\n\n" +
                    "Программу туда ставить нельзя: деинсталлятор никогда не удаляет папку с данными, " +
                    "поэтому файлы остались бы висеть. Выберите, например, " + Product.DefaultInstallDir + ".");
                return;
            }

            try
            {
                Installer.EnsureTargetNotRunning(target);
            }
            catch (Exception ex)
            {
                Prompt.Notice(this, ex.Message);
                return;
            }

            _installDir = target;
            _wantsDesktopShortcut = _desktop.Value;
            ShowProgress();
            StartExtraction();
        }

        private static string ResolveTarget(string text)
        {
            string raw = (text ?? "").Trim().Trim('"');
            if (raw.Length == 0) throw new InvalidOperationException("Укажите папку, куда ставить панель.");

            string full = Product.NormalizePath(Path.GetFullPath(raw))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length == 0) throw new InvalidOperationException("Не получился нормальный путь: " + raw);
            if (File.Exists(full)) throw new InvalidOperationException("Это файл, а не папка: " + full);
            if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Нельзя ставить программу в корень диска: " + full);
            return full;
        }

        // ---------- шаг 2 ----------

        private string _installDir;
        private bool _wantsDesktopShortcut;

        private void ShowProgress()
        {
            _bar = Chrome.Bar();
            _bar.Margin = new Thickness(0, 4, 0, 6);
            _volume = Chrome.Body("0 %", Palette.Text, 13, false);
            _currentFile = Chrome.Body("подготовка…", Palette.Muted, 12, false);
            _currentFile.TextTrimming = TextTrimming.CharacterEllipsis;
            _currentFile.Margin = new Thickness(0, 0, 0, 10);

            FlatButton cancel = Chrome.Button("Отменить", false);
            cancel.Click += (s, e) =>
            {
                _canceled = true;
                cancel.IsEnabled = false;
                cancel.Content = "отмена…";
            };

            Show(Column(
                Chrome.Title("Распаковка"),
                _volume,
                _bar,
                _currentFile,
                Chrome.Muted("Панель ещё не запущена, поэтому старые сборки во время копирования не меняются.")),
                cancel);
        }

        private void StartExtraction()
        {
            string target = _installDir;
            _worker = new Thread(() =>
            {
                try
                {
                    Directory.CreateDirectory(target);
                    _payload.ExtractTo(target, ReportChunk, () => _canceled);
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() => ShowFailure(ex)));
                    return;
                }
                Dispatcher.BeginInvoke(new Action(FinishInstall));
            });
            _worker.IsBackground = true;
            _worker.Start();
        }

        private void ReportChunk(string name, long done, long total)
        {
            // Куски по 64 КБ: на 173 МБ это ~2700 событий, столько вызовов в Dispatcher
            // тормозят окно сильнее, чем сама распаковка.
            int now = Environment.TickCount;
            if (name != null && now - _lastPaint < 80) return;
            _lastPaint = now;
            Dispatcher.BeginInvoke(new Action(() => PaintProgress(name, done, total)));
        }

        private void PaintProgress(string name, long done, long total)
        {
            if (_bar == null) return;
            double fraction = total <= 0 ? 1 : (double)done / total;
            _bar.SetValue(fraction);
            _volume.Text = Fmt.Bytes(done) + " из " + Fmt.Bytes(total) + " · " + Fmt.Percent(fraction);
            if (name != null) _currentFile.Text = name;
        }

        // Ярлыки и реестр — уже в UI-потоке: WScript.Shell из фонового потока создавать
        // COM-объект в Apartment-классе неудобно, а эти три вызова длятся доли секунды.
        private void FinishInstall()
        {
            try
            {
                _payload.WriteStubTo(Path.Combine(_installDir, Product.UninstallerExe));
                Installer.CreateShortcuts(_installDir, _wantsDesktopShortcut);
                RegistryStore.Register(_installDir);
                ShowDone();
            }
            catch (Exception ex)
            {
                ShowFailure(ex);
            }
        }

        private void ShowFailure(Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                _worker = null;
                Prompt.Notice(this,
                    "Установка отменена. Часть файлов осталась в " + _installDir + ".\n\n" +
                    "Можно повторить — мастер перезапишет то, что уже скопировал.");
                _canceled = false;
                ShowWelcome();
                return;
            }

            _worker = null;
            Prompt.Notice(this, "Не вышло: " + ex.Message);
            ShowWelcome();
        }

        // ---------- шаг 3 ----------

        private void ShowDone()
        {
            StackPanel lines = new StackPanel();
            lines.Children.Add(Line("Программа", _installDir));
            lines.Children.Add(Line("Ярлык в меню «Пуск»", Product.StartMenuDir));
            if (_wantsDesktopShortcut) lines.Children.Add(Line("Рабочий стол", Path.Combine(Product.DesktopDir, Product.Name + ".lnk")));
            lines.Children.Add(Line("Запись в списке программ", "Программа и установленные обновления → ForgePanel"));

            ToggleChip launch = Chrome.Chip("Запустить ForgePanel", true);
            launch.HorizontalAlignment = HorizontalAlignment.Left;
            launch.Margin = new Thickness(0, 8, 0, 0);

            FlatButton finish = Chrome.Button("Готово", true);
            finish.IsDefault = true;
            finish.Click += (s, e) =>
            {
                if (launch.Value) StartPanel(_installDir);
                Close();
            };

            Show(Column(
                Chrome.Title("Готово"),
                Chrome.Muted("ForgePanel " + Product.Version + " установлен. Дальше панель сама помнит окно, " +
                             "сборки и настройки."),
                Chrome.Card(lines),
                launch),
                finish);
        }

        private static UIElement Line(string label, string value)
        {
            StackPanel row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = Palette.Muted
            });
            row.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 12.5,
                Foreground = Palette.Text,
                TextWrapping = TextWrapping.Wrap
            });
            return row;
        }

        private void StartPanel(string installDir)
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(Path.Combine(installDir, Product.Exe))
                {
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                };
                Process.Start(start);
            }
            catch (Exception ex)
            {
                Prompt.Notice(this, "Панель установлена, но запустить её сразу не вышло: " + ex.Message +
                                    "\n\nОткройте ярлык ForgePanel вручную.");
            }
        }
    }
}
