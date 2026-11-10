using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tinyprobe.Core.Definitions;
using YamlDotNet.RepresentationModel;

namespace Tinyprobe.Core.Execution;

/// <summary>
/// What a run that stopped at <c>--to</c> leaves for the next steps (spec 0022): the values saved, the workflow
/// variables already resolved and the cookies. tinyprobe never stores it: it is given in <c>run_finished</c> and
/// taken back by <c>run --resume</c>, which continues the same run at the step after <see cref="After"/>.
/// </summary>
public sealed record RunState
{
    /// <summary>The format of the state's JSON; a state of a newer format needs a newer tinyprobe.</summary>
    public const int Format = 1;

    /// <summary>The workflow file, full path.</summary>
    public required string File { get; init; }
    /// <summary>The environment file of the run (<c>-e</c>), full path, or null.</summary>
    public string? EnvFile { get; init; }
    /// <summary>The index of the last step that ran.</summary>
    public required int After { get; init; }
    /// <summary><see cref="Fingerprint"/> of the steps up to <see cref="After"/>.</summary>
    public required string Steps { get; init; }
    public IReadOnlyList<KeyValuePair<string, JsonElement>> Saved { get; init; } = [];
    /// <summary>The workflow <c>variables</c> resolved by the run: a generated value must stay the same after it.</summary>
    public IReadOnlyList<KeyValuePair<string, JsonElement>> Variables { get; init; } = [];
    /// <summary>The cookies of the jar; a domain cookie (sent to subdomains too) has a domain starting with a dot.</summary>
    public IReadOnlyList<Cookie> Cookies { get; init; } = [];

