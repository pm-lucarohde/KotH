using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KotH.Client.Gamepad;
using Stride.Core.Mathematics;

namespace KotH.Client.Menu
{
    /// <summary>Gamepad navigation for Avalonia menu overlays without a drawn cursor: one control is always
    /// selected (.PadFocus), left stick and D-pad move between controls, A activates, right stick scrolls
    /// the active list, and LB/RB switch tabs in submenus. Reuses its lists so idle frames allocate nothing.</summary>
    public sealed class PadMenuNav
    {
        const float StickOn = 0.5f, RepeatAfter = 0.35f, RepeatEvery = 0.11f;
        const double ScrollSpeed = 700.0;
        static readonly bool Trace = Environment.GetEnvironmentVariable("KOTH_PAD_TRACE") == "1";

        readonly List<Control> items = new List<Control>(32);
        readonly Stack<Visual> walk = new Stack<Visual>(64);
        readonly Dictionary<Control, Control> remembered = new Dictionary<Control, Control>();

        Control scope, current;
        Border currentLine;
        ColorWheel editingWheel;
        int heldX, heldY;
        float heldTime;

        /// <summary>Whether the player is steering a ColorWheel's pick with the left stick.</summary>
        public bool EditingWheel => editingWheel != null;

        /// <summary>The currently selected control, or null when the mouse is in use.</summary>
        public Control Selected => current;

        /// <summary>One frame while Pad.Active: keeps a control selected in scope, moves on stick/D-pad,
        /// scrolls with the right stick, switches tabs on LB/RB, activates on A. Returns true if B was
        /// consumed (leaving wheel edit mode).</summary>
        public bool Update(Control activeScope, float seconds, bool buttons, Action<int> stepTab, Action<Control> onSelect = null)
        {
            if (activeScope == null)
            {
                Clear();
                return false;
            }
            if (activeScope != scope)
            {
                if (scope != null && current != null)
                    remembered[scope] = current;
                StopWheel();
                scope = activeScope;
                Control back = remembered.TryGetValue(scope, out Control prev) && IsSelectable(prev, scope) ? prev : null;
                Select(back ?? FirstIn(scope), onSelect);
                ReadDirection(out heldX, out heldY);
                heldTime = 0f;
                return false;
            }
            if (current == null || !IsSelectable(current, scope))
                Select(FirstIn(scope), onSelect);

            if (editingWheel != null)
            {
                if (!IsSelectable(editingWheel, scope))
                    StopWheel();
                else
                {
                    if (Pad.Left != Vector2.Zero)
                        editingWheel.Nudge(Pad.Left.X, Pad.Left.Y, seconds);
                    if (buttons && (Pad.Pressed(PadButton.A) || Pad.Pressed(PadButton.B)))
                    {
                        bool back = Pad.Pressed(PadButton.B);
                        StopWheel();
                        return back;
                    }
                    return false;
                }
            }

            // Right stick scrolls the active page's (or open dropdown's) scroll viewer; focus follows when
            // the current item scrolls out of view or the scroll hits the bottom/top edge.
            if (MathF.Abs(Pad.Right.Y) > 0f && FindScroll(scope) is { } sv && sv.ScrollBarMaximum.Y > 0.0)
            {
                bool down = Pad.Right.Y < 0f;
                double y = Math.Clamp(sv.Offset.Y - Pad.Right.Y * ScrollSpeed * seconds, 0.0, sv.ScrollBarMaximum.Y);
                sv.Offset = new Avalonia.Vector(sv.Offset.X, y);
                bool atEdge = down ? y >= sv.ScrollBarMaximum.Y - 2.0 : y <= 2.0;
                if (current != null && OwnerScroll(current) == sv && (!InViewport(current, sv) || atEdge))
                    FollowScroll(sv, down, onSelect);
            }

            if (!buttons)
            {
                heldX = heldY = 0;
                heldTime = 0f;
                return false;
            }

            if (stepTab != null)
            {
                int tabStep = (Pad.Pressed(PadButton.RightShoulder) ? 1 : 0) - (Pad.Pressed(PadButton.LeftShoulder) ? 1 : 0);
                if (tabStep != 0)
                {
                    stepTab(tabStep);
                    // Wait one layout pass so newly added tab rows have bounds before focusing the first row.
                    SetFocus(null);
                    return false;
                }
            }

            if (StepDirection(seconds, out int dx, out int dy))
                Move(dx, dy, onSelect);

            if (Pad.Pressed(PadButton.A) && current != null)
                Activate(current);
            return false;
        }

