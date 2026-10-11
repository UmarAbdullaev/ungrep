using NUnit.Framework;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace UnityGraph.Bridge.Tests
{
    /// <summary>The names `ug press` takes (SPEC-play-input): keys, gamepad buttons, mouse buttons, and the message for a name that is none of them.</summary>
    public class KeyNamesTests
    {
        [TestCase("Space", Key.Space)]
        [TestCase("space", Key.Space)]
        [TestCase("LeftShift", Key.LeftShift)]
        [TestCase("UpArrow", Key.UpArrow)]
        [TestCase("A", Key.A)]
        [TestCase("1", Key.Digit1)]
        [TestCase("0", Key.Digit0)]
        [TestCase("esc", Key.Escape)]
        [TestCase("Return", Key.Enter)]
        [TestCase("shift", Key.LeftShift)]
        [TestCase("ctrl", Key.LeftCtrl)]
        [TestCase("up", Key.UpArrow)]
        [TestCase("left", Key.LeftArrow)]
        [TestCase("del", Key.Delete)]
        public void A_key_by_enum_name_alias_or_digit(string text, Key expected)
        {
            Assert.IsTrue(KeyNames.TryKey(text, out Key key));
            Assert.AreEqual(expected, key);
        }

        [TestCase("None")]
        [TestCase("12")]
        [TestCase("-1")]
        [TestCase("Spacebar")]
        [TestCase("")]
        public void A_name_that_is_no_key_is_not_one(string text)
        {
            Assert.IsFalse(KeyNames.TryKey(text, out Key _));
        }

        [Test]
        public void Gamepad_buttons_by_compass_name_work_bare_and_the_letters_and_the_dpad_need_the_prefix()
        {
            Assert.IsTrue(KeyNames.TryPad("south", false, out GamepadButton button, out string label));
            Assert.AreEqual(GamepadButton.South, button);
            Assert.AreEqual("pad:south", label);
            Assert.IsTrue(KeyNames.TryPad("start", false, out button, out _));
            Assert.AreEqual(GamepadButton.Start, button);
            Assert.IsTrue(KeyNames.TryPad("lshoulder", false, out button, out _));
            Assert.AreEqual(GamepadButton.LeftShoulder, button);

            Assert.IsFalse(KeyNames.TryPad("a", false, out _, out _), "a is the key A without the prefix");
            Assert.IsFalse(KeyNames.TryPad("up", false, out _, out _), "up is the arrow key without the prefix");
            Assert.IsTrue(KeyNames.TryPad("a", true, out button, out label));
            Assert.AreEqual(GamepadButton.South, button);
            Assert.AreEqual("pad:a", label);
            Assert.IsTrue(KeyNames.TryPad("UP", true, out button, out _));
            Assert.AreEqual(GamepadButton.DpadUp, button);
            Assert.IsFalse(KeyNames.TryPad("zzz", true, out _, out _));
        }

        [Test]
        public void Mouse_buttons()
        {
            Assert.IsTrue(KeyNames.TryMouse("left", out MouseButton button));
            Assert.AreEqual(MouseButton.Left, button);
            Assert.IsTrue(KeyNames.TryMouse("Middle", out button));
            Assert.AreEqual(MouseButton.Middle, button);
            Assert.IsFalse(KeyNames.TryMouse("forward2", out _));
        }

        [Test]
        public void An_unknown_name_gets_near_names_or_where_to_look()
        {
            StringAssert.StartsWith("no key or button Spase; near: Space", KeyNames.Unknown("Spase"));
            StringAssert.StartsWith("no key or button Zzzzzzzzzzzz; keys: ", KeyNames.Unknown("Zzzzzzzzzzzz"));
            StringAssert.EndsWith(", ... (every key: ug api UnityEngine.InputSystem.Key --all); pad:south for a gamepad button, mouse:left for a mouse button", KeyNames.Unknown("Zzzzzzzzzzzz"));
            StringAssert.StartsWith("no gamepad button zzz; buttons: south", KeyNames.Unknown("pad:zzz"));
            StringAssert.Contains("select", KeyNames.Unknown("pad:zzz"));
            StringAssert.EndsWith(", right", KeyNames.Unknown("pad:zzz"));
            StringAssert.StartsWith("no mouse button thumb; buttons: ", KeyNames.Unknown("mouse:thumb"));
        }

        [Test]
        public void The_device_provider_names_what_it_will_press_and_refuses_what_it_does_not_know()
        {
            var devices = new DeviceInput();

            PlayAction key = devices.Press("Space", 0.1, out string error);
            Assert.IsNotNull(key);
            Assert.IsNull(error);
            Assert.AreEqual("press Space", key.Name);
            Assert.AreEqual("press pad:south", devices.Press("pad:south", 0.1, out _).Name);
            Assert.AreEqual("press mouse:left", devices.Press("mouse:left", 0.1, out _).Name);
            Assert.AreEqual("stick left", devices.Stick(true, 1, 0, 0.5).Name);

            Assert.IsNull(devices.Press("Spase", 0.1, out error));
            StringAssert.StartsWith("no key or button Spase; near: Space", error);
            Assert.IsNull(devices.Press("pad:up2", 0.1, out error));
            StringAssert.StartsWith("no gamepad button up2", error);
            Assert.IsNull(devices.Press("mouse:thumb", 0.1, out error));
            StringAssert.StartsWith("no mouse button thumb", error);
        }
    }
}
