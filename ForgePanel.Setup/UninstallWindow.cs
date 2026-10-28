using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace ForgePanel.Setup
{
    public sealed class UninstallWindow : ShellWindow
    {
        private readonly string _installDir;
        private TextBlock _stage;

        public UninstallWindow(string installDir) : base("ForgePanel", "удаление", 600, 260)
        {
            _installDir = installDir;
            ShowConfirm();
        }

        private void ShowConfirm()
        {
            bool exists = Directory.Exists(_installDir);

            StackPanel going = new StackPanel();
            going.Children.Add(new TextBlock
            {
                Text = exists
                    ? "Папка программы: " + _installDir + "  (" + Fmt.Bytes(FolderSize(_installDir)) + ")"
                    : "Папка программы уже пуста: " + _installDir,
                FontSize = 12.5,
                Foreground = Palette.Text,
                TextWrapping = TextWrapping.Wrap
            });
            going.Children.Add(new TextBlock
            {
                Text = "Ярлыки в меню «Пуск» и на рабочем столе · запись в списке программ",
                FontSize = 12,
                Foreground = Palette.Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });

            StackPanel stays = new StackPanel();
            stays.Children.Add(new TextBlock
            {
                Text = "Не трогаем: " + Product.UserDataDir,
                FontSize = 12.5,
                Foreground = Palette.Ok,
                TextWrapping = TextWrapping.Wrap
            });
            stays.Children.Add(new TextBlock
            {
                Text = "Сборки, миры (saves), автобэкапы, настройки и ключ CurseForge остаются на месте — " +
                       "при следующем установке панель их подхватит. Освободить их можно только вручную.",
                FontSize = 12,
                Foreground = Palette.Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });

            FlatButton cancel = Chrome.Button("Отмена", false);
            // Enter на этом экране не должен удалять программу.
            cancel.IsDefault = true;
            cancel.Click += (s, e) => Close();

            FlatButton yes = Chrome.Button("Да, удалить", true);
            yes.Click += (s, e) => Remove();

            Show(Column(
                Chrome.Title("Удалить ForgePanel?"),
                Chrome.Muted("Удаляется только то, что мастер сам создал: файлы программы и ярлыки."),
                Chrome.Card(going),
                Chrome.Card(stays)),
                cancel, yes);
        }

        private void Remove()
        {
            FlatButton cancel = Chrome.Button("Закройте панель", false);
            cancel.Click += (s, e) => Close();

            _stage = Chrome.Body("удаляем…", Palette.Muted, 12.5, true);

            Show(Column(Chrome.Title("Удаление"), _stage), cancel);

            Thread worker = new Thread(() =>
            {
                try
                {
                    Installer.EnsureTargetNotRunning(_installDir);
                    bool cleared = Installer.DeleteProgramFolder(_installDir, Note);
                    Installer.RemoveShortcuts();
                    RegistryStore.Unregister();
                    Dispatcher.BeginInvoke(new Action(() => ShowResult(cleared)));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _stage.Text = ex.Message;
                        _stage.Foreground = Palette.Warn;
                    }));
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void Note(string path)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_stage != null) _stage.Text = Path.GetFileName(path);
            }));
        }

        private void ShowResult(bool cleared)
        {
            StackPanel card = new StackPanel();
            card.Children.Add(new TextBlock
            {
                Text = cleared
                    ? "Папка программы удалена: " + _installDir
                    : "Файлы программы остались: " + _installDir,
                FontSize = 12.5,
                Foreground = Palette.Text,
                TextWrapping = TextWrapping.Wrap
            });
            card.Children.Add(new TextBlock
            {
                Text = "Ваши сборки и настройки не тронуты: " + Product.UserDataDir,
                FontSize = 12,
                Foreground = Palette.Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });

            FlatButton close = Chrome.Button("Закрыть", true);
            close.IsDefault = true;
            close.Click += (s, e) => Close();

            Show(Column(Chrome.Title("Готово"), Chrome.Card(card)), close);
        }

        private static long FolderSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    total += new FileInfo(file).Length;
            }
            catch (Exception) { }
            return total;
        }
    }
}
