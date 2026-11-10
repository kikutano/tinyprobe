using System.Text.Json;
using Tinyprobe.Core.Definitions;

namespace Tinyprobe.Core.Execution;

internal interface IVariableScope
{
    bool TryGet(string name, out JsonElement value);
}

/// <summary>
/// Workflow-wide variables: saved values, then the environment, then the workflow defaults.
/// A default is resolved the first time it is used and then kept, so a generated value
/// (<c>email: "smoke-${$uuid}@example.com"</c>) is the same in every step of the run.
/// Defaults may use the environment, other defaults and generated values.
/// </summary>
internal sealed class GlobalVariables(
    IReadOnlyDictionary<string, Template> defaults,
    IReadOnlyDictionary<string, JsonElement> environment) : IVariableScope
{
    private readonly Dictionary<string, JsonElement> _saved = new();
    private readonly Dictionary<string, JsonElement> _resolvedDefaults = new();
    private readonly HashSet<string> _resolving = new();

    public void Save(string name, JsonElement value) => _saved[name] = value;

    /// <summary>The values saved so far, for the state of a run (spec 0022).</summary>
    public IReadOnlyList<KeyValuePair<string, JsonElement>> Saved => _saved.ToList();

    /// <summary>The defaults resolved so far: a generated value must stay the same in a resumed run.</summary>
    public IReadOnlyList<KeyValuePair<string, JsonElement>> ResolvedDefaults => _resolvedDefaults.ToList();

    /// <summary><c>run --resume</c>: the values of the state's run. A default of the state keeps its value.</summary>
    public void Restore(RunState state)
    {
        foreach (var (name, value) in state.Saved)
            _saved[name] = value;
        foreach (var (name, value) in state.Variables)
            _resolvedDefaults[name] = value;
    }

    public bool TryGet(string name, out JsonElement value)
    {
        if (_saved.TryGetValue(name, out value) || environment.TryGetValue(name, out value))
            return true;

        if (_resolvedDefaults.TryGetValue(name, out value))
            return true;

        if (!defaults.TryGetValue(name, out var template))
            return false;

        if (!_resolving.Add(name))
            throw new InvalidOperationException($"Variable '{name}' refers to itself.");
        try
        {
            value = TemplateRenderer.RenderValue(template, this);
            _resolvedDefaults[name] = value;
            return true;
        }
        finally
        {
            _resolving.Remove(name);
        }
    }
}

/// <summary>
/// Variables seen by one call of an endpoint. Declared params are local: they come from the
/// step's <c>with</c> or their default, never from global variables. Any other name is looked
/// up in the step's <c>with</c> first, then in the globals.
/// </summary>
internal sealed class CallScope : IVariableScope
{
    private readonly Dictionary<string, JsonElement> _arguments;
    private readonly Dictionary<string, JsonElement> _params = new();
    private readonly IVariableScope _globals;

    public CallScope(EndpointDefinition endpoint, Dictionary<string, JsonElement> arguments, IVariableScope globals)
    {
        _arguments = arguments;
        _globals = globals;

        foreach (var (name, defaultValue) in endpoint.Params ?? new Dictionary<string, Template?>())
        {
            if (arguments.TryGetValue(name, out var argument))
                _params[name] = argument;
            else if (defaultValue is not null)
                _params[name] = TemplateRenderer.RenderValue(defaultValue, globals);
            else
                throw new InvalidOperationException($"Missing required param '{name}' of endpoint '{endpoint.Name}'.");
        }

        DeclaredParams = endpoint.Params?.Keys.ToHashSet() ?? [];
    }

    private HashSet<string> DeclaredParams { get; }

    public bool TryGet(string name, out JsonElement value)
    {
        if (DeclaredParams.Contains(name))
            return _params.TryGetValue(name, out value);

        return _arguments.TryGetValue(name, out value) || _globals.TryGet(name, out value);
    }

    /// <summary>A value passed by the step or a param default, ignoring globals (used for query parameters).</summary>
    public bool TryGetArgument(string name, out JsonElement value) =>
        DeclaredParams.Contains(name)
            ? _params.TryGetValue(name, out value)
            : _arguments.TryGetValue(name, out value);
}

/// <summary>The values of a <c>for_each</c> item, then the globals: the scope of the step's <c>with</c>.</summary>
internal sealed class ItemScope(IReadOnlyDictionary<string, JsonElement> item, IVariableScope globals) : IVariableScope
{
    public bool TryGet(string name, out JsonElement value) =>
        item.TryGetValue(name, out value) || globals.TryGet(name, out value);
}

/// <summary>
/// The variables of the steps of one <c>use</c>: its params (the <c>use</c> step's <c>with</c>, then the defaults), then
/// the scope it is used in. A param is resolved the first time it is read, against that outer scope, and then kept.
/// </summary>
internal sealed class SequenceScope(SequenceUse use, IVariableScope outer) : IVariableScope
{
    private readonly Dictionary<string, JsonElement> _resolved = new();

    public bool TryGet(string name, out JsonElement value)
    {
        if (_resolved.TryGetValue(name, out value))
            return true;

        Template? template = use.Arguments.TryGetValue(name, out var argument) ? argument
            : use.Params.TryGetValue(name, out var defaultValue) ? defaultValue
            : null;
        if (template is null)
            return outer.TryGet(name, out value);

        value = TemplateRenderer.RenderValue(template, outer);
        _resolved[name] = value;
        return true;
    }
}
