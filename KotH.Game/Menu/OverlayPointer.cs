using System;
using Stride.Core.Mathematics;
using Stride.Input;

namespace KotH.Client.Menu
{
    /// <summary>
    /// Hands the mouse to the overlay under it (a pressed one keeps it until
    /// the release, so a click completes), only when something happened; for
    /// the main menu and the pause menu.
    /// </summary>
    public sealed class OverlayPointer
    {
        MenuOverlay hovered, pressed;
        Vector2 last = new Vector2(-1f);

        /// <param name="targets">The overlays that take the pointer; hidden ones are skipped.</param>
        public void Update(InputManager input, Vector2 screen, MenuOverlay[] targets)
        {
            if (input.Mouse == null)
                return;
            Vector2 at = input.MousePosition * screen;
            bool down = input.IsMouseButtonPressed(MouseButton.Left), up = input.IsMouseButtonReleased(MouseButton.Left);
            MenuOverlay target = pressed ?? Array.Find(targets, o => o.Contains(at));
            if (target != hovered)
            {
                hovered?.Leave();
                hovered = target;
            }
            if (target != null && (at != last || down || up))
                target.Move(at, input.IsMouseButtonDown(MouseButton.Left));
            last = at;
            if (down && target != null)
            {
                target.Press(at);
                pressed = target;
            }
            if (up && pressed != null)
            {
                pressed.Release(at);
                pressed = null;
            }
        }

        /// <summary>The overlays went away: nothing stays under the pointer or pressed.</summary>
        public void Reset()
        {
            hovered?.Leave();
            hovered = pressed = null;
            last = new Vector2(-1f);
        }
    }
}
