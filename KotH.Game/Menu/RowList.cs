using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;

namespace KotH.Client.Menu
{
    /// <summary>A list in a well, a page of rows at a time (the overlays take no mouse wheel, so no
    /// scrolling): per row a name, a detail and a button. Used by the lobby list and the invites.</summary>
    public sealed class RowList : StackPanel
    {
        /// <summary>One row: its texts, the button's word and what it does (null: shown disabled).</summary>
        public readonly record struct Row(string Name, string Detail, string Action, Action Click);

        readonly int perPage;
        IReadOnlyList<Row> rows = Array.Empty<Row>();
        int page;

        public RowList(int perPage)
        {
            this.perPage = perPage;
            Spacing = 14;
            VerticalAlignment = VerticalAlignment.Top;
        }

        /// <summary>Whether there is more than one page, so the next one can be asked for.</summary>
        public bool Paged => rows.Count > perPage;

        /// <summary>The rows, from the first page or (keepPage) on the one shown if it is still there.</summary>
        public void Show(IReadOnlyList<Row> list, bool keepPage = false)
        {
            rows = list;
            if (!keepPage || page * perPage >= rows.Count)
                page = 0;
            Fill();
        }

        /// <summary>The next page, after the last the first again.</summary>
        public void Next()
        {
            if (!Paged)
                return;
            page = (page + 1) * perPage >= rows.Count ? 0 : page + 1;
            Fill();
        }

        void Fill()
        {
            Children.Clear();
            for (int i = page * perPage; i < Math.Min(rows.Count, (page + 1) * perPage); i++)
            {
                Row row = rows[i];
                var button = new Button
                {
                    Classes = { "Menu", "Small" }, Content = row.Action, Width = 170, Height = 48,
                    IsEnabled = row.Click != null,
                };
                if (row.Click != null)
                    button.Click += (_, _) => row.Click();
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                line.Children.Add(new TextBlock { Classes = { "Label" }, Text = row.Name, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
                var detail = new TextBlock { Classes = { "Hint" }, Text = row.Detail, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(16, 0, 18, 0) };
                Grid.SetColumn(detail, 1);
                Grid.SetColumn(button, 2);
                line.Children.Add(detail);
                line.Children.Add(button);
                Children.Add(new Border { Classes = { "Row" }, Child = line });
            }
        }
    }
}
