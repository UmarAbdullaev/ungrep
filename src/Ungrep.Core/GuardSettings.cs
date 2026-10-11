using System.Text.RegularExpressions;

namespace Ungrep.Core;

/// <summary>What a guard does when it fires (`guards.*` in ungrep.json; SPEC-guards, Config).</summary>
public enum GuardMode
{
    Warn,
    Deny,
    Off,
}

/// <summary>A runtime-lookup lint pattern: a regular expression and what to tell Claude when added code matches it.</summary>
public sealed record LintPattern(Regex Pattern, string Message);

/// <summary>
/// The `guards` section: a mode per case and the lint patterns. Every case warns by default; the
/// lint has no patterns until the user adds some, so it stays silent (SPEC-guards).
/// </summary>
public sealed class GuardSettings
{
    /// <summary>The cases with a mode besides the lint, in the order the spec lists them.</summary>
    public static IReadOnlyList<string> Cases { get; } = ["field-removed", "serialize-reference", "meta", "vendored"];

    public static GuardSettings Default { get; } = new(
        Cases.ToDictionary(c => c, _ => GuardMode.Warn, StringComparer.Ordinal), GuardMode.Warn, []);

    private readonly IReadOnlyDictionary<string, GuardMode> _modes;

    internal GuardSettings(IReadOnlyDictionary<string, GuardMode> modes, GuardMode lintMode, IReadOnlyList<LintPattern> lintPatterns)
    {
        _modes = modes;
        LintMode = lintMode;
        LintPatterns = lintPatterns;
    }

    public GuardMode LintMode { get; }

    public IReadOnlyList<LintPattern> LintPatterns { get; }

    /// <exception cref="ArgumentException">Not a case with a mode.</exception>
    public GuardMode ModeOf(string guardCase) =>
        guardCase == "lint" ? LintMode
        : _modes.TryGetValue(guardCase, out GuardMode mode) ? mode
        : throw new ArgumentException("unknown guard: " + guardCase, nameof(guardCase));

    internal Dictionary<string, GuardMode> ModesCopy() => new(_modes, StringComparer.Ordinal);

    internal static GuardMode ParseMode(string setting, string? text) =>
        text switch
        {
            "warn" => GuardMode.Warn,
            "deny" => GuardMode.Deny,
            "off" => GuardMode.Off,
            _ => throw new FormatException($"{setting} must be \"warn\", \"deny\" or \"off\""),
        };
}
