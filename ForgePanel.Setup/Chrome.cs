using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ForgePanel.Setup
{
    static class Fmt
    {
        private static readonly CultureInfo Ru = new CultureInfo("ru-RU");

        public static string Bytes(long value)
        {
            if (value >= 1024L * 1024 * 1024) return (value / (1024.0 * 1024 * 1024)).ToString("0.0", Ru) + " ГБ";
            if (value >= 1024L * 1024) return (value / (1024.0 * 1024)).ToString("0.0", Ru) + " МБ";
            if (value >= 1024) return (value / 1024.0).ToString("0", Ru) + " КБ";
            return value + " Б";
        }

        public static string Percent(double fraction)
        {
            return Math.Round(fraction * 100).ToString("0", Ru) + " %";
        }
    }

    // Все виджеты мастера нарисованы кодом: разметка .xaml потянула бы за собой XAML-конвейер
    // и сборку .g.cs ради окна на четыре экрана.
    static class Chrome
    {
        public static readonly Thickness CardPadding = new Thickness(14, 12, 14, 12);

        public static TextBlock Title(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.Text,
                Margin = new Thickness(0, 0, 0, 6)
            };
        }

        public static TextBlock Body(string text, Brush color, double size, bool wrap)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = color,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
            };
        }

        public static TextBlock Muted(string text) => Body(text, Palette.Muted, 12.5, true);

        // Карточка-пояснение: Panel на 12/12 скруглении с рамкой Line — тот же приём, что в панели.
        public static Border Card(UIElement content)
        {
            return new Border
            {
                Background = Palette.Panel,
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = CardPadding,
                Margin = new Thickness(0, 0, 0, 10),
                Child = content
            };
        }

        public static Border AccentMark()
        {
            return new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(4),
                Background = Palette.Accent,
                BorderBrush = Palette.AccentLine,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
        }

        public static FlatButton IconButton(string glyph)
        {
            FlatButton button = new FlatButton(false)
            {
                Content = glyph,
                Width = 30,
                Height = 30,
                MinWidth = 0,
                Margin = new Thickness(0),
                FontSize = 15,
                Template = ButtonTemplate(),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = System.Windows.Media.Brushes.Transparent
            };
            return button;
        }

        public static FlatButton Button(string text, bool primary)
        {
            FlatButton button = new FlatButton(primary)
            {
                Content = text,
                MinWidth = 128,
                Height = 34,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(14, 0, 14, 0),
                FontSize = 13.5,
                Template = ButtonTemplate(),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            return button;
        }

        public static TextBox Field(string text)
        {
            return new TextBox
            {
                Text = text,
                Background = Palette.FieldBg,
                Foreground = Palette.Text,
                CaretBrush = Palette.Text,
                BorderBrush = Palette.Line,
                BorderThickness = new Thickness(1),
                FontSize = 13,
                Padding = new Thickness(8, 6, 8, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 32
            };
        }

        // Двухсостояние-чип вместо CheckBox: коробочка галочки в теме Aero на тёмном фоне
        // выглядит светлым пятном, а здесь выбор сразу читается по тексту.
        public static ToggleChip Chip(string label, bool on)
        {
            return new ToggleChip(label, on);
        }

        public static ProgressBarView Bar() => new ProgressBarView();

        private static ControlTemplate ButtonTemplate()
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetBinding(Border.BackgroundProperty, BackgroundBinding("Background"));
            border.SetBinding(Border.BorderBrushProperty, BackgroundBinding("BorderBrush"));

            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);

            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        private static Binding BackgroundBinding(string path)
        {
            return new Binding(path) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) };
        }
    }

    public sealed class FlatButton : Button
    {
        private readonly Brush _back, _hover, _pressed, _border, _fore;

        public FlatButton(bool primary, bool ghost = false)
        {
            _hover = new SolidColorBrush(Palette.Hex("#232935"));
            _pressed = Palette.Pressed;
            if (ghost)
            {
                _back = Brushes.Transparent;
                _border = Brushes.Transparent;
                _fore = Palette.Text;
            }
            else if (primary)
            {
                _back = Palette.Accent;
                _hover = new SolidColorBrush(Mix(Palette.Hex("#3B82F6"), Palette.Hex("#60A5FA")));
                _pressed = Palette.Pressed;
                _border = Palette.AccentLine;
                _fore = new SolidColorBrush(Palette.Hex("#FFFFFF"));
            }
            else
            {
                _back = Palette.Panel2;
                _border = Palette.Line;
                _fore = Palette.Text;
            }
            Background = _back;
            BorderBrush = _border;
            Foreground = _fore;

            IsEnabledChanged += (s, e) =>
            {
                Foreground = IsEnabled ? _fore : Palette.Muted;
                Background = IsEnabled ? _back : Palette.Panel2;
                Opacity = IsEnabled ? 1.0 : 0.7;
            };
        }

        private static Color Mix(Color a, Color b)
        {
            return Color.FromRgb((byte)((a.R + b.R) / 2), (byte)((a.G + b.G) / 2), (byte)((a.B + b.B) / 2));
        }

        protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            if (IsEnabled) Background = _hover;
        }

        protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            Background = IsEnabled ? _back : Palette.Panel2;
        }

        protected override void OnPreviewMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            if (IsEnabled) Background = _pressed;
        }

    protected override void OnPreviewMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (IsEnabled) Background = _hover;
    }
}

    // Чип «Да / Нет»: один клик, состояние видно по тексту и по цвету.
    public sealed class ToggleChip : Border
    {
        private readonly string _label;
        private bool _on;
        private TextBlock _state;

        public bool Value => _on;

        public ToggleChip(string label, bool on)
        {
            _label = label;
            _on = on;
            CornerRadius = new CornerRadius(9);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(10, 5, 10, 5);
            Cursor = System.Windows.Input.Cursors.Hand;
            Child = Build();
            Paint();
            MouseLeftButtonUp += (s, e) => { _on = !_on; _state.Text = _on ? "Да" : "Нет"; Paint(); };
        }

        private UIElement Build()
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = _label,
                FontSize = 12.5,
                Foreground = Palette.Text,
                VerticalAlignment = VerticalAlignment.Center
            });
            _state = new TextBlock
            {
                Text = _on ? "Да" : "Нет",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(_state);
            return row;
        }

        private void Paint()
        {
            Background = _on ? Palette.Pressed : Palette.Panel2;
            BorderBrush = _on ? Palette.AccentLine : Palette.Line;
            _state.Foreground = _on ? Palette.AccentText : Palette.Muted;
        }
    }

    // Grid с двумя «звёздами» вместо ProgressBar: готовый контролл из темы Aero рисует светлое
    // стекло, а тут цвет и скругление ровно из палитры мастера.
    public sealed class ProgressBarView : Border
    {
        private readonly ColumnDefinition _fill;
        private readonly ColumnDefinition _rest;

        public ProgressBarView()
        {
            Height = 10;
            CornerRadius = new CornerRadius(5);
            Background = Palette.Panel2;
            BorderBrush = Palette.Line;
            BorderThickness = new Thickness(1);

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(_fill = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(_rest = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border chunk = new Border
            {
                Background = Palette.Accent,
                CornerRadius = new CornerRadius(5),
                BorderBrush = Palette.AccentLine,
                BorderThickness = new Thickness(1)
            };
            Grid.SetColumn(chunk, 0);
            grid.Children.Add(chunk);
            Child = grid;
        }

        public void SetValue(double fraction)
        {
            if (double.IsNaN(fraction) || fraction < 0) fraction = 0;
            if (fraction > 1) fraction = 1;
            _fill.Width = new GridLength(Math.Max(fraction, 0.0001) * 1000, GridUnitType.Star);
            _rest.Width = new GridLength(Math.Max(1 - fraction, 0.0001) * 1000, GridUnitType.Star);
        }
    }
}
