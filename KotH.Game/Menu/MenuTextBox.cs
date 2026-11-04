using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Stride.Input;

namespace KotH.Client.Menu
{
    /// <summary>A one-line text field for the overlays, which get no keyboard from Avalonia: clicked, it
    /// takes the focus, and the menu that shows it hands over Stride's text and keys (<see cref="Capture"/>).
    /// Ctrl+V pastes (SDL's clipboard); password fields show dots. One field has the focus at a time.</summary>
    public sealed class MenuTextBox : Border
    {
        static MenuTextBox focused;
        static bool typing;

        readonly TextBlock shown = new TextBlock
        {
            FontSize = 22, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        string text = "";

        /// <summary>The text changed by typing or pasting.</summary>
        public event Action<string> TextChanged;

        /// <summary>Enter was pressed in it.</summary>
        public event Action Submitted;

        public int MaxLength { get; set; } = 32;
        public bool Password { get; set; }
        public string Placeholder { get; set; } = "";

        public MenuTextBox()
        {
            Height = 50;
            CornerRadius = new CornerRadius(25);
            Padding = new Thickness(22, 0);
            Background = new SolidColorBrush(Color.Parse("#FBF5EC"));
            BorderBrush = new SolidColorBrush(Color.Parse("#D3BEA1"));
            BorderThickness = new Thickness(2);
            Cursor = new Cursor(StandardCursorType.Ibeam);
            Child = shown;
            PointerPressed += (_, _) => Focus(this);
            Redraw();
        }

        public string Text
        {
            get => text;
            set
            {
                text = value ?? "";
                Redraw();
            }
        }

        /// <summary>Whether this field takes the keys now.</summary>
        public bool HasFocus => focused == this;

        public static void Focus(MenuTextBox box)
        {
            MenuTextBox was = focused;
            focused = box;
            was?.Redraw();
            box?.Redraw();
        }

        /// <summary>Once per frame by the menu that shows fields: the focused one takes the typed text,
        /// Backspace, Ctrl+V, Enter (submits) and Escape (lets go). True while a field has the keys, so
        /// the menu leaves Escape alone.</summary>
        public static bool Capture(InputManager input)
        {
            if (focused != null && !focused.IsEffectivelyVisible)
                Focus(null);
            if (typing != (focused != null))
            {
                typing = focused != null;
                if (typing)
                    input.TextInput?.EnabledTextInput();
                else
                    input.TextInput?.DisableTextInput();
            }
            if (focused == null)
                return false;
            MenuTextBox box = focused;
            foreach (InputEvent e in input.Events)
                if (e is TextInputEvent { Type: TextInputEventType.Input } t && !string.IsNullOrEmpty(t.Text))
                    box.Insert(t.Text);
            bool control = input.IsKeyDown(Keys.LeftCtrl) || input.IsKeyDown(Keys.RightCtrl);
            if (control && input.IsKeyPressed(Keys.V))
                box.Insert(Clipboard.Text());
            if (input.IsKeyPressed(Keys.Back) && box.text.Length > 0)
                box.Set(box.text[..^1]);
            if (input.IsKeyPressed(Keys.Enter) || input.IsKeyPressed(Keys.NumPadEnter))
            {
                Focus(null);
                box.Submitted?.Invoke();
            }
            else if (input.IsKeyPressed(Keys.Escape))
                Focus(null);
            return true;
        }

        void Insert(string typed)
        {
            if (string.IsNullOrEmpty(typed))
                return;
            var kept = new System.Text.StringBuilder(text);
            foreach (char c in typed)
                if (!char.IsControl(c) && kept.Length < MaxLength)
                    kept.Append(c);
            Set(kept.ToString());
        }

        void Set(string value)
        {
            if (value == text)
                return;
            text = value;
            Redraw();
            TextChanged?.Invoke(text);
        }

        void Redraw()
        {
            bool empty = text.Length == 0;
            string body = Password ? new string('•', text.Length) : text;
            shown.Text = HasFocus ? body + "|" : empty ? Placeholder : body;
            shown.Foreground = new SolidColorBrush(Color.Parse(empty && !HasFocus ? "#A08A70" : "#5A3A22"));
            BorderBrush = new SolidColorBrush(Color.Parse(HasFocus ? "#C8231F" : "#D3BEA1"));
        }

        /// <summary>SDL's clipboard, the window's own (Stride runs on SDL).</summary>
        static class Clipboard
        {
            static readonly bool OnWindows = OperatingSystem.IsWindows();

            public static string Text()
            {
                try
                {
                    nint p = OnWindows ? Windows.SDL_GetClipboardText() : Linux.SDL_GetClipboardText();
                    if (p == 0)
                        return "";
                    string s = Marshal.PtrToStringUTF8(p) ?? "";
                    if (OnWindows) Windows.SDL_free(p); else Linux.SDL_free(p);
                    return s.Trim();
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                {
                    return "";
                }
            }

            public static void Set(string value)
            {
                try
                {
                    byte[] z = System.Text.Encoding.UTF8.GetBytes((value ?? "") + "\0");
                    unsafe
                    {
                        fixed (byte* p = z)
                            if (OnWindows) Windows.SDL_SetClipboardText(p); else Linux.SDL_SetClipboardText(p);
                    }
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                {
                }
            }

            static class Linux
            {
                const string Lib = "libSDL2-2.0.so";
                [DllImport(Lib)] public static extern nint SDL_GetClipboardText();
                [DllImport(Lib)] public static extern unsafe int SDL_SetClipboardText(byte* text);
                [DllImport(Lib)] public static extern void SDL_free(nint p);
            }

            static class Windows
            {
                const string Lib = "SDL2";
                [DllImport(Lib)] public static extern nint SDL_GetClipboardText();
                [DllImport(Lib)] public static extern unsafe int SDL_SetClipboardText(byte* text);
                [DllImport(Lib)] public static extern void SDL_free(nint p);
            }
        }

        /// <summary>Puts text on the clipboard (the invite code's Copy).</summary>
        public static void Copy(string value) => Clipboard.Set(value);
    }
}
