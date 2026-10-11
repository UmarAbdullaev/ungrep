using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ungrep.Assets;

/// <summary>An object reference, `{fileID: N, guid: G, type: T}`; <see cref="Guid"/> is null for a local one.</summary>
public readonly record struct ObjectRef(long FileId, string? Guid, int Type);

/// <summary>
/// A scalar in a document: <see cref="Path"/> is `m_Component[0].component` or
/// `_waves[2].prefab`; <see cref="Ref"/> is set when the value is an object reference.
/// </summary>
public sealed record YamlLeaf(string Path, string Value, ObjectRef? Ref, int Line);

/// <summary>One `--- !u!classID &amp;fileID` document; lines are 1-based.</summary>
public sealed record YamlDocument(int ClassId, string ClassName, long FileId, bool Stripped, int LineStart, int LineEnd, IReadOnlyList<YamlLeaf> Leaves);

/// <summary>
/// Reads the YAML subset Unity writes for scenes, prefabs and assets (SPEC-asset-index, Reading
/// Unity YAML) into flat leaves per document. No YAML package: Unity's output is regular enough
/// for a line reader, and anything else stops that document with a problem instead of throwing.
/// </summary>
public static partial class UnityYaml
{
    /// <summary>Reads every document; adds at most one problem per call (`line N: reason`).</summary>
    public static IReadOnlyList<YamlDocument> Read(TextReader text, List<string> problems)
    {
        var reader = new Reader(problems);
        int number = 0;
        while (text.ReadLine() is { } line)
        {
            reader.Line(line, ++number);
        }

        reader.End();
        return reader.Documents;
    }

    [GeneratedRegex(@"^--- !u!(\d+) &(-?\d+)( stripped)?\s*$")]
    private static partial Regex Header();

    // A mapping key: not starting with a quote, flow bracket, comment or YAML indicator.
    [GeneratedRegex(@"^([^\s'""{\[#&*!|>%@`-][^:]*?):(?: (.*)|)$")]
    private static partial Regex KeyLine();

    private sealed class Frame(int indent, string path, bool item)
    {
        public int Indent { get; } = indent;
        public string Path { get; } = path;
        public bool Item { get; } = item;
        public int NextIndex { get; set; }
    }

    private sealed class Reader(List<string> problems)
    {
        private readonly List<Frame> _stack = [];
        private List<YamlLeaf> _leaves = [];
        private bool _reported;

        // The document being read.
        private int _classId;
        private string _className = "";
        private long _fileId;
        private bool _stripped;
        private int _start;
        private int _last;
        private bool _inDocument;
        private bool _needClass;
        private bool _stopped;

        // A scalar that goes on over the next lines.
        private StringBuilder? _open;
        private char _quote;
        private string _openPath = "";
        private int _openLine;
        private int _plainIndent = int.MaxValue;

        public List<YamlDocument> Documents { get; } = [];

        public void Line(string line, int number)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                Match header = Header().Match(line);
                if (header.Success)
                {
                    End();
                    _classId = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                    _fileId = long.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
                    _stripped = header.Groups[3].Success;
                    (_start, _last, _className) = (number, number, "");
                    (_inDocument, _needClass, _stopped) = (true, true, false);
                    _stack.Clear();
                    _stack.Add(new Frame(0, "", item: false));
                    _leaves = [];
                    _plainIndent = int.MaxValue;
                    return;
                }
            }

            if (!_inDocument || line.Length == 0)
            {
                return;
            }

            _last = number;
            if (_stopped)
            {
                return;
            }

            if (_open is not null)
            {
                Continue(line.Trim(), number);
                return;
            }

            if (_needClass)
            {
                _needClass = false;
                if (line.EndsWith(':') && !line.StartsWith(' '))
                {
                    _className = line[..^1];
                    return;
                }

                Stop(number, "expected a class name");
                return;
            }

            int indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            if (indent < line.Length && line[indent] == '\t')
            {
                Stop(number, "tab indentation");
                return;
            }

            string content = line[indent..].TrimEnd();
            if (content.Length == 0 || content[0] == '#')
            {
                return;
            }

            if (indent > _plainIndent)
            {
                return; // a plain scalar going on over another line
            }

