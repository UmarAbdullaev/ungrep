using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ungrep.Bridge.Tests
{
    /// <summary>`Values.Parse` of the compound types: what is read back is what was set.</summary>
    public class ValuesCompoundTests
    {
        private ValueHolder _holder;
        private SerializedObject _serialized;

        [SetUp]
        public void SetUp()
        {
            _holder = ScriptableObject.CreateInstance<ValueHolder>();
            _serialized = new SerializedObject(_holder);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_holder);

        private string Set(string field, string text)
        {
            string problem = Values.Parse(_serialized.FindProperty(field), text);
            if (problem == null)
            {
                _serialized.ApplyModifiedPropertiesWithoutUndo();
                _serialized.Update();
            }

            return problem;
        }

        private string Show(string field) => Values.Format(_serialized.FindProperty(field));

        [Test]
        public void Bounds_curve_gradient_and_hash()
        {
            Assert.IsNull(Set("bounds", "center 0, 1, 0 size 2, 2, 2"));
            Assert.AreEqual("center 0, 1, 0 size 2, 2, 2", Show("bounds"));
            Assert.IsNull(Set("bounds", "1, 2, 3, 4, 5, 6"));
            Assert.AreEqual("center 1, 2, 3 size 4, 5, 6", Show("bounds"));
            StringAssert.StartsWith("not bounds", Set("bounds", "1, 2"));

            Assert.IsNull(Set("curve", "0:0, 1:1"));
            Assert.AreEqual("curve 0:0, 1:1", Show("curve"));
            Assert.AreEqual(2, _holder.curve.length);
            Assert.IsNull(Set("curve", "linear 0:0, 0.5:2, 1:0"));
            Assert.AreEqual("curve 0:0, 0.5:2, 1:0", Show("curve"));
            Assert.IsNull(Set("curve", "3"));
            Assert.AreEqual(3f, _holder.curve.Evaluate(0.5f));
            Assert.IsNull(Set("curve", "empty"));
            Assert.AreEqual("curve empty", Show("curve"));
            StringAssert.StartsWith("not a curve", Set("curve", "a:b"));
            StringAssert.StartsWith("two keys at time 1", Set("curve", "1:0, 1:2"));

            Assert.IsNull(Set("gradient", "#ff0000@0, #0000ff80@1"));
            Assert.AreEqual("gradient #ff0000ff@0, #0000ff80@1", Show("gradient"));
            Assert.IsNull(Set("gradient", "fixed #ff0000, #00ff00, #0000ff"));
            Assert.AreEqual("gradient fixed #ff0000ff@0, #00ff00ff@0.5, #0000ffff@1", Show("gradient"));
            Assert.AreEqual(GradientMode.Fixed, _holder.gradient.mode);
            StringAssert.StartsWith("not a gradient", Set("gradient", "nothing@9"));

            Assert.IsNull(Set("hash", "0123456789abcdef0123456789abcdef"));
            Assert.AreEqual("0123456789abcdef0123456789abcdef", Show("hash"));
            StringAssert.StartsWith("not a hash", Set("hash", "xyz"));
        }

        [Test]
        public void A_struct_from_its_fields_and_back()
        {
            Assert.IsNull(Set("pair", "{count: 3, offset: 1, 2, 3, name: \"a, b\"}"));
            Assert.AreEqual(3, _holder.pair.count);
            Assert.AreEqual(new Vector3(1, 2, 3), _holder.pair.offset);
            Assert.AreEqual("a, b", _holder.pair.name);

            string shown = Show("pair");
            Assert.AreEqual("{count: 3, offset: 1, 2, 3, name: \"a, b\"}", shown);
            Assert.IsNull(Set("pair", "count: 7"));
            Assert.AreEqual(7, _holder.pair.count);
            Assert.AreEqual(new Vector3(1, 2, 3), _holder.pair.offset);
            Assert.IsNull(Set("pair", shown));
            Assert.AreEqual(3, _holder.pair.count);

            Assert.AreEqual("Pair has no field size; fields: count, offset, name", Set("pair", "size: 1"));
            Assert.AreEqual("count: not a whole number: x", Set("pair", "count: x"));
        }

        [Test]
        public void Whole_lists()
        {
            Assert.IsNull(Set("numbers", "[1, 2, 3]"));
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, _holder.numbers);
            Assert.IsNull(Set("numbers", "4, 5"));
            CollectionAssert.AreEqual(new[] { 4, 5 }, _holder.numbers);
            Assert.AreEqual("numbers[1]: not a whole number: x", Set("numbers", "[1, x]"));
            Assert.IsNull(Set("numbers", "empty"));
            Assert.AreEqual(0, _holder.numbers.Length);

            Assert.IsNull(Set("names", "[\"a, b\", c]"));
            CollectionAssert.AreEqual(new[] { "a, b", "c" }, _holder.names);

            Assert.IsNull(Set("points", "(1, 2, 3), (4, 5, 6)"));
            Assert.AreEqual(new Vector3(4, 5, 6), _holder.points[1]);
            Assert.IsNull(Set("points", "1, 2, 3, 7, 8, 9"));
            Assert.AreEqual(new Vector3(7, 8, 9), _holder.points[1]);
            Assert.AreEqual(2, _holder.points.Length);

            Assert.IsNull(Set("pairs", "[{count: 1, name: x}, {count: 2}]"));
            Assert.AreEqual(2, _holder.pairs.Count);
            Assert.AreEqual(2, _holder.pairs[1].count);
            Assert.AreEqual("x", _holder.pairs[0].name);
        }

        [Test]
        public void A_cut_value_says_so_and_whole_shows_every_item_and_level()
        {
            Assert.IsNull(Set("numbers", "0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11"));
            Assert.AreEqual("0, 1, 2, 3, 4, 5, 6, 7, 8, 9, ... 2 more", Values.Format(_serialized.FindProperty("numbers"), out bool cut));
            Assert.IsTrue(cut);
            Assert.AreEqual("0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11", Values.Whole(_serialized.FindProperty("numbers")));

            Assert.AreEqual("{inner: {...}, level: 0}", Values.Format(_serialized.FindProperty("nest"), out cut));
            Assert.IsTrue(cut);
            Assert.AreEqual("{inner: {count: 0, offset: 0, 0, 0, name: \"\"}, level: 0}", Values.Whole(_serialized.FindProperty("nest")));

            Assert.AreEqual("{count: 0, offset: 0, 0, 0, name: \"\"}", Values.Format(_serialized.FindProperty("pair"), out cut));
            Assert.IsFalse(cut);

            var colors = new GradientColorKey[8];
            var alphas = new GradientAlphaKey[8];
            for (int i = 0; i < 8; i++)
            {
                colors[i] = new GradientColorKey(Color.red, i / 10f);
                alphas[i] = new GradientAlphaKey(1f, i / 10f + 0.05f);
            }

            _holder.gradient.SetKeys(colors, alphas);
            _serialized.Update();
            StringAssert.EndsWith(", ... 6 more", Values.Format(_serialized.FindProperty("gradient"), out cut));
            Assert.IsTrue(cut);
            string whole = Values.Whole(_serialized.FindProperty("gradient"));
            Assert.AreEqual(16, whole.Split('@').Length - 1);
            StringAssert.DoesNotContain("more", whole);
        }

        [Test]
        public void A_serialize_reference_takes_a_type_and_its_fields()
        {
            Assert.IsNull(Set("shape", "Circle {radius: 4, label: \"big\"}"));
            Assert.IsInstanceOf<Circle>(_holder.shape);
            Assert.AreEqual(4f, ((Circle)_holder.shape).radius);
            Assert.AreEqual("big", ((Circle)_holder.shape).label);

            Assert.IsNull(Set("shape", "{radius: 6}"));
            Assert.AreEqual(6f, ((Circle)_holder.shape).radius);
            Assert.AreEqual("big", ((Circle)_holder.shape).label);

            Assert.IsNull(Set("shape", "square"));
            Assert.IsInstanceOf<Square>(_holder.shape);
            StringAssert.StartsWith("no type Hexagon fits", Set("shape", "Hexagon"));
            StringAssert.Contains("one of Circle, Square", Set("shape", "Hexagon"));

            Assert.IsNull(Set("shape", "null"));
            Assert.IsNull(_holder.shape);
            StringAssert.StartsWith("no type yet", Set("shape", "{radius: 1}"));
        }
    }
}
