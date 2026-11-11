using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace KotH.Client.Menu
{
    /// <summary>A small drop-down (no Avalonia ComboBox: its popup is a window of its own, which the overlays
    /// never draw). The head is a pill with the value and a caret; its list opens under it on a canvas the page
    /// lays over itself, and closes on a choice, a second click on the head, a click elsewhere or Escape.</summary>
    public sealed class MenuDropdown
    {
        readonly Canvas layer;
        readonly Func<IReadOnlyList<string>> items;
        readonly Func<int> current;
        readonly Action<int> pick;
        readonly TextBlock label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        readonly StackPanel lines = new StackPanel { Spacing = 2 };
        readonly ScrollViewer scroll;
        readonly Border sheet;

        /// <summary>The pill to place among the page's filters.</summary>
        public Border Head { get; }

        /// <summary>items: the values' texts; current: the chosen one's index; pick: a line was chosen.</summary>
        public MenuDropdown(Canvas layer, Func<IReadOnlyList<string>> items, Func<int> current, Action<int> pick)
        {
            (this.layer, this.items, this.current, this.pick) = (layer, items, current, pick);
            var caret = new Path { Classes = { "Caret" }, Data = Geometry.Parse("M0,0 L12,0 L6,7 Z"), VerticalAlignment = VerticalAlignment.Center };
            var inside = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            inside.Children.Add(label);
            inside.Children.Add(caret);
            Head = new Border { Classes = { "Tab" }, Child = inside, Cursor = new Cursor(StandardCursorType.Hand) };
            Head.PointerPressed += (_, e) =>
            {
                if (IsOpen)
                    Close();
                else
                    Open();
                e.Handled = true;
            };
            scroll = new ScrollViewer
            {
                Content = lines, MaxHeight = 420, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, AllowAutoHide = false,
            };
            sheet = new Border { Classes = { "Drop" }, Child = scroll };
        }

        public bool IsOpen => sheet.Parent != null;

        /// <summary>The head's words; set marks a value other than "any".</summary>
        public void Show(string text, bool set)
        {
            label.Text = text;
            Head.Classes.Set("Chosen", set);
        }

        /// <summary>Whether a pointer press on this element belongs to the drop-down (head or open list).</summary>
        public bool Owns(Visual element)
        {
            for (Visual v = element; v != null; v = v.GetVisualParent())
                if (v == Head || v == sheet)
                    return true;
            return false;
        }

        public void Open()
        {
            layer.Children.Clear();
            lines.Children.Clear();
            IReadOnlyList<string> texts = items();
            int chosen = current();
            for (int i = 0; i < texts.Count; i++)
            {
                int index = i;
                var line = new Border { Classes = { "Pick" }, Child = new TextBlock { Text = texts[i] }, Cursor = new Cursor(StandardCursorType.Hand) };
                line.Classes.Set("Chosen", i == chosen);
                line.PointerPressed += (_, e) =>
                {
                    Close();
                    pick(index);
                    e.Handled = true;
                };
                lines.Children.Add(line);
            }
            Point at = Head.TranslatePoint(new Point(0, Head.Bounds.Height + 10), layer) ?? default;
            Canvas.SetLeft(sheet, at.X);
            Canvas.SetTop(sheet, at.Y);
            sheet.MinWidth = Math.Max(Head.Bounds.Width, 240);
            scroll.Offset = default;
            layer.Children.Add(sheet);
        }

        public void Close() => layer.Children.Remove(sheet);
    }
}