            _plainIndent = int.MaxValue;
            if (content == "-" || content.StartsWith("- ", StringComparison.Ordinal))
            {
                Item(indent, content, number);
            }
            else if (KeyLine().Match(content) is { Success: true } key)
            {
                Key(indent, key, number);
            }
            else
            {
                Stop(number, "not a key or a list item");
            }
        }

        public void End()
        {
            if (!_inDocument)
            {
                return;
            }

            if (_open is not null)
            {
                Leaf(_openPath, _open.ToString(), _openLine);
                _open = null;
            }

            Documents.Add(new YamlDocument(_classId, _className, _fileId, _stripped, _start, _last, _leaves));
            _inDocument = false;
        }

        private void Item(int indent, string content, int number)
        {
            while (_stack.Count > 1 && _stack[^1].Indent > indent)
            {
                _stack.RemoveAt(_stack.Count - 1);
            }

            if (_stack.Count > 1 && _stack[^1].Item && _stack[^1].Indent == indent)
            {
                _stack.RemoveAt(_stack.Count - 1);
            }

            Frame parent = _stack[^1];
            string path = parent.Path + "[" + parent.NextIndex.ToString(CultureInfo.InvariantCulture) + "]";
            parent.NextIndex++;
            _stack.Add(new Frame(indent, path, item: true));

            string rest = content.Length > 1 ? content[2..].TrimStart() : "";
            if (rest.Length == 0)
            {
                return;
            }

            int restIndent = indent + (content.Length - rest.Length);
            if (rest == "-" || rest.StartsWith("- ", StringComparison.Ordinal))
            {
                Stop(number, "nested list item on one line");
            }
            else if (KeyLine().Match(rest) is { Success: true } key)
            {
                Key(restIndent, key, number);
            }
            else
            {
                Value(path, rest, restIndent, number);
            }
        }

        private void Key(int indent, Match key, int number)
        {
            while (_stack.Count > 1 && _stack[^1].Indent >= indent)
            {
                _stack.RemoveAt(_stack.Count - 1);
            }

            Frame parent = _stack[^1];
            string name = key.Groups[1].Value;
            string path = parent.Path.Length == 0 ? name : parent.Path + "." + name;
            string value = key.Groups[2].Success ? key.Groups[2].Value.Trim() : "";
            if (value.Length == 0)
            {
                _stack.Add(new Frame(indent, path, item: false));
            }
            else
            {
                Value(path, value, indent, number);
            }
        }

        private void Value(string path, string value, int indent, int number)
        {
            switch (value[0])
            {
                case '{' when !value.EndsWith('}'):
                case '\'' when !Closed(value, '\''):
                case '"' when !Closed(value, '"'):
                    _open = new StringBuilder(value);
                    _quote = value[0];
                    (_openPath, _openLine) = (path, number);
                    return;
                case '|' or '>':
                    Stop(number, "block scalar");
                    return;
                case '&' or '*' or '!':
                    Stop(number, "anchor, alias or tag");
                    return;
            }

            Leaf(path, value, number);
            _plainIndent = indent;
        }

        private void Continue(string text, int number)
        {
            _open!.Append(' ').Append(text);
            bool done = _quote == '{' ? text.EndsWith('}') : Closed(_open.ToString(), _quote);
            if (done)
            {
                Leaf(_openPath, _open.ToString(), _openLine);
                _open = null;
            }
            else if (_open.Length > 1 << 20)
            {
                _open = null;
                Stop(number, "unterminated value");
            }
        }

        private void Leaf(string path, string raw, int line)
        {
            ObjectRef? reference = raw.StartsWith('{') ? ParseRef(raw) : null;
            _leaves.Add(new YamlLeaf(path, reference is null ? Unquote(raw) : raw, reference, line));
        }

        private void Stop(int number, string reason)
        {
            _stopped = true;
            if (!_reported)
            {
                _reported = true;
                problems.Add($"line {number}: {reason}; rest of the document skipped");
            }
        }
    }

    // Whether a quoted scalar that starts at value[0] ends at the last character.
    private static bool Closed(string value, char quote)
    {
        if (value.Length < 2 || value[^1] != quote)
        {
            return false;
        }

        int i = 1;
        while (i < value.Length)
        {
            char c = value[i];
            if (quote == '\'' && c == '\'' && i + 1 < value.Length && value[i + 1] == '\'')
            {
                i += 2;
            }
            else if (quote == '"' && c == '\\')
            {
                i += 2;
            }
            else if (c == quote)
            {
                return i == value.Length - 1;
            }
            else
            {
                i++;
            }
        }

        return false;
    }

    /// <summary>The text of a quoted scalar; a plain one as it is.</summary>
    internal static string Unquote(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            var text = new StringBuilder(raw.Length);
            for (int i = 1; i < raw.Length - 1; i++)
            {
                if (raw[i] != '\\' || i + 1 >= raw.Length - 1)
                {
                    text.Append(raw[i]);
                    continue;
                }

                char next = raw[++i];
                switch (next)
                {
                    case 'n': text.Append('\n'); break;
                    case 't': text.Append('\t'); break;
                    case 'u' when i + 4 < raw.Length - 1 && int.TryParse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code):
                        text.Append((char)code);
                        i += 4;
                        break;
                    default: text.Append(next); break;
                }
            }

            return text.ToString();
        }

        return raw;
    }

    /// <summary>`{fileID: N, guid: G, type: T}` as a reference; null for any other flow mapping.</summary>
    internal static ObjectRef? ParseRef(string raw)
    {
        if (raw.Length < 2 || raw[0] != '{' || raw[^1] != '}')
        {
            return null;
        }

        long? fileId = null;
        string? guid = null;
        int type = 0;
        foreach (string pair in raw[1..^1].Split(','))
        {
            int colon = pair.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            string name = pair[..colon].Trim();
            string value = pair[(colon + 1)..].Trim();
            switch (name)
            {
                case "fileID" when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id):
                    fileId = id;
                    break;
                case "guid" when value.Length > 0:
                    guid = value;
                    break;
                case "type" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int t):
                    type = t;
                    break;
            }
        }

        return fileId is { } f ? new ObjectRef(f, guid, type) : null;
    }
}
