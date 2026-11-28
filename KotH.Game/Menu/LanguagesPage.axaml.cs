using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KotH.Client.Localization;

namespace KotH.Client.Menu
{
    /// <summary>The Languages page: lists all 17 supported languages in their native scripts (autonyms).
    /// Picking a language switches the entire UI live and persists the choice to settings.toml.</summary>
    public partial class LanguagesPage : UserControl
    {
        readonly List<(string Code, Border Chip, Path Check)> chips = new List<(string, Border, Path)>();

        public LanguagesPage()
        {
            InitializeComponent();
            var cream = new SolidColorBrush(Color.FromRgb(0xF6, 0xEC, 0xDE));
            foreach (LanguageInfo lang in LanguageInfo.All)
            {
                string code = lang.Code;
                Action pick = () => Select(code);
                var check = new Path
                {
                    Data = Geometry.Parse("M 2,10 L 8,16 L 19,4"),
                    Stroke = cream, StrokeThickness = 4.0,
                    StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                    VerticalAlignment = VerticalAlignment.Center, IsVisible = false,
                };
                var label = new TextBlock
                {
                    Text = lang.Autonym, FontSize = 23, VerticalAlignment = VerticalAlignment.Center,
                    FlowDirection = lang.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                };
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Children = { check, label },
                };
                var chip = new Border
                {
                    Classes = { "Tab" }, MinWidth = 266, Padding = new Thickness(18, 10, 20, 10),
                    Cursor = new Cursor(StandardCursorType.Hand), Child = row, Tag = pick,
                };
                chip.PointerPressed += (_, _) => pick();
                chips.Add((code, chip, check));
                Choices.Children.Add(chip);
            }
            Refresh();
        }

        /// <summary>Highlights the currently active language chip.</summary>
        public void Refresh()
        {
            string active = Loc.Current.Code;
            foreach ((string code, Border chip, Path check) in chips)
            {
                bool on = code == active;
                chip.Classes.Set("Chosen", on);
                check.IsVisible = on;
            }
        }

        void Select(string code)
        {
            Loc.Set(code, save: true);
            Refresh();
        }
    }
}
