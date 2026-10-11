using System.Globalization;

namespace Ungrep.Assets;

/// <summary>
/// What asset-index needs from a `.meta` file (SPEC-asset-index, Meta files): the GUID, whether
/// it describes a folder, and a native asset's main object fileID.
/// </summary>
public sealed record MetaFile(string? Guid, bool Folder, long? MainObjectFileId)
{
    // The importer section starts after the header; the keys that matter come within its first lines.
    private const int ImporterLines = 6;

    /// <exception cref="IOException">The file can't be read.</exception>
    public static MetaFile Read(string fullPath)
    {
        using var reader = new StreamReader(fullPath);
        return Parse(reader);
    }

    public static MetaFile Parse(TextReader text)
    {
        string? guid = null;
        bool folder = false;
        long? main = null;
        int importer = -1;
        while (text.ReadLine() is { } line)
        {
            if (importer >= 0 && ++importer > ImporterLines)
            {
                break;
            }

            if (line.StartsWith("guid: ", StringComparison.Ordinal))
            {
                string value = line[6..].Trim();
                guid = IsGuid(value) ? value : null;
            }
            else if (line.StartsWith("folderAsset: ", StringComparison.Ordinal))
            {
                folder = line[13..].Trim() == "yes";
            }
            else if (line.TrimStart().StartsWith("mainObjectFileID: ", StringComparison.Ordinal)
                && long.TryParse(line.TrimStart()[18..].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long id))
            {
                main = id;
            }
            else if (importer < 0 && line.Length > 1 && line[0] != ' ' && line.EndsWith("Importer:", StringComparison.Ordinal))
            {
                importer = 0;
            }
        }

        return new MetaFile(guid, folder, main);
    }

    /// <summary>32 lowercase hex characters, as Unity writes GUIDs.</summary>
    public static bool IsGuid(string value) =>
        value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
