using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace UnityGraph.Query;

/// <summary>
/// `--json` answers (SPEC-query, `outline` and `impact`): compact UTF-8 JSON that stays within the
/// answer budget by dropping items from the end, the way text answers drop lines.
/// </summary>
internal static class JsonAnswer
{
    private static readonly JsonWriterOptions _options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The document <paramref name="write"/> produces for the largest item count up to
    /// <paramref name="total"/> that fits <paramref name="budget"/> tokens (4 bytes each). With no
    /// item fitting, the document has zero items: the header fields alone, whatever their size.
    /// </summary>
    public static string Fit(int total, int budget, Action<Utf8JsonWriter, int> write)
    {
        int limit = budget * 4;
        string full = Build(total, write);
        if (Encoding.UTF8.GetByteCount(full) <= limit)
        {
            return full;
        }

        int low = 0;
        int high = total - 1;
        string best = Build(0, write);
        while (low <= high)
        {
            int middle = (low + high) / 2;
            string candidate = Build(middle, write);
            if (Encoding.UTF8.GetByteCount(candidate) <= limit)
            {
                best = candidate;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    /// <summary>`{"error": first line, "lines": the rest}` for an answer that is not the document.</summary>
    public static string Error(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return Build(0, (w, _) =>
        {
            w.WriteStartObject();
            w.WriteString("error", lines.Length > 0 ? lines[0] : "no answer");
            w.WriteStartArray("lines");
            foreach (string line in lines.Skip(1))
            {
                w.WriteStringValue(line.Trim());
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private static string Build(int items, Action<Utf8JsonWriter, int> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, _options))
        {
            write(writer, items);
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
