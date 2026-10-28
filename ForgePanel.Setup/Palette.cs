using System.Windows.Media;

namespace ForgePanel.Setup
{
    // Значения переписаны из Theme.cs панели (нейтральные + акцент «синий», который там стоит
    // по умолчанию), чтобы мастер выглядел как часть того же приложения.
    // В панели числа в разметке запрещены, потому что там акцент переключается на лету;
    // у мастера одна тема и один акцент, поэтому кисти здесь заморожены и дальше только читаются.
    static class Palette
    {
        public static readonly Brush Bg = Frozen("#0B0D12");
        public static readonly Brush Panel = Frozen("#14171E");
        public static readonly Brush Panel2 = Frozen("#1B1F29");
        public static readonly Brush Line = Frozen("#2A2F3A");
        public static readonly Brush Muted = Frozen("#8290A8");
        public static readonly Brush Text = Frozen("#F4F5FA");
        public static readonly Brush FieldBg = Frozen("#101319");
        public static readonly Brush Accent = Frozen("#3B82F6");
        public static readonly Brush AccentLine = Frozen("#2B6CB8");
        public static readonly Brush AccentText = Frozen("#93C5FD");
        public static readonly Brush Pressed = Frozen("#223A5C");
        public static readonly Brush Ok = Frozen("#4ADE80");
        public static readonly Brush Warn = Frozen("#FBBF24");

        public static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);

        private static Brush Frozen(string value)
        {
            SolidColorBrush brush = new SolidColorBrush(Hex(value));
            brush.Freeze();
            return brush;
        }
    }
}
