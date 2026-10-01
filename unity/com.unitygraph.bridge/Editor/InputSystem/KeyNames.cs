using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace UnityGraph.Bridge
{
    /// <summary>The names `ug press` takes (SPEC-play-input, `ug press`): keyboard keys, gamepad buttons, mouse buttons.</summary>
    internal static class KeyNames
    {
        private const int MaxNear = 5;

        private static readonly Dictionary<string, Key> KeyAliases = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
        {
            { "esc", Key.Escape },
            { "return", Key.Enter },
            { "shift", Key.LeftShift },
            { "ctrl", Key.LeftCtrl },
            { "control", Key.LeftCtrl },
            { "alt", Key.LeftAlt },
            { "up", Key.UpArrow },
            { "down", Key.DownArrow },
            { "left", Key.LeftArrow },
            { "right", Key.RightArrow },
            { "del", Key.Delete },
        };

        // Gamepad buttons by name. The ones marked `Bare` also work without `pad:`; the rest are keyboard names too.
        private static readonly Dictionary<string, (GamepadButton Button, bool Bare)> PadNames = new Dictionary<string, (GamepadButton, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            { "south", (GamepadButton.South, true) },
            { "east", (GamepadButton.East, true) },
            { "west", (GamepadButton.West, true) },
            { "north", (GamepadButton.North, true) },
            { "a", (GamepadButton.South, false) },
            { "b", (GamepadButton.East, false) },
            { "x", (GamepadButton.West, false) },
            { "y", (GamepadButton.North, false) },
            { "start", (GamepadButton.Start, true) },
            { "select", (GamepadButton.Select, true) },
            { "lshoulder", (GamepadButton.LeftShoulder, true) },
            { "rshoulder", (GamepadButton.RightShoulder, true) },
            { "ltrigger", (GamepadButton.LeftTrigger, true) },
            { "rtrigger", (GamepadButton.RightTrigger, true) },
            { "lstick", (GamepadButton.LeftStick, true) },
            { "rstick", (GamepadButton.RightStick, true) },
            { "up", (GamepadButton.DpadUp, false) },
            { "down", (GamepadButton.DpadDown, false) },
            { "left", (GamepadButton.DpadLeft, false) },
            { "right", (GamepadButton.DpadRight, false) },
        };

        private static readonly Dictionary<string, MouseButton> MouseNames = new Dictionary<string, MouseButton>(StringComparer.OrdinalIgnoreCase)
        {
            { "left", MouseButton.Left },
            { "right", MouseButton.Right },
            { "middle", MouseButton.Middle },
        };

        /// <summary>A key by enum name or alias, `1` for `Digit1`.</summary>
        public static bool TryKey(string text, out Key key)
        {
            if (text.Length == 1 && text[0] >= '0' && text[0] <= '9')
            {
                key = (Key)Enum.Parse(typeof(Key), "Digit" + text);
                return true;
            }

            if (KeyAliases.TryGetValue(text, out key))
            {
                return true;
            }

            bool number = int.TryParse(text, out _);
            return !number && Enum.TryParse(text, true, out key) && key != Key.None && Enum.IsDefined(typeof(Key), key);
        }

        public static bool TryPad(string text, bool prefixed, out GamepadButton button, out string label)
        {
            button = default;
            label = null;
            if (!PadNames.TryGetValue(text, out (GamepadButton Button, bool Bare) found) || (!prefixed && !found.Bare))
            {
                return false;
            }

            button = found.Button;
            label = "pad:" + text.ToLowerInvariant();
            return true;
        }

        public static bool TryMouse(string text, out MouseButton button) => MouseNames.TryGetValue(text, out button);

        /// <summary>The message for a name that is none of those: near names when there are some, else where to look.</summary>
        public static string Unknown(string name)
        {
            string prefix = "no key or button " + name;
            if (name.StartsWith("pad:", StringComparison.OrdinalIgnoreCase))
            {
                return "no gamepad button " + name.Substring(4) + "; buttons: " + string.Join(", ", PadNames.Keys.Take(12));
            }

            if (name.StartsWith("mouse:", StringComparison.OrdinalIgnoreCase))
            {
                return "no mouse button " + name.Substring(6) + "; buttons: " + string.Join(", ", MouseNames.Keys);
            }

            IEnumerable<string> names = Enum.GetNames(typeof(Key)).Where(n => n != nameof(Key.None)).Concat(KeyAliases.Keys).Concat(PadNames.Where(p => p.Value.Bare).Select(p => p.Key));
            List<string> near = names
                .Select(n => (Name: n, Distance: Distance(n.ToLowerInvariant(), name.ToLowerInvariant())))
                .Where(n => n.Distance <= 2 || n.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(n => n.Distance)
                .ThenBy(n => n.Name, StringComparer.Ordinal)
                .Select(n => n.Name)
                .Take(MaxNear)
                .ToList();
            return near.Count > 0
                ? prefix + "; near: " + string.Join(", ", near)
                : prefix + "; keys: " + string.Join(", ", Enum.GetNames(typeof(Key)).Where(n => n != nameof(Key.None)).Take(MaxNear)) + "; pad:south for a gamepad button, mouse:left for a mouse button";
        }

        // Edit distance, for near names.
        private static int Distance(string a, string b)
        {
            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length];
        }
    }
}