        /// <summary>Mouse took over or the menu closed: removes focus styling and stops editing.</summary>
        public void Clear()
        {
            StopWheel();
            SetFocus(null);
            scope = null;
            heldX = heldY = 0;
            heldTime = 0f;
        }

        /// <summary>Forgets remembered controls when a whole menu tears down.</summary>
        public void Reset()
        {
            Clear();
            remembered.Clear();
        }

        void StopWheel()
        {
            if (editingWheel != null)
            {
                editingWheel.Classes.Remove("PadEdit");
                editingWheel.InvalidateVisual();
                editingWheel = null;
            }
        }

        void Move(int dx, int dy, Action<Control> onSelect)
        {
            // Left/Right on a slider, choice row or toggle edits it directly instead of leaving the row.
            if (dx != 0 && current != null && AdjustHorizontal(current, dx))
                return;
            Collect(scope);
            if (items.Count == 0)
                return;
            if (current == null || !items.Contains(current))
            {
                Select(PickInitial(), onSelect);
                return;
            }
            Control next = FindNeighbor(current, dx, dy) ?? WrapVertical(current, dy);
            if (next != null && next != current)
                Select(next, onSelect);
        }

        bool AdjustHorizontal(Control c, int dx)
        {
            if (c is Slider slider)
            {
                double span = slider.Maximum - slider.Minimum;
                double step = slider.Tag is double padStep && padStep > 0.0
                    ? padStep
                    : slider.IsSnapToTickEnabled && slider.TickFrequency > 0.0
                        ? slider.TickFrequency * (span >= 50.0 ? 5.0 : span >= 30.0 ? 2.0 : 1.0)
                        : span * 0.05;
                slider.Value = Math.Clamp(slider.Value + dx * step, slider.Minimum, slider.Maximum);
                return true;
            }
            if (c is StackPanel panel && panel.Classes.Contains("Choice") && panel.Tag is Action<int> stepChoice)
            {
                stepChoice(dx);
                return true;
            }
            if (c is ToggleSwitch toggle)
            {
                toggle.IsChecked = dx > 0;
                return true;
            }
            return false;
        }

        void Activate(Control c)
        {
            if (c is ToggleSwitch toggle)
                toggle.IsChecked = !(toggle.IsChecked == true);
            else if (c is Button button)
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if (c is StackPanel panel && panel.Classes.Contains("Choice") && panel.Tag is Action<int> stepChoice)
                stepChoice(1);
            else if (c is MenuTextBox box)
                MenuTextBox.Focus(box);
            else if (c is ColorWheel wheel)
            {
                editingWheel = wheel;
                wheel.Classes.Add("PadEdit");
                wheel.InvalidateVisual();
            }
            else if (c is Border { Tag: Action click })
                click();
        }

        void Select(Control next, Action<Control> onSelect)
        {
            SetFocus(next);
            if (next != null)
            {
                EnsureVisible(next);
                onSelect?.Invoke(next);
            }
        }