    /// <summary>
    /// A hash of the steps up to <paramref name="after"/> as they are loaded: each step as written, the arguments
    /// of the <c>use</c> it comes from, the items of a <c>for_each</c> data file and the endpoint it calls (with its
    /// auth set). An edit anywhere else, such as the next step, keeps it.
    /// </summary>
    public static string Fingerprint(WorkflowDefinition workflow, int after)
    {
        var text = new StringBuilder();
        for (int index = 0; index <= after && index < workflow.Steps.Count; index++)
        {
            var step = workflow.Steps[index];
            text.Append("step ").Append(index).Append('\n');
            Node(text, step.Written);
            for (var use = step.Sequence; use is not null; use = use.Parent)
            {
                text.Append("use ").Append(use.Name).Append(' ').Append(use.Item).Append('\n');
                foreach (var (name, value) in use.Arguments)
                    Named(text, name, value);
            }
            if (step is CallStep call)
            {
                foreach (var item in call.ForEach?.Items ?? [])
                {
                    text.Append("item\n");
                    foreach (var (name, value) in item)
                        Named(text, name, value);
                }
                Endpoint(text, call.Endpoint);
            }
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void Endpoint(StringBuilder text, EndpointDefinition endpoint)
    {
        text.Append("endpoint ").Append(endpoint.Name).Append(' ').Append(endpoint.Method).Append('\n');
        Template(text, endpoint.BaseUrl);
        Template(text, endpoint.Path);
        text.Append("query ").AppendJoin(',', endpoint.Query).Append('\n');
        foreach (var (name, value) in endpoint.Headers)
            Named(text, name, value);
        Template(text, endpoint.Body);
        foreach (var field in (endpoint.Form ?? []).Concat(endpoint.Multipart ?? []))
        {
            text.Append("field ").Append(field.Name).Append('\n');
            foreach (var template in new[] { field.Value, field.File, field.FileName, field.ContentType })
                Template(text, template);
        }
        if (endpoint.BasicAuth is { } basic)
        {
            text.Append("basic\n");
            Template(text, basic.Username);
            Template(text, basic.Password);
        }
        if (endpoint.OAuth2 is { } oauth2)
        {
            text.Append("oauth2 ").Append(oauth2.ClientAuth).Append('\n');
            foreach (var template in new[] { oauth2.TokenUrl, oauth2.ClientId, oauth2.ClientSecret, oauth2.Scope })
                Template(text, template);
            foreach (var (name, value) in oauth2.Form)
                Named(text, name, value);
        }
        foreach (var (name, value) in endpoint.Params ?? new Dictionary<string, Template?>())
        {
            text.Append("param ").Append(name).Append('\n');
            Template(text, value);
        }
    }

    private static void Named(StringBuilder text, string name, Template value)
    {
        text.Append("= ").Append(name).Append('\n');
        Template(text, value);
    }

    private static void Template(StringBuilder text, Template? template)
    {
        if (template is null)
            text.Append("~\n");
        else
            Node(text, template.Node);
    }

    /// <summary>
    /// A YAML node in one unambiguous form: scalars with their length, and whether they are plain, since
    /// <c>42</c> and <c>"42"</c> are different values.
    /// </summary>
    private static void Node(StringBuilder text, YamlNode? node)
    {
        switch (node)
        {
            case null:
                text.Append('~');
                break;
            case YamlScalarNode scalar:
                var value = scalar.Value ?? "";
                text.Append(scalar.Style is YamlDotNet.Core.ScalarStyle.Plain or YamlDotNet.Core.ScalarStyle.Any ? 'p' : 'q')
                    .Append(value.Length).Append(':').Append(value);
                break;
            case YamlSequenceNode sequence:
                text.Append('[');
                foreach (var child in sequence.Children)
                    Node(text, child);
                text.Append(']');
                break;
            case YamlMappingNode mapping:
                text.Append('{');
                foreach (var (key, child) in mapping.Children)
                {
                    Node(text, key);
                    Node(text, child);
                }
                text.Append('}');
                break;
            default:
                text.Append(node.ToString());
                break;
        }
        text.Append('\n');
    }

    /// <summary>The state as <c>run_finished</c> gives it.</summary>
    public void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteNumber("state", Format);
        w.WriteString("file", File);
        if (EnvFile is null)
            w.WriteNull("env_file");
        else
            w.WriteString("env_file", EnvFile);
        w.WriteNumber("after", After);
        w.WriteString("steps", Steps);
        WriteValues(w, "saved", Saved);
        WriteValues(w, "variables", Variables);
        w.WriteStartArray("cookies");
        foreach (var cookie in Cookies)
        {
            w.WriteStartObject();
            w.WriteString("name", cookie.Name);
            w.WriteString("value", cookie.Value);
            w.WriteString("domain", cookie.Domain);
            w.WriteString("path", cookie.Path);
            if (cookie.Expires == DateTime.MinValue)
                w.WriteNull("expires");
            else
                w.WriteString("expires", cookie.Expires.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            w.WriteBoolean("secure", cookie.Secure);
            w.WriteBoolean("http_only", cookie.HttpOnly);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteValues(Utf8JsonWriter w, string name, IReadOnlyList<KeyValuePair<string, JsonElement>> values)
    {
        w.WriteStartObject(name);
        foreach (var (key, value) in values)
        {
            w.WritePropertyName(key);
            value.WriteTo(w);
        }
        w.WriteEndObject();
    }

    /// <summary>Reads a state given to <c>--resume</c>; a <see cref="RunStateException"/> says what is wrong.</summary>
    public static RunState Parse(string json)
    {
        const string NotAState = "--resume: not a tinyprobe state (expected the \"state\" of a run_finished event)";
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new RunStateException(NotAState);
        }

        try
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("state", out var format) || format.ValueKind != JsonValueKind.Number)
                throw new RunStateException(NotAState);
            if (format.GetInt32() > Format)
                throw new RunStateException("--resume: this state needs a newer tinyprobe");

            var envFile = root.GetProperty("env_file");
            return new RunState
            {
                File = root.GetProperty("file").GetString()!,
                EnvFile = envFile.ValueKind == JsonValueKind.Null ? null : envFile.GetString(),
                After = root.GetProperty("after").GetInt32(),
                Steps = root.GetProperty("steps").GetString()!,
                Saved = Values(root.GetProperty("saved")),
                Variables = Values(root.GetProperty("variables")),
                Cookies = root.GetProperty("cookies").EnumerateArray().Select(ReadCookie).ToList()
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new RunStateException(NotAState);
        }

        static List<KeyValuePair<string, JsonElement>> Values(JsonElement values) =>
            values.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value.Clone())).ToList();

        static Cookie ReadCookie(JsonElement c)
        {
            var expires = c.GetProperty("expires");
            return new Cookie(c.GetProperty("name").GetString()!, c.GetProperty("value").GetString(), c.GetProperty("path").GetString(), c.GetProperty("domain").GetString())
            {
                Expires = expires.ValueKind == JsonValueKind.Null
                    ? DateTime.MinValue
                    : DateTime.Parse(expires.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Secure = c.GetProperty("secure").GetBoolean(),
                HttpOnly = c.GetProperty("http_only").GetBoolean()
            };
        }
    }

    /// <summary>
    /// The cookies of a run's jar, a domain cookie written <c>.example.com</c>: the jar shows a cookie of
    /// <c>Domain=example.com</c> and one of the host <c>example.com</c> the same way, so it is asked whether a
    /// subdomain would get the cookie.
    /// </summary>
    internal static List<Cookie> CookiesOf(CookieContainer jar) =>
        jar.GetAllCookies().Where(c => !c.Expired).Select(c =>
        {
            var probe = new Uri($"https://tinyprobe-probe.{c.Domain.TrimStart('.')}{c.Path}");
            bool wide = jar.GetCookies(probe).Any(sent => sent.Name == c.Name && sent.Domain == c.Domain);
            return new Cookie(c.Name, c.Value, c.Path, wide ? "." + c.Domain.TrimStart('.') : c.Domain)
            {
                Expires = c.Expires, Secure = c.Secure, HttpOnly = c.HttpOnly
            };
        }).ToList();

    /// <summary>Puts the state's cookies back in a run's jar.</summary>
    internal void RestoreCookies(CookieContainer jar)
    {
        foreach (var cookie in Cookies)
        {
            // A cookie set without a Domain attribute is sent to its host only: added for a URL, it stays so.
            if (cookie.Domain.StartsWith('.'))
                jar.Add(cookie);
            else
                jar.Add(new Uri($"{(cookie.Secure ? "https" : "http")}://{cookie.Domain}{cookie.Path}"),
                    new Cookie(cookie.Name, cookie.Value, cookie.Path) { Expires = cookie.Expires, Secure = cookie.Secure, HttpOnly = cookie.HttpOnly });
        }
    }
}

/// <summary>A state given to <c>--resume</c> that cannot be used; the message is printed as it is.</summary>
public sealed class RunStateException(string message) : Exception(message);
