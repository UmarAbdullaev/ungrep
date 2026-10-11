using System.Text;

namespace Ungrep.Query;

/// <summary>
/// Builds an answer line by line within a token budget (SPEC-query, Budget): a token is taken as
/// 4 UTF-8 bytes, lines go in whole, and a cut answer ends with a `more` line naming a larger budget.
/// A paged answer (SPEC-query, Nothing out of reach) skips its first lines and, cut at the largest
/// budget, names the line the next page starts after.
/// </summary>
public sealed class Answer
{
    public const int MinBudget = 200;
    public const int MaxBudget = 8000;

    // Room kept for the `more` line, so a cut answer stays within the budget too.
    private const int MoreReserve = 120;

    private readonly StringBuilder _text = new();
    private readonly int _budget;
    private readonly int? _from;
    private readonly List<string> _footer = [];
    private int _limit;
    private int _bytes;
    private int _offered;
    private int _shown;

    /// <summary><paramref name="from"/>: lines 1 to <paramref name="from"/> are skipped; null when the command does not page.</summary>
    public Answer(int budget, int? from = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, MinBudget);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(budget, MaxBudget);
        ArgumentOutOfRangeException.ThrowIfNegative(from ?? 0);
        _budget = budget;
        _from = from;
        _limit = budget * 4 - MoreReserve;
    }

    /// <summary>True once a line didn't fit; later lines are dropped.</summary>
    public bool Cut { get; private set; }

    /// <summary>How many lines were offered after the cut and left out, for `more` lines that say so.</summary>
    public int Dropped { get; private set; }

    /// <summary>Adds <paramref name="line"/> if it fits; the first line shown always does.</summary>
    public bool Add(string line)
    {
        if (++_offered <= (_from ?? 0))
        {
            return false;
        }

        int bytes = Encoding.UTF8.GetByteCount(line) + 1;
        if (Cut || (_bytes > 0 && _bytes + bytes > _limit))
        {
            Cut = true;
            Dropped++;
            return false;
        }

        _text.Append(line).Append('\n');
        _bytes += bytes;
        _shown++;
        return true;
    }

    public void AddRange(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Add(line);
        }
    }

    /// <summary>
    /// A line printed last, after any `more` line, whatever the budget cut; its room is kept from
    /// the budget, so call it before adding lines.
    /// </summary>
    public void Footer(string line)
    {
        _footer.Add(line);
        _limit -= Encoding.UTF8.GetByteCount(line) + 1;
    }

    /// <summary>
    /// The text; when cut, followed by `  more  &lt;command&gt; --budget &lt;2 × budget&gt;`, which says
    /// how many lines were left out when <paramref name="countDropped"/> is set. A paged answer cut
    /// at the largest budget names `--budget 8000 --from &lt;next line&gt;`.
    /// </summary>
    public string Finish(string command, bool countDropped = false)
    {
        string from = _from is > 0 ? " --from " + _from.Value : "";
        if (_from is > 0 && _shown == 0)
        {
            _text.Append("(nothing past line ").Append(_from.Value).Append(": the answer has ").Append(_offered).Append(" lines)\n");
        }

        if (Cut && _budget < MaxBudget)
        {
            _text.Append("  more  ");
            if (countDropped)
            {
                _text.Append(Dropped).Append(" more lines: ");
            }

            _text.Append(command).Append(from).Append(" --budget ").Append(Math.Min(_budget * 2, MaxBudget)).Append('\n');
        }
        else if (Cut && _from != null)
        {
            _text.Append("  more  ");
            if (countDropped)
            {
                _text.Append(Dropped).Append(" more lines: ");
            }

            _text.Append(command).Append(" --budget ").Append(MaxBudget).Append(" --from ").Append(_from.Value + _shown).Append('\n');
        }
        else if (Cut)
        {
            _text.Append("  more  (cut at the largest budget; ask a narrower question)\n");
        }

        foreach (string line in _footer)
        {
            _text.Append(line).Append('\n');
        }

        return _text.ToString();
    }

    /// <summary>Rows as lines, each column padded to its widest cell; trailing blanks trimmed.</summary>
    public static IEnumerable<string> Table(IReadOnlyList<string[]> rows, string indent = "")
    {
        if (rows.Count == 0)
        {
            yield break;
        }

        int columns = rows.Max(r => r.Length);
        int[] widths = new int[columns];
        foreach (string[] row in rows)
        {
            for (int i = 0; i < row.Length - 1; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        foreach (string[] row in rows)
        {
            var line = new StringBuilder(indent);
            for (int i = 0; i < row.Length; i++)
            {
                line.Append(i < row.Length - 1 ? row[i].PadRight(widths[i] + 2) : row[i]);
            }

            yield return line.ToString().TrimEnd();
        }
    }
}
