using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace KotH.Client.Menu
{
    /// <summary>A list in a well that scrolls with the wheel, as the Settings rows do: per row a name,
    /// a detail and a button. Used by the lobby list, the invites and the host's maps.</summary>
    public sealed class RowList : ScrollViewer
    {
        /// <summary>One row: its texts, the button's word and what it does (null: shown disabled).</summary>
        public readonly record struct Row(string Name, string Detail, string Action, Action Click);

        // Room round the rows inside the well: the buttons' pictures reach 24 px past them.
        readonly StackPanel rows = new StackPanel
        {
            Spacing = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(26, 10, 30, 14),
        };

        // The ScrollViewer's theme and template, which go by type.
        protected override Type StyleKeyOverride => typeof(ScrollViewer);

        public RowList()
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            AllowAutoHide = false;
            Content = rows;
        }

        /// <summary>The rows, from the top or (keepPlace) where the list was scrolled to.</summary>
        public void Show(IReadOnlyList<Row> list, bool keepPlace = false)
        {
            rows.Children.Clear();
            foreach (Row row in list)
            {
                var button = new Button
                {
                    Classes = { "Menu", "Small" }, Content = row.Action, Width = 170, Height = 48,
                    IsEnabled = row.Click != null,
                };
                if (row.Click != null)
                    button.Click += (_, _) => row.Click();
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                line.Children.Add(new TextBlock { Classes = { "Label" }, Text = row.Name, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
                var detail = new TextBlock { Classes = { "Hint" }, Text = row.Detail, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 18, 0) };
                Grid.SetColumn(detail, 1);
                Grid.SetColumn(button, 2);
                line.Children.Add(detail);
                line.Children.Add(button);
                rows.Children.Add(new Border { Classes = { "Row" }, Child = line });
            }
            if (!keepPlace)
                Offset = default;
        }
    }
}
