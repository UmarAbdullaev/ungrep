using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("Ungrep.Bridge.Editor.Tests")]
[assembly: InternalsVisibleTo("Ungrep.Bridge.Editor.TestRuns")]
[assembly: InternalsVisibleTo("Ungrep.Bridge.Editor.Ugui")]
[assembly: InternalsVisibleTo("Ungrep.Bridge.Editor.InputSystem")]
[assembly: InternalsVisibleTo("Ungrep.Bridge.Editor.Urp")]
[assembly: InternalsVisibleTo("Ungrep.Bridge.Editor.Cinemachine")]
[assembly: InternalsVisibleTo("Ungrep.Bridge.Editor.Ugui.Tests")]
[assembly: InternalsVisibleTo("Ungrep.Bridge.Editor.InputSystem.Tests")]
[assembly: InternalsVisibleTo("Ungrep.Bridge.Editor.Cinemachine.Tests")]

namespace Ungrep.Bridge
{
    /// <summary>
    /// A small JSON writer: objects, arrays, strings, numbers, booleans and pre-written JSON.
    /// JsonUtility cannot write dictionaries-shaped answers or nulls, so answers are written by hand.
    /// </summary>
    internal sealed class Json
    {
        private readonly StringBuilder _text = new StringBuilder();
        private readonly Stack<bool> _first = new Stack<bool>();
        private bool _afterKey;

        public Json Object()
        {
            Separator();
            _text.Append('{');
            _first.Push(true);
            return this;
        }

        public Json EndObject()
        {
            _first.Pop();
            _text.Append('}');
            return this;
        }

        public Json Array()
        {
            Separator();
            _text.Append('[');
            _first.Push(true);
            return this;
        }

        public Json EndArray()
        {
            _first.Pop();
            _text.Append(']');
            return this;
        }

        public Json Key(string name)
        {
            Separator();
            WriteString(name);
            _text.Append(':');
            _afterKey = true;
            return this;
        }

        public Json Value(string value)
        {
            Separator();
            if (value == null)
            {
                _text.Append("null");
            }
            else
            {
                WriteString(value);
            }

            return this;
        }

        public Json Value(long value)
        {
            Separator();
            _text.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public Json Value(double value)
        {
            Separator();
            _text.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            return this;
        }

        public Json Value(bool value)
        {
            Separator();
            _text.Append(value ? "true" : "false");
            return this;
        }

        /// <summary>Writes text that is already JSON (a stored answer).</summary>
        public Json Raw(string json)
        {
            Separator();
            _text.Append(json);
            return this;
        }

        public Json Field(string name, string value) => Key(name).Value(value);

        public Json Field(string name, long value) => Key(name).Value(value);

        public Json Field(string name, double value) => Key(name).Value(value);

        public Json Field(string name, bool value) => Key(name).Value(value);

        public override string ToString() => _text.ToString();

        public static string Escape(string value)
        {
            var json = new Json();
            json.WriteString(value);
            return json.ToString();
        }

        private void Separator()
        {
            if (_afterKey)
            {
                _afterKey = false;
                return;
            }

            if (_first.Count == 0)
            {
                return;
            }

            if (_first.Peek())
            {
                _first.Pop();
                _first.Push(false);
            }
            else
            {
                _text.Append(',');
            }
        }

        private void WriteString(string value)
        {
            _text.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': _text.Append("\\\""); break;
                    case '\\': _text.Append("\\\\"); break;
                    case '\n': _text.Append("\\n"); break;
                    case '\r': _text.Append("\\r"); break;
                    case '\t': _text.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            _text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _text.Append(c);
                        }

                        break;
                }
            }

            _text.Append('"');
        }
    }
}
