using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ForgePanel;

public partial class App : Application
{
    // На машинах, где у WPF не ладит аппаратный рендер (кривый драйвер GPU, виртуалка, удалённый
    // рабочий стол), получается вот такой артефакт: целые панели заливаются чистым жёлтым,
    // пурпурным или циан, а текст поверх них остаётся читаемым. Цвета эти не из нашей палитры —
    // это испорченная поверхность композиции, а не ошибка в стилях. Программный рендер убирает
    // их полностью; для интерфейса со списками и полями цена — доли процента процессора.
    // Ставится до base.OnStartup, то есть до создания главного окна из StartupUri.
    protected override void OnStartup(StartupEventArgs e)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // Падение панели — это не только испорченный вечер: вместе с ней человек теряет окно
        // сервера, который он уже не может ни остановить, ни прочитать. Три точки сбора закрывают
        // всё, что может упасть: интерфейс, фоновые задачи и потоки вне WPF.
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "интерфейс");
            args.Handled = true;
            ReportCrash(args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write(args.ExceptionObject as Exception, "поток приложения");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "фоновая задача");

            // Без этого исключения всплывёт на финализации сборщика мусора и убьёт процесс
            // в самый непредсказуемый момент.
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    // Оповещение — тоже код, который исполняется во время аварии, поэтому оно в своём try:
    // неудачное сообщение о падении не должно превращаться во второе падение, уже без журнала.
    private static void ReportCrash(Exception error)
    {
        try
        {
            Dialog.Info(
                $"Панель споткнулась: {error.Message}\n\nЗаписал это в {PanelPaths.CrashLogFile} — файл " +
                "можно показать тому, кто чинит панель. Работать можно дальше, но после аварии панель " +
                "лучше закрыть и открыть заново: серверы при этом останутся живыми, а вот состояние " +
                "кнопок и графиков — уже нет.",
                "Сбой панели");
        }
        catch
        {
        }
    }

    // Одиночная строка ввода не должна ездить от колеса мыши или горизонтального свайпа тачпада.
    // PART_ContentHost живёт в ScrollViewer с Visibility=Hidden: полоса не видна, но прокрутка
    // работает, и начало вводного текста уползало за край поля — оставалась «половина буквы».
    // Каретку при наборе WPF по-прежнему выводит в видимую зону сам TextBoxView, это не колесо.
    private void TextBoxField_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => e.Handled = true;
}

// У TextBox в WPF нет встроенного placeholder, поэтому подсказка рисуется отдельным слоем внутри
// шаблона (стиль TextBox в App.xaml). Свойство attached: его задают и разметка (GlobalSearch),
// и код, потому что большинство полей форм создаются программно.
// Подсказка видна только пока поле пустое — с первым символом её перекрывает введённый текст.
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Placeholder), new PropertyMetadata(""));

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);
}
