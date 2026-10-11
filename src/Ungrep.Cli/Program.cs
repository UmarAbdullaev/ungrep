using System.Text;
using Ungrep.Cli;
using Ungrep.Hooks;

if (args is ["hook", ..])
{
    // Claude Code writes and reads UTF-8; the console code page on Windows may be something else.
    var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    using var input = new StreamReader(Console.OpenStandardInput(), utf8);
    using var output = new StreamWriter(Console.OpenStandardOutput(), utf8);
    return HookCommand.Run(args, input, output, HookHandlers.All);
}

return CliApp.Run(args, Console.Out, Console.Error);
