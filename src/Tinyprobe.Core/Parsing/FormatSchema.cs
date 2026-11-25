using System.Text;
using System.Text.Json;

namespace Tinyprobe.Core.Parsing;

/// <summary>
/// <c>tinyprobe schema</c>: a JSON Schema (draft 2020-12) of API files and workflows, for editors (completion and
/// hover) and agents. The keys come from the arrays the loader validates with, so the schema cannot list a key the
/// loader rejects; every key needs a description here. The loader stays the validator: the schema does not know
/// imports, endpoint names, params, placeholders or JSONPath. See docs/specs/0006-completion-and-hover.md.
/// </summary>
public static class FormatSchema
{
    public const string Id = "https://raw.githubusercontent.com/kikutano/tinyprobe/main/docs/schema/tinyprobe.schema.json";

    private static readonly Dictionary<string, string> Descriptions = new()
    {
        // API file and workflow
        ["file.name"] = "Name of the workflow, shown in the report. Default: the file name.",
        ["file.description"] = "What the workflow tests, shown in the report.",
        ["file.import"] = "API files whose endpoints the steps can call, relative to this file: one path or a list. An item `billing: ../api/billing.yaml` names the import: its endpoints are called `billing.login`.",
        ["file.variables"] = "Default variables of the workflow, `name: value`. Each is resolved once per run and may use the environment, other variables and generated values (`${$uuid}`). The environment file overrides them.",
        ["file.settings"] = "`timeout` (seconds per request) and `max_retries` (retries on network errors and timeouts).",
        ["file.sequence"] = "A sequence file: the steps that a workflow runs with `use:` (a login, a shared setup), written as in `steps`.",
        ["file.params"] = "A sequence's params, passed by `use` with `with`: `name: default`, or `name:` for a required one. Inside the sequence each one is a variable.",
        ["file.steps"] = "The steps, run in order. A step `call`s an endpoint or `sleep`s; the workflow stops at the first failing step.",
        ["file.base_url"] = "Prepended to every `path` of this file, usually `${baseUrl}` from the environment file.",
        ["file.headers"] = "Headers sent by every endpoint of this file, `Name: value`.",
        ["file.auth"] = "Named auth sets: headers, such as `bearer: { Authorization: \"Bearer ${token}\" }`, and at most one of `basic` and `oauth2`. An endpoint picks them with `auth:`.",
        ["auth_set.basic"] = "Basic auth: `username` and `password`; tinyprobe sends `Authorization: Basic` with their base64.",
        ["auth_set.oauth2"] = "OAuth2 client credentials: tinyprobe gets a token from `token_url` on first use, reuses it for the run until it expires, and sends `Authorization: Bearer <token>`.",
        ["basic.username"] = "The user name. May be a placeholder. Cannot contain `:`.",
        ["basic.password"] = "The password. May be a placeholder, e.g. `${password}` from the env file.",
        ["oauth2.token_url"] = "The token endpoint, a full URL such as `${authUrl}/oauth/token`.",
        ["oauth2.client_id"] = "The client id.",
        ["oauth2.client_secret"] = "The client secret: a placeholder, with the value in `env.local.yaml`.",
        ["oauth2.scope"] = "Optional: a scope, or a list of scopes, sent joined with spaces.",
        ["oauth2.client_auth"] = "How the client id and secret are sent: `basic` (an `Authorization: Basic` header, the default) or `form` (fields of the body).",
        ["oauth2.form"] = "Other fields of the token request, such as `audience` (Auth0) or `resource` (Azure AD).",
        ["file.endpoints"] = "The endpoints, by name. Steps call them like functions: `call: <name>`, with arguments in `with`.",

        // endpoint
        ["endpoint.method"] = "HTTP method. Default: GET.",
        ["endpoint.path"] = "Path, after the file's `base_url`; may contain placeholders, such as `/users/${id}`.",
        ["endpoint.query"] = "Names of the query parameters. Each one is sent only when the step passes it in `with`.",
        ["endpoint.headers"] = "Headers of this endpoint, `Name: value`, after the file's headers and auth sets.",
        ["endpoint.auth"] = "The auth set to send, or a list of them (`[bearer, apikey]`).",
        ["endpoint.body"] = "The request body: a YAML value sent as JSON, a string sent as is, or a single `${...}` placeholder.",
        ["endpoint.form"] = "An `application/x-www-form-urlencoded` body, `field: value`, encoded by tinyprobe. A list sends the field once per value. Instead of `body`.",
        ["endpoint.multipart"] = "A `multipart/form-data` body (file uploads), `field: value` or `field: { file: path }`. A list sends the field once per item. Instead of `body`.",
        ["endpoint.params"] = "The arguments of the endpoint, `name: default`, or `name:` for a required one. Steps pass them in `with`; they never come from global variables.",
        ["endpoint.globals"] = "Values of a 2xx response saved as global variables for the next single calls, `variable: <JSONPath>` or `variable: { cookie: name }`. Used only with `--globals <file>`.",

        // multipart file
        ["multipart_file.file"] = "The path of the file to send, relative to the file tinyprobe was given (the workflow, or the API file of a `call`). May be a placeholder.",
        ["multipart_file.filename"] = "The file name sent to the server. Default: the name of the file.",
        ["multipart_file.content_type"] = "The type of the file. Default: from its extension (`.png` is `image/png`), otherwise `application/octet-stream`.",

        // step
        ["step.name"] = "Shown in the report. Default: the call and its path and query arguments.",
        ["step.call"] = "The endpoint to call, by name.",
        ["step.use"] = "Run the steps of a sequence file here, relative to this file (`../sequences/log-in.yaml`). `with` passes its params; its saved values are visible to the next steps.",
        ["step.with"] = "Arguments of the endpoint, `name: value`. Values may use variables, such as `id: ${userId}`.",
        ["step.for_each"] = "Repeat the call once per item: a list of mappings whose keys are arguments (like `with`) and variables of the step, a variable holding such a list (`${users}`), or a data file (`{ file: ../data/cases.csv }`, CSV or JSON). Every item runs; the step passes when all passed.",
        ["for_each_file.file"] = "A JSON list of objects, or a CSV file whose first line names the columns, relative to the workflow.",
        ["step.expect"] = "What the response must be: `status`, `headers`, `cookies`, `body` (by JSONPath) and `duration`. Without `status`, any 2xx passes.",
        ["step.until"] = "Polling: repeat the call until these conditions pass, every `every`, at most `timeout`. Then `expect` is checked on the last response.",
        ["step.save"] = "Values of the response for the next steps, `variable: <JSONPath>` or `variable: { cookie: name }`, saved after every check passed.",
        ["save_cookie.cookie"] = "The name of a cookie in the jar after the response, for the request's URL. Its value is saved.",
        ["step.sleep"] = "A step of its own that waits, such as `500ms` or `2s`.",

        // expect and until
        ["expect.status"] = "Expected status code, or a list of accepted ones (`[200, 204]`). In a `for_each` step it may be a placeholder (`${status}`) given by each item.",
        ["expect.headers"] = "Checks on response headers, by name (case-insensitive): a value, or conditions such as `{ exists: true }`.",
        ["expect.cookies"] = "Checks on cookies by name (case-sensitive), in the workflow's cookie jar after the response, for the request's URL: a value, or conditions such as `{ exists: false }`.",
        ["expect.body"] = "Checks on the JSON body, by JSONPath (`$.id`, `$.items[0].name`): a value, or conditions such as `{ gt: 0 }`.",
        ["expect.duration"] = "Check on the response time, such as `{ lt: 500ms }`.",
        ["until.status"] = "Wait until the status is this code, or one of a list. Without it, any status is accepted while waiting.",
        ["until.headers"] = "Wait until these header checks pass.",
        ["until.cookies"] = "Wait until these cookie checks pass.",
        ["until.body"] = "Wait until these body checks pass, by JSONPath. Stop on every final state, then check the expected one in `expect`.",
        ["until.every"] = "Time between attempts. Default: 1s.",
        ["until.timeout"] = "Give up after this time. Default: 30s.",

        // settings
        ["settings.timeout"] = "Seconds per request. Default: 30.",
        ["settings.max_retries"] = "Retries on network errors and timeouts. HTTP error statuses are never retried. Default: 0.",

        // conditions
        ["condition.eq"] = "Equal to this value.",
        ["condition.ne"] = "Not equal to this value.",
        ["condition.gt"] = "Greater than this number, or this duration in `expect.duration`.",
        ["condition.gte"] = "Greater than or equal to.",
        ["condition.lt"] = "Less than this number, or this duration in `expect.duration`.",
        ["condition.lte"] = "Less than or equal to.",
        ["condition.exists"] = "`true`: the value is present; `false`: it is not.",
        ["condition.contains"] = "A string contains this text, or a list contains this item.",
        ["condition.in"] = "One of these values (OR).",
        ["condition.any"] = "One of these conditions passes (OR), such as `[{ lt: 0 }, { gt: 100 }]`.",
        ["condition.schema"] = "The value is valid against a JSON Schema: a file (`../schemas/user.json`, `openapi.yaml#/components/schemas/User`) or a schema written here (`{ type: string }`). `$` is the whole body.",
        ["condition.matches"] = "A regular expression finds a match in the value, such as `'^ORD-[0-9]+$'` (in single quotes; `(?i)` ignores case). Numbers and booleans are matched as their text."
    };

