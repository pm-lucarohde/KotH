using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KotH.Client.Settings;
using Stride.Input;

namespace KotH.Client.Menu
{
    /// <summary>One line of the Settings page: a name on the left, its control on the right. It writes
    /// into the settings the page edits (<see cref="Edited"/>) and shows them again in <see cref="Show"/>;
    /// a reason from <see cref="Locked"/> greys it out (DLSS off, not available here).</summary>
    public abstract class SettingRow
    {
        public Control View { get; private set; }

        /// <summary>The settings being edited, and what to do after a change.</summary>
        public Func<PlayerSettings> Edited;
        public Action Changed;

        /// <summary>Why it cannot be changed now, null when it can.</summary>
        public Func<PlayerSettings, string> Locked;

        protected TextBlock Name { get; }

        protected SettingRow(string name, string note)
        {
            Name = new TextBlock { Classes = { "Label" }, Text = name };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(Name);
            if (note != null)
                left.Children.Add(new TextBlock { Classes = { "Hint" }, Text = note, VerticalAlignment = VerticalAlignment.Center });
            Grid line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 60 };
            line.Children.Add(left);
            View = line;
        }

        protected void Place(Control control)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            ((Grid)View).Children.Add(control);
        }

        public void Show(PlayerSettings s)
        {
            string reason = Locked?.Invoke(s);
            View.IsEnabled = reason == null;
            View.Opacity = reason == null ? 1.0 : 0.5;
            Fill(s, reason);
        }

        protected abstract void Fill(PlayerSettings s, string reason);

        protected void Change(Action<PlayerSettings> set)
        {
            set(Edited());
            Changed?.Invoke();
        }
    }

    /// <summary>A heading between groups of rows.</summary>
    public sealed class HeadingRow : SettingRow
    {
        public HeadingRow(string name, string note = null) : base(name, note)
        {
            Name.Classes.Set("Label", false);
            Name.Classes.Add("Heading");
            ((Grid)View).Height = 44;
        }

        protected override void Fill(PlayerSettings s, string reason) { }
    }

    /// <summary>A slider with its value written beside it. Inside it counts whole steps from 0, as the
    /// volumes do: with a minimum above 0, Avalonia's track clamped the value to its own range when its
    /// template came and wrote that back (FOV showed 60).</summary>
    public sealed class SliderRow : SettingRow
    {
        readonly Slider slider;
        readonly TextBlock value = new TextBlock { Classes = { "Value" }, Width = 92, TextAlignment = TextAlignment.Right };
        readonly Func<PlayerSettings, double> get;
        readonly Func<double, string> text;
        readonly double min, step;
        bool filling;

        public SliderRow(string name, double min, double max, double step, Func<PlayerSettings, double> get,
                         Action<PlayerSettings, double> set, Func<double, string> text, string note = null) : base(name, note)
        {
            (this.get, this.text, this.min, this.step) = (get, text, min, step);
            slider = new Slider
            {
                Minimum = 0, Maximum = Math.Round((max - min) / step), Width = 290, TickFrequency = 1, IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            slider.ValueChanged += (_, e) =>
            {
                double v = Real(e.NewValue);
                value.Text = text(v);
                if (!filling && Edited != null && Math.Abs(v - get(Edited())) > step * 0.5)
                    Change(s => set(s, v));
            };
            var box = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            box.Children.Add(slider);
            box.Children.Add(value);
            Place(box);
        }

        double Real(double steps) => min + Math.Round(steps) * step;

        protected override void Fill(PlayerSettings s, string reason)
        {
            filling = true;
            slider.Value = Math.Round((get(s) - min) / step);
            filling = false;
            value.Text = text(get(s));
        }
    }

    /// <summary>A choice among a few values, stepped with ‹ and › (no ComboBox: its popup is a window of
    /// its own, which the overlays never draw; longer lists use MenuDropdown). Wraps round at both ends.</summary>
    public sealed class ChoiceRow<T> : SettingRow
    {
        readonly TextBlock value = new TextBlock { Classes = { "Value" }, Width = 250, TextAlignment = TextAlignment.Center,
                                                   TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Func<PlayerSettings, IReadOnlyList<T>> values;
        readonly Func<PlayerSettings, T> get;
        readonly Action<PlayerSettings, T> set;
        readonly Func<T, string> text;

        public ChoiceRow(string name, Func<PlayerSettings, IReadOnlyList<T>> values, Func<PlayerSettings, T> get, Action<PlayerSettings, T> set,
                         Func<T, string> text, string note = null) : base(name, note)
        {
            (this.values, this.get, this.set, this.text) = (values, get, set, text);
            var box = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            box.Children.Add(Arrow("‹", -1));
            box.Children.Add(value);
            box.Children.Add(Arrow("›", +1));
            Place(box);
        }

        Button Arrow(string glyph, int step)
        {
            var button = new Button { Classes = { "Step" }, Content = glyph };
            button.Click += (_, _) => Step(step);
            return button;
        }

        void Step(int step)
        {
            IReadOnlyList<T> list = values(Edited());
            if (list.Count == 0)
                return;
            int at = IndexOf(list, get(Edited()));
            int next = at < 0 ? 0 : (at + step + list.Count) % list.Count;
            Change(s => set(s, list[next]));
        }

        static int IndexOf(IReadOnlyList<T> list, T item)
        {
            for (int i = 0; i < list.Count; i++)
                if (EqualityComparer<T>.Default.Equals(list[i], item))
                    return i;
            return -1;
        }

        protected override void Fill(PlayerSettings s, string reason) => value.Text = reason ?? text(get(s));
    }

    /// <summary>On or off.</summary>
    public sealed class ToggleRow : SettingRow
    {
        readonly ToggleSwitch toggle = new ToggleSwitch { OnContent = "", OffContent = "", Width = 110 };
        readonly Func<PlayerSettings, bool> get;
        bool filling;

        public ToggleRow(string name, Func<PlayerSettings, bool> get, Action<PlayerSettings, bool> set, string note = null) : base(name, note)
        {
            this.get = get;
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (!filling)
                    Change(s => set(s, toggle.IsChecked == true));
            };
            Place(toggle);
        }

        protected override void Fill(PlayerSettings s, string reason)
        {
            filling = true;
            toggle.IsChecked = get(s);
            filling = false;
        }
    }

    /// <summary>A key: clicked, it waits for the next key pressed (the page's owner hands it over).</summary>
    public sealed class KeyRow : SettingRow
    {
        readonly Button button = new Button { Classes = { "Menu", "Small" }, Width = 250 };
        readonly Binding binding;

        /// <summary>Clicked: this row wants the next key.</summary>
        public event Action<KeyRow> Listen;

        public KeyRow(string name, Binding binding, string note = null) : base(name, note)
        {
            this.binding = binding;
            button.Click += (_, _) => Listen?.Invoke(this);
            Place(button);
        }

        public Binding Binding => binding;

        /// <summary>Shows that it waits for a key.</summary>
        public void Waiting() => button.Content = "Press a key";

        public void Bind(Keys key) => Change(s => s.Bind(binding, key));

        protected override void Fill(PlayerSettings s, string reason) => button.Content = KeyText(s.Key(binding));

        /// <summary>A key as the page writes it: "Left Shift", "1", "Num 1", "—" for none.</summary>
        public static string KeyText(Keys key)
        {
            if (key == Keys.None)
                return "—";
            string name = key.ToString();
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
                return name[1..];
            if (name.StartsWith("NumPad"))
                return "Num " + name[6..];
            var spaced = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                    spaced.Append(' ');
                spaced.Append(name[i]);
            }
            return spaced.ToString();
        }
    }
}
