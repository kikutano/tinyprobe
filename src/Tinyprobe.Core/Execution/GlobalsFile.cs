using System.Text;
using System.Text.Json;

namespace Tinyprobe.Core.Execution;

/// <summary>
/// Global variables (spec 0023): the file that keeps them between single calls, given with <c>--globals</c>.
/// JSON, so tools read it without a YAML parser, and a person can still read and edit it:
/// <c>{ "token": { "value": "abc", "from": "login", "at": "2026-11-24T10:12:00Z" } }</c>.
/// A value written by hand without the object (<c>"token": "abc"</c>) is read too.
/// </summary>
public static class GlobalsFile
{
    /// <summary>One global variable: its value, the endpoint that kept it and when (UTC, ISO 8601), when known.</summary>
    public sealed record Entry(string Name, JsonElement Value, string? From, string? At);

    /// <summary>The variables of the file; none when it does not exist yet.</summary>
    /// <exception cref="GlobalsFileException">The file is not a JSON object.</exception>
    public static List<Entry> Load(string path)
    {
        if (!File.Exists(path))
            return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new GlobalsFileException($"{path}: not valid JSON: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new GlobalsFileException($"{path}: must be a JSON object of variables");

            var entries = new List<Entry>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var item = property.Value;
                entries.Add(item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var value)
                    ? new Entry(property.Name, value.Clone(), Text(item, "from"), Text(item, "at"))
                    : new Entry(property.Name, item.Clone(), null, null));
            }
            return entries;
        }
    }

    /// <summary>
    /// Writes <paramref name="kept"/> over the variables already in the file: a variable kept again is replaced
    /// in its place, a new one goes last. The file is replaced at once, so a reader never sees half of it.
    /// </summary>
    public static void Update(string path, IReadOnlyList<Entry> kept)
    {
        if (kept.Count == 0)
            return;

        var entries = Load(path);
        foreach (var entry in kept)
        {
            int index = entries.FindIndex(e => e.Name == entry.Name);
            if (index >= 0)
                entries[index] = entry;
            else
                entries.Add(entry);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            foreach (var entry in entries)
            {
                writer.WriteStartObject(entry.Name);
                writer.WritePropertyName("value");
                entry.Value.WriteTo(writer);
                if (entry.From is not null)
                    writer.WriteString("from", entry.From);
                if (entry.At is not null)
                    writer.WriteString("at", entry.At);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + ".tmp";
        File.WriteAllText(temporary, Encoding.UTF8.GetString(stream.ToArray()) + "\n");
        File.Move(temporary, full, overwrite: true);
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A globals file that cannot be read.</summary>
public sealed class GlobalsFileException(string message) : Exception(message);