    /// <summary>The schema as indented JSON, the text of <c>tinyprobe schema</c> and of docs/schema/tinyprobe.schema.json.</summary>
    public static string Json()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            w.WriteStartObject();
            w.WriteString("$schema", "https://json-schema.org/draft/2020-12/schema");
            w.WriteString("$id", Id);
            w.WriteString("title", "tinyprobe file");
            w.WriteString("description",
                "A tinyprobe API file (endpoints) or workflow (steps). Validate with `tinyprobe inspect`; the format is in `tinyprobe guide`.");
            w.WriteString("type", "object");
            Properties(w, "file", Loader.FileKeys);
            w.WriteBoolean("additionalProperties", false);

            w.WriteStartObject("$defs");
            Definition(w, "endpoint", "An endpoint: what to send.", Loader.EndpointKeys);
            Definition(w, "multipart_file", "A file of a multipart body.", Loader.MultipartFileKeys, anyOfRequired: ["file"]);
            AuthSet(w);
            Definition(w, "basic", "Basic auth credentials.", Loader.BasicKeys, required: Loader.BasicKeys);
            Definition(w, "oauth2", "OAuth2 client credentials.", Loader.OAuth2Keys, required: ["token_url", "client_id", "client_secret"]);
            Definition(w, "for_each_file", "A data file of items.", Loader.ForEachFileKeys, required: Loader.ForEachFileKeys);
            Definition(w, "save_cookie", "A value saved from a cookie.", Loader.SaveKeys, anyOfRequired: ["cookie"]);
            Definition(w, "step", "A step: `call` an endpoint, `use` a sequence, or `sleep`.", Loader.StepKeys, anyOfRequired: ["call", "use", "sleep"]);
            Definition(w, "expect", "What the response must be.", Loader.ExpectKeys);
            Definition(w, "until", "Polling conditions.", Loader.UntilKeys);
            Definition(w, "settings", "Settings of the workflow.", Loader.SettingsKeys);
            Definition(w, "condition", "Conditions on a value; all must pass.", Loader.Operators.Keys.ToArray());
            Definition(w, "duration_condition", "Conditions on the response time, with durations such as `500ms`.",
                Loader.DurationOperators, section: "condition");

