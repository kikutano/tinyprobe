using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tinyprobe.Core.Definitions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Tinyprobe.Core.Execution;

/// <summary>
/// Turns YAML values into JSON values, resolving <c>${name}</c> placeholders.
/// Plain scalars follow the YAML core schema (<c>true</c>, <c>42</c>, <c>null</c>);
/// quoted and block scalars are always strings. A plain scalar made of a single
/// placeholder takes the variable's value with its type.
/// </summary>
internal static partial class TemplateRenderer
{
    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^\$\{([^}]+)\}$")]
    private static partial Regex LonePlaceholder();

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatPattern();

    public static JsonElement RenderValue(Template template, IVariableScope scope) =>
        JsonValues.Build(writer => Write(writer, template.Node, scope));

    /// <summary>Converts a YAML value without resolving placeholders (environment files, defaults).</summary>
    public static JsonElement ToLiteral(YamlNode node) =>
        JsonValues.Build(writer => Write(writer, node, scope: null));

    /// <summary>Renders a value used inside a text: path, query, header, base URL.</summary>
    public static string RenderText(Template template, IVariableScope scope) =>
        template.Node is YamlScalarNode scalar
            ? Interpolate(scalar.Value ?? string.Empty, scope)
            : JsonValues.ToText(RenderValue(template, scope));

    public static string Interpolate(string text, IVariableScope scope) =>
        Placeholder().Replace(text, match => JsonValues.ToText(Resolve(match.Groups[1].Value, scope)));

    /// <summary>Names of the variables referenced by a value, for validation.</summary>
    public static IEnumerable<string> Placeholders(YamlNode node) => node switch
    {
        YamlScalarNode scalar => Placeholder().Matches(scalar.Value ?? string.Empty).Select(m => m.Groups[1].Value.Trim()),
        YamlSequenceNode sequence => sequence.Children.SelectMany(Placeholders),
        YamlMappingNode mapping => mapping.Children.Values.SelectMany(Placeholders),
        _ => []
    };

    private static JsonElement Resolve(string name, IVariableScope scope)
    {
        name = name.Trim();
        if (Generators.IsGenerated(name))
            return Generators.Generate(name);

        return scope.TryGet(name, out var value)
            ? value
            : throw new UndefinedVariableException(name);
    }

    private static void Write(Utf8JsonWriter writer, YamlNode node, IVariableScope? scope)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                writer.WriteStartObject();
                foreach (var (key, value) in mapping.Children)
                {
                    writer.WritePropertyName(((YamlScalarNode)key).Value ?? string.Empty);
                    Write(writer, value, scope);
                }
                writer.WriteEndObject();
                break;

            case YamlSequenceNode sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Children)
                    Write(writer, item, scope);
                writer.WriteEndArray();
                break;

            case YamlScalarNode scalar:
                WriteScalar(writer, scalar, scope);
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, YamlScalarNode scalar, IVariableScope? scope)
    {
        var text = scalar.Value ?? string.Empty;
        var plain = scalar.Style is ScalarStyle.Plain or ScalarStyle.Any;

        if (scope is not null && Placeholder().IsMatch(text))
        {
            var lone = LonePlaceholder().Match(text);
            if (plain && lone.Success)
                Resolve(lone.Groups[1].Value, scope).WriteTo(writer);
            else
                writer.WriteStringValue(Interpolate(text, scope));
            return;
        }

        if (!plain)
        {
            writer.WriteStringValue(text);
            return;
        }

        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                writer.WriteNullValue();
                return;
            case "true" or "True" or "TRUE":
                writer.WriteBooleanValue(true);
                return;
            case "false" or "False" or "FALSE":
                writer.WriteBooleanValue(false);
                return;
        }

        if ((IntegerPattern().IsMatch(text) || FloatPattern().IsMatch(text))
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            writer.WriteNumberValue(number);
            return;
        }

        writer.WriteStringValue(text);
    }
}

/// <summary>A placeholder whose variable no scope has; the engine names a skipped step that would have saved it.</summary>
public sealed class UndefinedVariableException(string name) : InvalidOperationException($"Variable '{name}' is not defined.")
{
    public string Name { get; } = name;
}
