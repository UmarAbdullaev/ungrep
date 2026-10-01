using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// `ug press`, `stick`, `tap` and `swipe` through queued Input System state events (SPEC-play-input).
    /// Devices are used as they are; a missing one is added as `ug <kind>` and removed when play stops.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class DeviceInput : IDeviceInput
    {
        private const int TouchId = 1;

        private readonly List<InputDevice> _added = new List<InputDevice>();
        private bool _focus;
        private InputSettings.EditorInputBehaviorInPlayMode _editorBehavior;
        private InputSettings.BackgroundBehavior _background;

        static DeviceInput()
        {
            PlayInput.Devices = new DeviceInput();
        }

        public PlayAction Press(string name, double hold, out string error)
        {
            error = null;
            string text = name;
            bool prefixed = false;
            if (text.StartsWith("pad:", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(4);
                prefixed = true;
            }
            else if (text.StartsWith("mouse:", StringComparison.OrdinalIgnoreCase))
            {
                if (KeyNames.TryMouse(text.Substring(6), out MouseButton mouseButton))
                {
                    return PressMouse(mouseButton, hold);
                }

                error = KeyNames.Unknown(name);
                return null;
            }

            if (!prefixed && KeyNames.TryKey(text, out Key key))
            {
                return PressKey(key, hold);
            }

            if (KeyNames.TryPad(text, prefixed, out GamepadButton button, out string label))
            {
                return PressPad(button, label, hold);
            }

            error = KeyNames.Unknown(name);
            return null;
        }

        public PlayAction Stick(bool left, float x, float y, double hold)
        {
            string side = left ? "left" : "right";
            Gamepad pad = null;
            var action = new PlayAction("stick " + side);
            action.Start = () =>
            {
                pad = Prepare(Gamepad.current, () => InputSystem.AddDevice<Gamepad>("ug gamepad"));
                var state = new GamepadState();
                if (left)
                {
                    state.leftStick = new Vector2(x, y);
                }
                else
                {
                    state.rightStick = new Vector2(x, y);
                }

                InputSystem.QueueStateEvent(pad, state);
                return Answer.Line("stick " + side + " " + Number(x) + "," + Number(y) + " for " + InputQueue.Seconds(hold));
            };
            action.Hold = PlayAction.Lasting(hold, 2);
            action.Release = () => Queue(pad, new GamepadState());
            return action;
        }

        public PlayAction Tap(float x, float y, double hold, bool mouse) =>
            Pointer("tap", mouse, x, y, x, y, hold, 2, false,
                "tapped " + Number(x) + "," + Number(y) + " for " + InputQueue.Seconds(hold));

        public PlayAction Swipe(float x1, float y1, float x2, float y2, double over, bool mouse) =>
            Pointer("swipe", mouse, x1, y1, x2, y2, over, 3, true,
                "swiped " + Number(x1) + "," + Number(y1) + " -> " + Number(x2) + "," + Number(y2) + " over " + InputQueue.Seconds(over));

        public void Reset()
        {
            foreach (InputDevice device in _added)
            {
                if (device.added)
                {
                    InputSystem.RemoveDevice(device);
                }
            }

            _added.Clear();
            if (_focus)
            {
                _focus = false;
                InputSettings settings = InputSystem.settings;
                settings.editorInputBehaviorInPlayMode = _editorBehavior;
                settings.backgroundBehavior = _background;
            }
        }

        private PlayAction PressKey(Key key, double hold)
        {
            Keyboard keyboard = null;
            var action = new PlayAction("press " + key);
            action.Start = () =>
            {
                keyboard = Prepare(Keyboard.current, () => InputSystem.AddDevice<Keyboard>("ug keyboard"));
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
                return Answer.Line("pressed " + key + " for " + InputQueue.Seconds(hold));
            };
            action.Hold = PlayAction.Lasting(hold, 2);
            action.Release = () => Queue(keyboard, new KeyboardState());
            return action;
        }

        private PlayAction PressPad(GamepadButton button, string label, double hold)
        {
            Gamepad pad = null;
            var action = new PlayAction("press " + label);
            action.Start = () =>
            {
                pad = Prepare(Gamepad.current, () => InputSystem.AddDevice<Gamepad>("ug gamepad"));
                var state = new GamepadState();
                if (button == GamepadButton.LeftTrigger)
                {
                    state.leftTrigger = 1f;
                }
                else if (button == GamepadButton.RightTrigger)
                {
                    state.rightTrigger = 1f;
                }
                else
                {
                    state = state.WithButton(button);
                }

                InputSystem.QueueStateEvent(pad, state);
                return Answer.Line("pressed " + label + " for " + InputQueue.Seconds(hold));
            };
            action.Hold = PlayAction.Lasting(hold, 2);
            action.Release = () => Queue(pad, new GamepadState());
            return action;
        }

        // A mouse button at the pointer's current place.
        private PlayAction PressMouse(MouseButton button, double hold)
        {
            Mouse mouse = null;
            Vector2 at = default;
            var action = new PlayAction("press mouse:" + button.ToString().ToLowerInvariant());
            action.Start = () =>
            {
                mouse = Prepare(Mouse.current, () => InputSystem.AddDevice<Mouse>("ug mouse"));
                at = mouse.position.ReadValue();
                InputSystem.QueueStateEvent(mouse, new MouseState { position = at }.WithButton(button));
                return Answer.Line("pressed mouse:" + button.ToString().ToLowerInvariant() + " for " + InputQueue.Seconds(hold));
            };
            action.Hold = PlayAction.Lasting(hold, 2);
            action.Release = () => Queue(mouse, new MouseState { position = at });
            return action;
        }

        // A touch or the left mouse button from one Game view point to another (y down), held for `seconds` and `frames` at least.
        private PlayAction Pointer(string name, bool mouse, float x1, float y1, float x2, float y2, double seconds, int frames, bool moves, string line)
        {
            Touchscreen touch = null;
            Mouse pointer = null;
            Vector2 from = Flip(x1, y1);
            Vector2 to = Flip(x2, y2);
            var action = new PlayAction(name + (mouse ? " mouse" : ""));

            void Send(Vector2 at, TouchPhase phase)
            {
                if (mouse)
                {
                    var state = new MouseState { position = at };
                    InputSystem.QueueStateEvent(pointer, phase == TouchPhase.Ended ? state : state.WithButton(MouseButton.Left));
                }
                else
                {
                    InputSystem.QueueStateEvent(touch, new TouchState { touchId = TouchId, phase = phase, position = at, pressure = phase == TouchPhase.Ended ? 0f : 1f });
                }
            }

            action.Start = () =>
            {
                if (mouse)
                {
                    pointer = Prepare(Mouse.current, () => InputSystem.AddDevice<Mouse>("ug mouse"));
                }
                else
                {
                    touch = Prepare(Touchscreen.current, () => InputSystem.AddDevice<Touchscreen>("ug touchscreen"));
                }

                Send(from, TouchPhase.Began);
                return Answer.Line(line);
            };
            action.Hold = PlayAction.Lasting(seconds, frames, moves ? fraction => Send(Vector2.Lerp(from, to, fraction), TouchPhase.Moved) : (Action<float>)null);
            action.Release = () => Send(to, TouchPhase.Ended);
            return action;
        }

        // The device to send to: the machine's own, else one added for this play session. Also lets input reach the game unfocused.
        private T Prepare<T>(T current, Func<T> add) where T : InputDevice
        {
            Focus();
            if (current != null && current.added)
            {
                return current;
            }

            T device = add();
            _added.Add(device);
            return device;
        }

        // Without a focused Game view the Input System drops keyboard and pointer input; in memory only until play stops.
        private void Focus()
        {
            if (_focus)
            {
                return;
            }

            InputSettings settings = InputSystem.settings;
            _editorBehavior = settings.editorInputBehaviorInPlayMode;
            _background = settings.backgroundBehavior;
            settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            _focus = true;
        }

        private static void Queue<TState>(InputDevice device, TState state) where TState : struct, IInputStateTypeInfo
        {
            if (device != null && device.added)
            {
                InputSystem.QueueStateEvent(device, state);
            }
        }

        // Game view pixels, y down, to the Input System's screen space, y up.
        private static Vector2 Flip(float x, float y) => new Vector2(x, Screen.height - y);

        private static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
