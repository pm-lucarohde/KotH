using Stride.Core.Mathematics;
using Stride.Input;

namespace KotH.Client.Menu
{
    /// <summary>Hands the mouse to the overlay under it (a pressed one keeps it until the
    /// release, so a click completes) and the wheel, only when something happened; for the
    /// main menu and the pause menu.</summary>
    public sealed class OverlayPointer
    {
        MenuOverlay hovered, pressed;
        Vector2 last = new Vector2(-1f);

        // A loop, not Array.Find: its lambda captured the point, a closure every frame.
        static MenuOverlay Under(MenuOverlay[] targets, Vector2 at)
        {
            foreach (MenuOverlay target in targets)
                if (target.Contains(at))
                    return target;
            return null;
        }

        /// <param name="targets">The overlays that take the pointer; hidden ones are skipped.</param>
        public void Update(InputManager input, Vector2 screen, MenuOverlay[] targets)
        {
            if (input.Mouse == null)
                return;
            Vector2 at = input.MousePosition * screen;
            bool down = input.IsMouseButtonPressed(MouseButton.Left), up = input.IsMouseButtonReleased(MouseButton.Left);

            MenuOverlay target = pressed ?? Under(targets, at);
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
                pressed = target;
                target.Press(at);
            }
            if (up && pressed != null)
            {
                pressed.Release(at);
                pressed = null;
            }
            if (input.MouseWheelDelta != 0f && target != null)
                target.Wheel(at, input.MouseWheelDelta);
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
