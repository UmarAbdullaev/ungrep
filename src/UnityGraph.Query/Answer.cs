using System.Text;

namespace UnityGraph.Query;

/// <summary>
/// Builds an answer line by line within a token budget (SPEC-query, Budget): a token is taken as
/// 4 UTF-8 bytes, lines go in whole, and a cut answer ends with a `more` line naming a larger budget.
/// </summary>
public sealed class Answer
{
    public const int MinBudget = 200;
    public const int MaxBudget = 8000;

    // Room kept for the `more` line, so a cut answer stays within the budget too.
    private const int MoreReserve = 120;

    private readonly StringBuilder _text = new();
    private readonly int _budget;
    private readonly int _limit;
    private int _bytes;

    public Answer(int budget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, MinBudget);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(budget, MaxBudget);
        _budget = budget;
        _limit = budget * 4 - MoreReserve;
    }

    /// <summary>True once a line didn't fit; later lines are dropped.</summary>
    public bool Cut { get; private set; }

    /// <summary>How many lines were offered after the cut and left out, for `more` lines that say so.</summary>
    public int Dropped { get; private set; }

    /// <summary>Adds <paramref name="line"/> if it fits; the first line always does.</summary>
    public bool Add(string line)
    {
        int bytes = Encoding.UTF8.GetByteCount(line) + 1;
        if (Cut || (_bytes > 0 && _bytes + bytes > _limit))
        {
            Cut = true;
            Dropped++;
            return false;
        }

        _text.Append(line).Append('\n');
        _bytes += bytes;
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
    /// The text; when cut, followed by `  more  &lt;command&gt; --budget &lt;2 × budget&gt;`, which says
    /// how many lines were left out when <paramref name="countDropped"/> is set.
    /// </summary>
    public string Finish(string command, bool countDropped = false)
    {
        if (Cut && _budget < MaxBudget)
        {
            _text.Append("  more  ");
            if (countDropped)
            {
                _text.Append(Dropped).Append(" more lines: ");
            }

            _text.Append(command).Append(" --budget ").Append(Math.Min(_budget * 2, MaxBudget)).Append('\n');
        }
        else if (Cut)
        {
            _text.Append("  more  (cut at the largest budget; ask a narrower question)\n");
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