        void SetFocus(Control next)
        {
            if (current == next)
                return;
            Control old = current;
            current?.Classes.Remove("PadFocus");
            currentLine?.Classes.Remove("PadFocus");
            current = next;
            currentLine = FindSettingLine(next);
            current?.Classes.Add("PadFocus");
            currentLine?.Classes.Add("PadFocus");
            if (old is ColorWheel w0)
                w0.InvalidateVisual();
            if (next is ColorWheel w1)
                w1.InvalidateVisual();
            if (old is MenuTextBox t0)
                t0.RefreshFocus();
            if (next is MenuTextBox t1)
                t1.RefreshFocus();
            if (Trace)
                Console.WriteLine($"[padnav] focus -> {Describe(next)}");
        }

        static string Describe(Control c) => c switch
        {
            null => "(null)",
            Button b => $"Button({b.Content})",
            MenuTextBox box => $"TextBox({box.Placeholder})",
            Border { Child: TextBlock tb } => $"Chip({tb.Text})",
            _ => c.GetType().Name,
        };

        // Highlights the SettingRow line behind sliders, toggles and choice rows for clear contrast.
        static Border FindSettingLine(Control c)
        {
            if (c == null || c is Button && c is not ToggleSwitch)
                return null;
            for (Visual v = c.GetVisualParent(); v != null; v = v.GetVisualParent())
                if (v is Border b && b.Classes.Contains("SettingLine"))
                    return b;
            return null;
        }

        // Brings a newly selected item inside a ScrollViewer into view with a small margin.
        static void EnsureVisible(Control c)
        {
            ScrollViewer sv = null;
            for (Visual v = c.GetVisualParent(); v != null; v = v.GetVisualParent())
                if (v is ScrollViewer s)
                {
                    sv = s;
                    break;
                }
            if (sv == null || sv.Content is not Visual content || sv.Viewport.Height <= 0.0)
                return;
            if (c.TranslatePoint(default, content) is not { } top)
                return;
            const double Margin = 20.0;
            double itemTop = top.Y - Margin, itemBottom = top.Y + c.Bounds.Height + Margin;
            double y = sv.Offset.Y;
            if (itemTop < y)
                y = itemTop;
            else if (itemBottom > y + sv.Viewport.Height)
                y = itemBottom - sv.Viewport.Height;
            y = Math.Clamp(y, 0.0, sv.ScrollBarMaximum.Y);
            if (Math.Abs(y - sv.Offset.Y) > 0.5)
                sv.Offset = new Avalonia.Vector(sv.Offset.X, y);
        }

        // Reads left stick or D-pad into a cardinal step with hold-to-repeat.
        bool StepDirection(float seconds, out int dx, out int dy)
        {
            ReadDirection(out dx, out dy);
            if (dx == 0 && dy == 0)
            {
                heldX = heldY = 0;
                heldTime = 0f;
                return false;
            }
            if (dx != heldX || dy != heldY)
            {
                (heldX, heldY, heldTime) = (dx, dy, 0f);
                return true;
            }
            float was = heldTime;
            heldTime += seconds;
            if (heldTime < RepeatAfter)
                return false;
            int before = (int)((was - RepeatAfter) / RepeatEvery), now = (int)((heldTime - RepeatAfter) / RepeatEvery);
            return was < RepeatAfter || now > before;
        }

        static void ReadDirection(out int dx, out int dy)
        {
            dx = (Pad.Down(PadButton.DPadRight) ? 1 : 0) - (Pad.Down(PadButton.DPadLeft) ? 1 : 0);
            dy = (Pad.Down(PadButton.DPadDown) ? 1 : 0) - (Pad.Down(PadButton.DPadUp) ? 1 : 0);
            if (dx != 0 || dy != 0)
                return;
            Vector2 s = Pad.Left;
            if (MathF.Abs(s.X) >= StickOn || MathF.Abs(s.Y) >= StickOn)
            {
                if (MathF.Abs(s.X) > MathF.Abs(s.Y))
                    dx = s.X > 0f ? 1 : -1;
                else
                    dy = s.Y < 0f ? 1 : -1;
            }
        }

        Control FirstIn(Control root)
        {
            Collect(root);
            return PickInitial();
        }

