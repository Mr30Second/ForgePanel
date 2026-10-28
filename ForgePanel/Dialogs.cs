using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ForgePanel;

// Родной MessageBox — окно Win32: его цветами приложение не управляет вовсе, и в тёмной панели
// белый диалог выглядит чужим. Здесь своя модалка на кистях из App.xaml — тот же фон, та же
// скруглённая кнопка, и акцент подхватывается сразу после смены темы на странице «Настройки».
public static class Dialog
{
    // Сообщение с одной кнопкой. title — короткая надпись над текстом («Готово», «Ошибка»).
    public static void Info(string text, string title = "ForgePanel") => Show(text, title, Ok, 0, 0);

    // Вопрос с «Да»/«Нет». Отказ — это и «Нет», и Esc, и закрытие окна: ответ false.
    public static bool Confirm(string text, string title) => Show(text, title, NoYes, 1, 0) == 1;

    // Выбор из нескольких ответов. Возвращает индекс нажатой кнопки, а -1 — если окно закрыли
    // крестиком, ничего не выбрав. По Enter срабатывает кнопка defaultValue, по Esc и
    // закрытию — cancelValue, поэтому в опасном вопросе безопасный ответ всегда назначаем
    // именно Enter: случайное нажатие не должно стирать файлы.
    public static int Choose(string text, string title, string[] labels, int defaultValue, int cancelValue) =>
        Show(text, title, labels, defaultValue, cancelValue);

    private static readonly string[] NoYes = { "Нет", "Да" };

    private static readonly string[] Ok = { "ОК" };

    private static int Show(string text, string title, string[] labels, int defaultValue, int cancelValue)
    {
        int answer = -1;
        // Владелец — то окно, которое сейчас в фокусе: сообщение может прийти и поверх окна
        // установки, и тогда модалка встанет по центру именно над ним, а не над панелью.
        Window? owner = null;
        if (Application.Current != null)
        {
            foreach (Window window in Application.Current.Windows)
            {
                if (!window.IsActive) continue;
                owner = window;
                break;
            }
            owner ??= Application.Current.MainWindow;
        }

        var win = new Window
        {
            Title = title,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Background = ResourceBrush("Bg"),
            Foreground = ResourceBrush("Text")
        };
        DarkCaption(win);

        var head = new StackPanel { Margin = new Thickness(22, 20, 22, 0) };
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResourceBrush("Accent"),
            TextWrapping = TextWrapping.Wrap
        });
        head.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = ResourceBrush("Text"),
            Margin = new Thickness(0, 10, 0, 20),
            TextWrapping = TextWrapping.Wrap
        });

        var buttons = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(14, 0, 22, 18)
        };

        for (int index = 0; index < labels.Length; index++)
        {
            int chosen = index;
            buttons.Children.Add(MakeButton(labels[index], () =>
            {
                answer = chosen;
                win.Close();
            }, accent: index == defaultValue, isCancel: index == cancelValue));
        }

        // Кнопки прижаты к низу и остаются на месте: текст сообщения об ошибке бывает длинным,
        // и без прокрутки окно выросло бы за пределы экрана, а вместе с ним и кнопки за его край.
        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer
        {
            Content = head,
            MaxHeight = 560,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });

        win.Content = root;
        win.ShowDialog();
        return answer;
    }

    // Отступ только слева: группа прижата вправо, поэтому справа лишнего воздуха не возникает,
    // а между «Нет» и «Да» остаётся те же 8 пикселей, что и в основном окне.
    private static Button MakeButton(string label, Action click, bool accent = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(13, 9, 13, 9),
            Background = ResourceBrush(accent ? "Accent" : "Panel2"),
            BorderBrush = ResourceBrush(accent ? "Accent" : "Line"),
            Foreground = ResourceBrush(accent ? "OnAccent" : "Text"),
            IsDefault = accent,
            IsCancel = isCancel
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static Brush ResourceBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    // Заголовок нативного окна раскрашивает сама Windows: без этого атрибута шапка остаётся
    // светлой, и тёмное содержимое кончается ровно под кнопкой «закрыть». Атрибут 20 — текущий,
    // 19 — то же самое для ранних сборок Windows 10.
    public static void DarkCaption(Window window)
    {
        void Apply(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            int on = 1;
            if (DwmSetWindowAttribute(handle, 20, ref on, 4) != 0)
                DwmSetWindowAttribute(handle, 19, ref on, 4);
        }

        if (window.IsLoaded) Apply(new WindowInteropHelper(window).Handle);
        else window.SourceInitialized += (_, _) => Apply(new WindowInteropHelper(window).Handle);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
