using System.Text;

namespace AsterLauncher.Core;

public static class ArgumentTokenizer
{
    public static IReadOnlyList<string> Parse(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        var arguments = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var argumentStarted = false;

        for (var index = 0; index < commandLine.Length;)
        {
            if (char.IsWhiteSpace(commandLine[index]) && !inQuotes)
            {
                if (argumentStarted)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    argumentStarted = false;
                }

                index++;
                continue;
            }

            var backslashCount = 0;
            while (index < commandLine.Length && commandLine[index] == '\\')
            {
                backslashCount++;
                index++;
            }

            if (index < commandLine.Length && commandLine[index] == '"')
            {
                current.Append('\\', backslashCount / 2);
                if (backslashCount % 2 == 0)
                {
                    inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('"');
                }

                argumentStarted = true;
                index++;
                continue;
            }

            current.Append('\\', backslashCount);
            if (index < commandLine.Length)
            {
                current.Append(commandLine[index]);
                argumentStarted = true;
                index++;
            }
        }

        if (inQuotes)
        {
            throw new FormatException("参数中存在未闭合的双引号。");
        }

        if (argumentStarted)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    public static string Join(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(Quote));

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character is '"' or '\\'))
        {
            return argument;
        }

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', (backslashes * 2) + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }
}

