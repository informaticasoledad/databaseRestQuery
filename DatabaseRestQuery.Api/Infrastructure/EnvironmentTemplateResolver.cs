using System.Text.RegularExpressions;

namespace DatabaseRestQuery.Api.Infrastructure;

public static partial class EnvironmentTemplateResolver
{
    [GeneratedRegex(@"\{\{([A-Za-z_][A-Za-z0-9_]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateRegex();

    public static string ResolveRequired(string value, string context)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = TemplateRegex().Replace(value, match =>
        {
            var variableName = match.Groups[1].Value;
            var variableValue = Environment.GetEnvironmentVariable(variableName);

            if (string.IsNullOrEmpty(variableValue))
            {
                missing.Add(variableName);
                return match.Value;
            }

            return variableValue;
        });

        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Select(x => $"'{x}'"));
            throw new InvalidOperationException($"{context}: faltan variables de entorno requeridas: {names}.");
        }

        return resolved;
    }
}
