using Avalonia.Controls;

namespace KotH.Client.Hud
{
    /// <summary>The round's XP card in the parts XpCard takes out and shows as overlays of their own.
    /// Setters change a control only when its value changed, so an equal value renders nothing.</summary>
    public partial class XpView : UserControl
    {
        public const int Lines = 3;

        readonly TextBlock[] labels, details, values;

        public XpView()
        {
            InitializeComponent();
            labels = new[] { Label0, Label1, Label2 };
            details = new[] { Detail0, Detail1, Detail2 };
            values = new[] { Value0, Value1, Value2 };
        }

        /// <summary>The parts, out of this control, each to be the content of an overlay.</summary>
        public void TakeParts() => Parts.Children.Clear();

        public void Line(int i, string label, string detail, string value)
        {
            Set(labels[i], label);
            Set(details[i], detail);
            Set(values[i], value);
        }

        public void Sum(string total) => Set(Total, total);

        public void Level(string level) => Set(Badge, level);

        public void Progress(string numbers) => Set(Numbers, numbers);

        public void Unlocked(string what) => Set(Unlock, what);

        static void Set(TextBlock block, string text)
        {
            if (block.Text != text)
                block.Text = text;
        }
    }
}
