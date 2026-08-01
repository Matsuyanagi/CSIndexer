namespace CsIndex.Cli;

internal sealed class CliUsageException(string message) : Exception(message);

internal sealed class CliArguments
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "help", "rebuild", "verbose", "diagnostics", "exclude-generated", "only-generated",
        "require-single", "all-profiles", "async-involved", "short-names", "exclude-lambda-calls",
    };

    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = [];

    public static CliArguments Parse(IEnumerable<string> arguments)
    {
        var result = new CliArguments();
        using var enumerator = arguments.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var token = enumerator.Current;
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positionals.Add(token);
                continue;
            }

            var option = token[2..];
            var equals = option.IndexOf('=');
            var name = equals < 0 ? option : option[..equals];
            if (name.Length == 0)
            {
                throw new CliUsageException("Option name cannot be empty.");
            }

            if (Flags.Contains(name))
            {
                if (equals >= 0)
                {
                    throw new CliUsageException($"Flag --{name} does not take a value.");
                }

                result.Add(name, "true");
                continue;
            }

            string value;
            if (equals >= 0)
            {
                value = option[(equals + 1)..];
            }
            else
            {
                if (!enumerator.MoveNext())
                {
                    throw new CliUsageException($"Option --{name} requires a value.");
                }

                value = enumerator.Current;
            }

            if (value.Length == 0)
            {
                throw new CliUsageException($"Option --{name} requires a non-empty value.");
            }

            result.Add(name, value);
        }

        return result;
    }

    public bool HasFlag(string name) => _options.ContainsKey(name);

    public string? GetSingle(string name)
    {
        if (!_options.TryGetValue(name, out var values))
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new CliUsageException($"Option --{name} can be specified only once.");
        }

        return values[0];
    }

    public IReadOnlyList<string> GetMany(string name) =>
        _options.TryGetValue(name, out var values) ? values : [];

    public void EnsureOnly(params string[] names)
    {
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        var unknown = _options.Keys.Where(name => !allowed.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new CliUsageException($"Unknown option(s): {string.Join(", ", unknown.Select(name => $"--{name}"))}");
        }
    }

    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out var values))
        {
            values = [];
            _options[name] = values;
        }

        values.Add(value);
    }
}
