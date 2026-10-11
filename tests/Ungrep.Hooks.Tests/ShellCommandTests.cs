namespace Ungrep.Hooks.Tests;

/// <summary>
/// Reading moves, copies and removes out of Bash and PowerShell command lines (SPEC-guards,
/// Parsing a shell command): quoting, chains, here-documents, flags, and what stays unreported.
/// </summary>
public sealed class ShellCommandTests
{
    [Theory]
    [InlineData("mv Assets/A.cs Assets/B.cs", "Move mv [Assets/A.cs] -> Assets/B.cs")]
    [InlineData("mv -f Assets/A.cs Assets/A.cs.meta Assets/Sub/", "Move mv [Assets/A.cs, Assets/A.cs.meta] -> Assets/Sub/")]
    [InlineData("git mv \"Assets/My Art/A.png\" 'Assets/Art/A.png'", "Move git mv [Assets/My Art/A.png] -> Assets/Art/A.png")]
    [InlineData("git rm --cached Assets/A.cs.meta", "Remove git rm [Assets/A.cs.meta] -> -")]
    [InlineData("cp -r Assets/Art Assets/Art2", "Copy cp [Assets/Art] -> Assets/Art2 recursive")]
    [InlineData("cp -a Assets/Art /tmp/backup", "Copy cp [Assets/Art] -> /tmp/backup recursive")]
    [InlineData("cp --verbose Assets/A.png Assets/B.png", "Copy cp [Assets/A.png] -> Assets/B.png")]
    [InlineData("rm -rf Assets/Old Assets/Old.meta", "Remove rm [Assets/Old, Assets/Old.meta] -> - recursive")]
    [InlineData("rm -- -odd.cs", "Remove rm [-odd.cs] -> -")]
    [InlineData("sudo mv a b", "Move mv [a] -> b")]
    [InlineData("LANG=C mv a b", "Move mv [a] -> b")]
    [InlineData("mv a b > log.txt 2>&1", "Move mv [a] -> b")]
    [InlineData("mv a b 2>/dev/null", "Move mv [a] -> b")]
    [InlineData("mkdir -p x && mv a x/ || echo failed; rm c | cat", "Move mv [a] -> x/ | Remove rm [c] -> -")]
    [InlineData("mv a \\\n  b", "Move mv [a] -> b")]
    [InlineData("mv my\\ file.png b.png", "Move mv [my file.png] -> b.png")]
    [InlineData("cd Assets && mv a b", "ChangeDirectory cd [] -> Assets | Move mv [a] -> b")]
    [InlineData("cd && cd - && popd", "ChangeDirectory cd [] -> - | ChangeDirectory cd [] -> - | ChangeDirectory popd [] -> -")]
    [InlineData("cd $HOME", "ChangeDirectory cd [] -> -")]
    [InlineData("echo mv a b # rm c", "")]
    [InlineData("cat <<'EOF' > notes.md\nrm Assets/A.cs.meta\nEOF\nrm b", "Remove rm [b] -> -")]
    [InlineData("git commit -m \"$(cat <<EOF\nmv a b\nEOF\n)\"", "")]
    [InlineData("mv $SRC Assets/", "")]
    [InlineData("mv Assets/*.png Assets/Art/", "")]
    [InlineData("rm ~/x", "")]
    [InlineData("cp {a,b} c", "")]
    [InlineData("mv -t Assets/Art a.png", "")]
    [InlineData("mv a", "")]
    [InlineData("rm -f", "")]
    public void Bash(string command, string expected) =>
        Assert.Equal(expected, Describe(ShellCommand.Operations(command, powerShell: false)));

    [Theory]
    [InlineData("Move-Item -Path Assets/A.cs -Destination Assets/B.cs", "Move Move-Item [Assets/A.cs] -> Assets/B.cs")]
    [InlineData("Move-Item Assets/A.cs,Assets/A.cs.meta Assets/Sub", "Move Move-Item [Assets/A.cs, Assets/A.cs.meta] -> Assets/Sub")]
    [InlineData("mi -LiteralPath 'Assets/My Art/A.png' -Destination \"Assets/Art\" -Force", "Move mi [Assets/My Art/A.png] -> Assets/Art")]
    [InlineData("Rename-Item Assets/Art/A.png B.png", "Move Rename-Item [Assets/Art/A.png] -> Assets/Art/B.png")]
    [InlineData("Rename-Item -Path Assets\\Art\\A.png -NewName B.png", "Move Rename-Item [Assets\\Art\\A.png] -> Assets\\Art\\B.png")]
    [InlineData("ren A.png B.png", "Move ren [A.png] -> B.png")]
    [InlineData("Rename-Item a.png Art/b.png", "")]
    [InlineData("Copy-Item -Recurse Assets/Art Assets/Art2", "Copy Copy-Item [Assets/Art] -> Assets/Art2 recursive")]
    [InlineData("Copy-Item Assets/Art Assets/Art2 -Filter *.png", "Copy Copy-Item [Assets/Art] -> Assets/Art2")]
    [InlineData("Remove-Item -Path Assets/A.cs.meta -ErrorAction SilentlyContinue", "Remove Remove-Item [Assets/A.cs.meta] -> -")]
    [InlineData("del Assets/A.cs.meta; Set-Location Assets; ri B.cs", "Remove del [Assets/A.cs.meta] -> - | ChangeDirectory Set-Location [] -> Assets | Remove ri [B.cs] -> -")]
    [InlineData("Move-Item `\n  a b", "Move Move-Item [a] -> b")]
    [InlineData("Move-Item $src Assets/", "")]
    public void PowerShell(string command, string expected) =>
        Assert.Equal(expected, Describe(ShellCommand.Operations(command, powerShell: true)));

    private static string Describe(IReadOnlyList<FileOp> ops) =>
        string.Join(" | ", ops.Select(o => $"{o.Kind} {o.Verb} [{string.Join(", ", o.Sources)}] -> {o.Destination ?? "-"}{(o.Recursive ? " recursive" : "")}"));
}