            // A check is conditions (only operators), or a value to compare: anything but an object that mixes
            // operators with other fields, which the loader rejects.
            w.WriteStartObject("check");
            w.WriteString("description", "A value (equal to it), or conditions such as `{ gt: 0, lt: 10 }`.");
            w.WriteStartArray("anyOf");
            Ref(w, "condition");
            w.WriteStartObject();
            w.WriteStartArray("type");
            foreach (var type in new[] { "string", "number", "boolean", "null", "array" })
                w.WriteStringValue(type);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("type", "object");
            w.WriteStartObject("propertyNames");
            w.WriteStartObject("not");
            w.WriteStartArray("enum");
            foreach (var op in Loader.Operators.Keys)
                w.WriteStringValue(op);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();

            // A form or multipart field: a value, a file (multipart only), or a list of them.
            w.WriteStartObject("form_value");
            w.WriteStartArray("anyOf");
            w.WriteStartObject();
            Scalars(w);
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("type", "array");
            w.WriteStartObject("items");
            Scalars(w);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject("multipart_value");
            w.WriteStartArray("anyOf");
            w.WriteStartObject();
            Scalars(w);
            w.WriteEndObject();
            Ref(w, "multipart_file");
            w.WriteStartObject();
            w.WriteString("type", "array");
            w.WriteStartObject("items");
            w.WriteStartArray("anyOf");
            w.WriteStartObject();
            Scalars(w);
            w.WriteEndObject();
            Ref(w, "multipart_file");
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartObject("headers");
            w.WriteString("type", "object");
            w.WriteStartObject("additionalProperties");
            w.WriteStartArray("type");
            foreach (var type in new[] { "string", "number", "boolean" })
                w.WriteStringValue(type);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void Definition(Utf8JsonWriter w, string name, string description, string[] keys,
        string[]? anyOfRequired = null, string? section = null, string[]? required = null)
    {
        w.WriteStartObject(name);
        w.WriteString("description", description);
        w.WriteString("type", "object");
        Properties(w, section ?? name, keys);
        w.WriteBoolean("additionalProperties", false);
        if (required is not null)
        {
            w.WriteStartArray("required");
            foreach (var key in required)
                w.WriteStringValue(key);
            w.WriteEndArray();
        }
        if (anyOfRequired is not null)
        {
            w.WriteStartArray("anyOf");
            foreach (var key in anyOfRequired)
            {
                w.WriteStartObject();
                w.WriteStartArray("required");
                w.WriteStringValue(key);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    /// <summary>An auth set: the scheme keys, and any other key is a header.</summary>
    private static void AuthSet(Utf8JsonWriter w)
    {
        w.WriteStartObject("auth_set");
        w.WriteString("description", "An auth set: headers (`Name: value`), and at most one of `basic` and `oauth2`.");
        w.WriteString("type", "object");
        Properties(w, "auth_set", Loader.AuthSchemeKeys);
        w.WriteStartObject("additionalProperties");
        w.WriteStartArray("type");
        foreach (var type in new[] { "string", "number", "boolean" })
            w.WriteStringValue(type);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void Properties(Utf8JsonWriter w, string section, string[] keys)
    {
        w.WriteStartObject("properties");
        foreach (var key in keys)
        {
            var id = $"{section}.{key}";
            if (!Descriptions.TryGetValue(id, out var description))
                throw new InvalidOperationException($"FormatSchema has no description for '{id}': add one.");

            w.WriteStartObject(key);
            w.WriteString("description", description);
            WriteType(w, section, key);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    /// <summary>The type of each key: loose where the loader accepts several forms or placeholders.</summary>
    private static void WriteType(Utf8JsonWriter w, string section, string key)
    {
        switch ($"{section}.{key}")
        {
            case "file.name" or "file.description" or "file.base_url" or "endpoint.path"
                or "multipart_file.file" or "multipart_file.filename" or "multipart_file.content_type"
                or "save_cookie.cookie" or "for_each_file.file" or "basic.username" or "basic.password"
                or "oauth2.token_url" or "oauth2.client_id" or "oauth2.client_secret" or "step.name" or "step.call" or "step.use" or "step.sleep" or "until.every" or "until.timeout":
                w.WriteString("type", "string");
                break;
            case "endpoint.method":
                w.WriteString("type", "string");
                w.WriteString("pattern", "^[A-Za-z]+$");
                w.WriteStartArray("examples");
                foreach (var method in new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" })
                    w.WriteStringValue(method);
                w.WriteEndArray();
                break;
            case "file.import":
                ImportList(w);
                break;
            case "endpoint.auth":
                StringOrList(w);
                break;
            case "endpoint.query":
                w.WriteString("type", "array");
                w.WriteStartObject("items");
                w.WriteString("type", "string");
                w.WriteEndObject();
                break;
            case "file.headers" or "endpoint.headers":
                Ref(w, "headers", inline: true);
                break;
            case "endpoint.form":
                MapOf(w, () => Ref(w, "form_value", inline: true));
                break;
            case "endpoint.multipart":
                MapOf(w, () => Ref(w, "multipart_value", inline: true));
                break;
            case "file.auth":
                MapOf(w, () => Ref(w, "auth_set", inline: true));
                break;
            case "auth_set.basic":
                Ref(w, "basic", inline: true);
                break;
            case "auth_set.oauth2":
                Ref(w, "oauth2", inline: true);
                break;
            case "oauth2.scope":
                StringOrList(w);
                break;
            case "oauth2.client_auth":
                w.WriteString("type", "string");
                w.WriteStartArray("enum");
                w.WriteStringValue("basic");
                w.WriteStringValue("form");
                w.WriteEndArray();
                break;
            case "oauth2.form":
                Ref(w, "headers", inline: true);
                break;
            case "file.endpoints":
                MapOf(w, () => Ref(w, "endpoint", inline: true));
                break;
            case "file.steps" or "file.sequence":
                w.WriteString("type", "array");
                w.WriteStartObject("items");
                Ref(w, "step", inline: true);
                w.WriteEndObject();
                break;
            case "file.settings":
                Ref(w, "settings", inline: true);
                break;
            case "file.variables" or "step.with" or "endpoint.params" or "file.params":
                w.WriteString("type", "object");
                break;
            case "step.save" or "endpoint.globals":
                MapOf(w, () =>
                {
                    w.WriteStartArray("anyOf");
                    w.WriteStartObject();
                    w.WriteString("type", "string");
                    w.WriteEndObject();
                    Ref(w, "save_cookie");
                    w.WriteEndArray();
                });
                break;
            case "step.expect":
                Ref(w, "expect", inline: true);
                break;
            case "step.for_each":
                w.WriteStartArray("anyOf");
                w.WriteStartObject();
                w.WriteString("type", "array");
                w.WriteStartObject("items");
                w.WriteString("type", "object");
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteStartObject();
                w.WriteString("type", "string");
                w.WriteString("pattern", "^\\$\\{[^}]+\\}$");
                w.WriteEndObject();
                Ref(w, "for_each_file");
                w.WriteEndArray();
                break;
            case "step.until":
                Ref(w, "until", inline: true);
                break;
            case "expect.status" or "until.status":
                w.WriteStartArray("anyOf");
                w.WriteStartObject();
                w.WriteString("type", "integer");
                w.WriteEndObject();
                w.WriteStartObject();
                w.WriteString("type", "array");
                w.WriteStartObject("items");
                w.WriteString("type", "integer");
                w.WriteEndObject();
                w.WriteEndObject();
                if (key == "status" && section == "expect")
                {
                    // A placeholder, given by each item of a for_each step.
                    w.WriteStartObject();
                    w.WriteString("type", "string");
                    w.WriteString("pattern", "^\\$\\{[^}]+\\}$");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                break;
            case "expect.headers" or "expect.cookies" or "expect.body" or "until.headers" or "until.cookies" or "until.body":
                MapOf(w, () => Ref(w, "check", inline: true));
                break;
            case "expect.duration":
                Ref(w, "duration_condition", inline: true);
                break;
            case "settings.timeout":
                w.WriteString("type", "number");
                break;
            case "settings.max_retries":
                w.WriteString("type", "integer");
                break;
            case "condition.exists":
                w.WriteString("type", "boolean");
                break;
            case "condition.in":
                w.WriteString("type", "array");
                break;
            case "condition.matches":
                w.WriteString("type", "string");
                break;
            case "condition.any":
                w.WriteString("type", "array");
                w.WriteStartObject("items");
                Ref(w, "check", inline: true);
                w.WriteEndObject();
                break;
            default:
                // endpoint.body, condition.eq/ne/gt/gte/lt/lte/contains: any value.
                break;
        }
    }

    private static void Scalars(Utf8JsonWriter w)
    {
        w.WriteStartArray("type");
        foreach (var type in new[] { "string", "number", "boolean", "null" })
            w.WriteStringValue(type);
        w.WriteEndArray();
    }

    /// <summary><c>import</c>: a path, or a list of paths and <c>name: path</c> pairs.</summary>
    private static void ImportList(Utf8JsonWriter w)
    {
        w.WriteStartArray("anyOf");
        w.WriteStartObject();
        w.WriteString("type", "string");
        w.WriteEndObject();
        w.WriteStartObject();
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteStartArray("anyOf");
        w.WriteStartObject();
        w.WriteString("type", "string");
        w.WriteEndObject();
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteNumber("minProperties", 1);
        w.WriteNumber("maxProperties", 1);
        w.WriteStartObject("propertyNames");
        w.WriteString("pattern", "^[a-z][a-z0-9_]*$");
        w.WriteEndObject();
        w.WriteStartObject("additionalProperties");
        w.WriteString("type", "string");
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndArray();
    }

    private static void StringOrList(Utf8JsonWriter w)
    {
        w.WriteStartArray("anyOf");
        w.WriteStartObject();
        w.WriteString("type", "string");
        w.WriteEndObject();
        w.WriteStartObject();
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteString("type", "string");
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndArray();
    }

    private static void MapOf(Utf8JsonWriter w, Action value)
    {
        w.WriteString("type", "object");
        w.WriteStartObject("additionalProperties");
        value();
        w.WriteEndObject();
    }

    /// <summary>A <c>$ref</c> in its own object, or (inline) as a key of the current one.</summary>
    private static void Ref(Utf8JsonWriter w, string definition, bool inline = false)
    {
        if (!inline)
            w.WriteStartObject();
        w.WriteString("$ref", $"#/$defs/{definition}");
        if (!inline)
            w.WriteEndObject();
    }
}
