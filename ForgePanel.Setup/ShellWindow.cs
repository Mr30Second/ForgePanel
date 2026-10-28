using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ForgePanel.Setup
{
    // Окно мастера без системной рамки — как у самой панели: тёмная шапка, крестик, перетаскивание.
    public class ShellWindow : Window
    {
        private readonly Grid _body;
        private readonly StackPanel _footer;

        // minHeight — нижняя граница, а не фиксированная высота: мастер без reasons на
        // пол-экрана пустоты выглядит недоделанным, поэтому высота считается по содержимому.
        protected ShellWindow(string title, string subtitle, double width, double minHeight)
        {
            Title = title;
            Width = width;
            MinHeight = minHeight;
            SizeToContent = SizeToContent.Height;
            MaxHeight = Math.Max(minHeight, SystemParameters.WorkArea.Height - 60);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Palette.Bg;
            BorderBrush = Palette.Line;
            BorderThickness = new Thickness(1);
            Foreground = Palette.Text;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });

            UIElement header = BuildHeader(title, subtitle);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            _body = new Grid { Margin = new Thickness(22, 16, 22, 8) };
            // Длинные тексты (например, «Не вышло: …» с путём) не должны обрезаться за краем
            // экрана: Height подстраивается, а после MaxHeight содержимое прокручивается.
            ScrollViewer bodyScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _body
            };
            Grid.SetRow(bodyScroll, 1);
            root.Children.Add(bodyScroll);

            Border foot = new Border
            {
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(22, 0, 22, 0)
                }
            };
            _footer = (StackPanel)foot.Child;
            Grid.SetRow(foot, 2);
            root.Children.Add(foot);

            Content = root;
        }

        private UIElement BuildHeader(string title, string subtitle)
        {
            Grid grid = new Grid { Margin = new Thickness(22, 0, 14, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel mark = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            mark.Children.Add(Chrome.AccentMark());
            mark.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.Text,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(mark, 0);
            grid.Children.Add(mark);

            if (!string.IsNullOrEmpty(subtitle))
            {
                TextBlock hint = new TextBlock
                {
                    Text = subtitle,
                    FontSize = 12,
                    Foreground = Palette.Muted,
                    Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(hint, 1);
                grid.Children.Add(hint);
            }

            FlatButton close = Chrome.IconButton("×");
            close.Click += (s, e) => Close();
            Grid.SetColumn(close, 2);
            grid.Children.Add(close);

            Border bar = new Border
            {
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = grid
            };
            // DragMove бросает InvalidOperationException, если кнопка уже отпущена (двойной клик
            // по шапке) — окно без рамки не должно из-за этого падать.
            bar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                try { DragMove(); }
                catch (InvalidOperationException) { }
            };
            return bar;
        }

        protected void Show(UIElement content, params UIElement[] footerButtons)
        {
            _body.Children.Clear();
            _body.Children.Add(content);

            _footer.Children.Clear();
            foreach (UIElement button in footerButtons) _footer.Children.Add(button);
        }

        protected StackPanel Column(params UIElement[] children)
        {
            StackPanel panel = new StackPanel();
            foreach (UIElement child in children) panel.Children.Add(child);
            return panel;
        }
    }

    public sealed class PromptWindow : ShellWindow
    {
        public bool Accepted { get; private set; }

        public PromptWindow(string message, string yesText, string noText)
            : base("ForgePanel", "", 520, 190)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            TextBlock text = new TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = Palette.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };

            FlatButton yes = Chrome.Button(yesText, true);
            yes.Click += (s, e) => { Accepted = true; Close(); };

            if (string.IsNullOrEmpty(noText))
            {
                yes.IsDefault = true;
                yes.IsCancel = true;
                Show(Column(text), yes);
                return;
            }

            // Enter и Esc должны давать безопасный ответ: «да» здесь — это отменить установку
            // посреди распаковки или удалить программу, а случайное нажатие клавиши не имеет
            // права запускать такое.
            FlatButton no = Chrome.Button(noText, false);
            no.IsDefault = true;
            no.IsCancel = true;
            no.Click += (s, e) => { Accepted = false; Close(); };
            Show(Column(text), no, yes);
        }
    }

    static class Prompt
    {
        public static bool Confirm(Window owner, string message, string yesText, string noText)
        {
            return Run(owner, new PromptWindow(message, yesText, noText));
        }

        public static void Notice(Window owner, string message)
        {
            Run(owner, new PromptWindow(message, "Понятно", ""));
        }

        private static bool Run(Window owner, PromptWindow window)
        {
            if (owner == null || owner == window)
            {
                Application app = new Application();
                app.Run(window);
            }
            else
            {
                window.Owner = owner;
                window.ShowInTaskbar = false;
                window.ShowDialog();
            }
            return window.Accepted;
        }
    }
}