        // Prefers content controls (buttons, rows, sliders) over top page tabs, since LB/RB switch tabs.
        Control PickInitial()
        {
            foreach (Control c in items)
                if (!(c is Border b && b.Classes.Contains("PageTab")))
                    return c;
            return items.Count > 0 ? items[0] : null;
        }

        // Spatial nearest neighbour in (dx, dy): horizontal steps stay on the same row and scroll scope,
        // vertical steps stay inside the current ScrollViewer until its end and prefer the closest row
        // when crossing a ScrollViewer boundary (e.g. Reset <-> list bottom, chips -> Host).
        Control FindNeighbor(Control from, int dx, int dy)
        {
            if (!RectIn(from, scope, out Rect a))
                return null;
            ScrollViewer ownScroll = OwnerScroll(from);
            if (dx != 0)
                return BestIn(from, a, dx, dy, ownScroll, matchScroll: true, visibleOnly: ownScroll == null, acrossWeight: 3.5, maxAcross: 8.0);
            if (ownScroll != null)
            {
                Control inside = BestIn(from, a, dx, dy, ownScroll, matchScroll: true, visibleOnly: false, acrossWeight: 3.5);
                if (inside != null)
                    return inside;
                return BestIn(from, a, dx, dy, requiredScroll: null, matchScroll: false, visibleOnly: false, acrossWeight: 0.2);
            }
            return BestIn(from, a, dx, dy, requiredScroll: null, matchScroll: false, visibleOnly: true, acrossWeight: 0.2);
        }

