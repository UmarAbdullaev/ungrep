namespace Ungrep.Core;

/// <summary>
/// Folders holding third-party code checked into the project (SPEC-code-index, Which files are
/// indexed). code-index indexes their declarations only; guards protect them from edits.
/// Prefixes are project-relative, forward slashes, ending in `/`, matched case-insensitively.
/// </summary>
public sealed class VendoredFolders
{
    private readonly string[] _prefixes;

    public VendoredFolders(IEnumerable<string> prefixes)
    {
        _prefixes = prefixes.ToArray();
        foreach (string prefix in _prefixes)
        {
            if (Problem(prefix) is { } problem)
            {
                throw new ArgumentException($"vendored folder '{prefix}': {problem}", nameof(prefixes));
            }
        }
    }

    public static VendoredFolders Default { get; } = new(
    [
        "Assets/Plugins/", "Assets/3rd Party/", "Assets/3rdParty/", "Assets/Third Party/", "Assets/ThirdParty/",
        "Assets/Vendor/", "Assets/AssetStore/", "Packages/",
    ]);

    public IReadOnlyList<string> Prefixes => _prefixes;

    public bool Contains(string path) => _prefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why <paramref name="prefix"/> isn't a valid entry, or null.</summary>
    public static string? Problem(string prefix) =>
        prefix.Length < 2 ? "empty"
        : prefix.Contains('\\') ? "use forward slashes"
        // A drive letter is absolute on every OS: the config is shared by a team on all three.
        : prefix.StartsWith('/') || Path.IsPathRooted(prefix) || (char.IsAsciiLetter(prefix[0]) && prefix[1] == ':') ? "must be project-relative"
        : !prefix.EndsWith('/') ? "must end in '/'"
        : prefix.Split('/').Any(part => part is "." or "..") ? "no '.' or '..' parts"
        : null;
}
