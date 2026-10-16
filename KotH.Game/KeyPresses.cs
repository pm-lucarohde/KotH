using Stride.Input;

namespace KotH.Client
{
    /// <summary>
    /// Key presses without the operating system's auto-repeat. Stride's
    /// IsKeyPressed also reports every repeat of a held key, so a held
    /// Space turned into a stream of taps and fired the double jump.
    /// Unity's GetKeyDown never saw repeats; this is the same rule.
    /// </summary>
    public static class KeyPresses
    {
        /// <summary>How often key went down this frame, repeats left out.</summary>
        public static int Count(InputManager input, Keys key)
        {
            int count = 0;
            var events = input.KeyEvents;
            for (int i = 0; i < events.Count; i++)
            {
                KeyEvent e = events[i];
                if (e.IsDown && e.RepeatCount == 0 && e.Key == key)
                    count++;
            }
            return count;
        }

        /// <summary>Whether key went down this frame, repeats left out.</summary>
        public static bool Pressed(InputManager input, Keys key) => Count(input, key) > 0;
    }
}