        Control BestIn(Control from, Rect a, int dx, int dy, ScrollViewer requiredScroll, bool matchScroll, bool visibleOnly,
                       double acrossWeight, double maxAcross = double.PositiveInfinity)
        {
            Control best = null;
            double bestScore = double.MaxValue;
            foreach (Control c in items)
            {
                if (c == from)
                    continue;
                ScrollViewer sv = OwnerScroll(c);
                if (matchScroll && sv != requiredScroll)
                    continue;
                if (visibleOnly && sv != null && !InViewport(c, sv))
                    continue;
                if (!RectIn(c, scope, out Rect b))
                    continue;
                double vx = b.Center.X - a.Center.X, vy = b.Center.Y - a.Center.Y;
                double along = vx * dx + vy * dy;
                if (along <= 4.0)
                    continue;
                double across = dx != 0 ? Gap(a.Top, a.Bottom, b.Top, b.Bottom) : Gap(a.Left, a.Right, b.Left, b.Right);
                if (across > maxAcross)
                    continue;
                double score = along + across * acrossWeight;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            return best;
        }

        // Picks the nearest visible item in sv after the right stick scrolled current out of view.
        void FollowScroll(ScrollViewer sv, bool down, Action<Control> onSelect)
        {
            Collect(scope);
            Control pick = null;
            // Picks the nearest visible item in sv after the right stick scrolled current out of view
            // (at the very bottom/top, picks the edge item so one stick tap leaves the list cleanly).
            bool takeTop = down ? sv.Offset.Y < sv.ScrollBarMaximum.Y - 2.0 : sv.Offset.Y <= 2.0;
            double bestY = takeTop ? double.MaxValue : double.MinValue;
            foreach (Control c in items)
            {
                if (OwnerScroll(c) != sv || !InViewport(c, sv) || !RectIn(c, scope, out Rect r))
                    continue;
                if (takeTop ? r.Center.Y < bestY : r.Center.Y > bestY)
                {
                    bestY = r.Center.Y;
                    pick = c;
                }
            }
            if (pick != null)
            {
                SetFocus(pick);
                onSelect?.Invoke(pick);
            }
        }

        static ScrollViewer OwnerScroll(Control c)
        {
            for (Visual v = c.GetVisualParent(); v != null; v = v.GetVisualParent())
                if (v is ScrollViewer s)
                    return s;
            return null;
        }

        static bool InViewport(Control c, ScrollViewer sv)
        {
            if (sv.Content is not Visual content || sv.Viewport.Height <= 0.0)
                return true;
            if (c.TranslatePoint(default, content) is not { } top)
                return false;
            return top.Y + c.Bounds.Height > sv.Offset.Y + 4.0 && top.Y < sv.Offset.Y + sv.Viewport.Height - 4.0;
        }

        // Wraps top <-> bottom in pure single-column boards (MainMenu column, PauseMenu board, dropdown).
        Control WrapVertical(Control from, int dy)
        {
            if (dy == 0 || items.Count < 2 || !RectIn(from, scope, out Rect a))
                return null;
            Control edge = null;
            double edgeY = dy > 0 ? double.MaxValue : double.MinValue;
            foreach (Control c in items)
            {
                if (!RectIn(c, scope, out Rect b))
                    continue;
                if (Gap(a.Left, a.Right, b.Left, b.Right) > 40.0)
                    return null;
                if (dy > 0 ? b.Center.Y < edgeY : b.Center.Y > edgeY)
                {
                    edgeY = b.Center.Y;
                    edge = c;
                }
            }
            return edge;
        }

        static double Gap(double a0, double a1, double b0, double b1)
            => a1 < b0 ? b0 - a1 : b1 < a0 ? a0 - b1 : 0.0;

        static bool RectIn(Control c, Control root, out Rect rect)
        {
            if (c.TranslatePoint(default, root) is not { } at)
            {
                rect = default;
                return false;
            }
            rect = new Rect(at, c.Bounds.Size);
            return rect.Width > 0.0 && rect.Height > 0.0;
        }

        // Visual-tree walk collecting every currently visible and enabled interactive control.
        void Collect(Control root)
        {
            items.Clear();
            walk.Clear();
            walk.Push(root);
            while (walk.Count > 0)
            {
                Visual v = walk.Pop();
                if (v is ScrollBar)
                    continue;
                if (v is Control c)
                {
                    if (!c.IsVisible || !c.IsEnabled)
                        continue;
                    if (IsInteractive(c))
                    {
                        if (c.Bounds.Width > 0.0 && c.Bounds.Height > 0.0)
                            items.Add(c);
                        continue;
                    }
                }
                PushChildren(v);
            }
        }

        void PushChildren(Visual v)
        {
            if (v.GetVisualChildren() is IReadOnlyList<Visual> list)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                    walk.Push(list[i]);
                return;
            }
            foreach (Visual child in v.GetVisualChildren())
                walk.Push(child);
        }

        static bool IsInteractive(Control c) => c switch
        {
            RepeatButton => false,
            ToggleSwitch => true,
            Button => true,
            Slider => true,
            MenuTextBox => true,
            ColorWheel => true,
            StackPanel p when p.Classes.Contains("Choice") => true,
            Border b when b.Tag is Action && !b.Classes.Contains("PageTab")
                          && (b.Classes.Contains("Pick") || b.Classes.Contains("Tab"))
                          && (!b.Classes.Contains("Off") || b.Classes.Contains("Chosen")) => true,
            _ => false,
        };

        static bool IsSelectable(Control c, Control root)
        {
            if (!c.IsVisible || !c.IsEffectivelyVisible || !c.IsEnabled || c.Bounds.Width <= 0.0)
                return false;
            for (Visual v = c; v != null; v = v.GetVisualParent())
                if (v == root)
                    return true;
            return false;
        }

        ScrollViewer FindScroll(Control root)
        {
            walk.Clear();
            walk.Push(root);
            while (walk.Count > 0)
            {
                Visual v = walk.Pop();
                if (v is Control c && !c.IsVisible)
                    continue;
                if (v is ScrollViewer sv && sv.Bounds.Height > 0.0)
                    return sv;
                PushChildren(v);
            }
            return null;
        }
    }
}
