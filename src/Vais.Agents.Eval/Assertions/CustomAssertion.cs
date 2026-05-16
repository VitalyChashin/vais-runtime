// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Delegate assertion: resolves a named factory from <see cref="IEvalAssertionFactoryRegistry"/>
/// and creates the inner assertion at eval time.
/// Config: <c>{ "name": "my-kind", "args": { ... } }</c>.
/// </summary>
internal sealed class CustomAssertion : IEvalAssertion
{
    private readonly string _name;
    private readonly JsonElement _innerArgs;
    private readonly IEvalAssertionFactoryRegistry _registry;
    private readonly IServiceProvider _services;

    /// <summary>Construct with target assertion name, forwarded args, factory registry, and service provider.</summary>
    public CustomAssertion(string name, JsonElement innerArgs, IEvalAssertionFactoryRegistry registry, IServiceProvider services)
    {
        _name = name;
        _innerArgs = innerArgs;
        _registry = registry;
        _services = services;
    }

    /// <inheritdoc/>
    public string Kind => "custom";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        if (!_registry.TryGet(_name, out var factory))
            return ValueTask.FromResult(new EvalAssertionResult(
                EvalAssertionStatus.Error,
                Score: null,
                Reason: $"Custom assertion '{_name}' not found in registry. Registered: [{string.Join(", ", _registry.RegisteredKinds)}]"));

        IEvalAssertion inner;
        try
        {
            inner = factory.Create(_innerArgs, _services);
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(new EvalAssertionResult(
                EvalAssertionStatus.Error,
                Score: null,
                Reason: $"Failed to create custom assertion '{_name}': {ex.Message}"));
        }

        return inner.EvaluateAsync(ctx, run, ct);
    }
}

/// <summary>
/// Factory for <see cref="CustomAssertion"/>. Resolves the registry lazily from the
/// <see cref="IServiceProvider"/> passed to <see cref="Create"/> to avoid the circular
/// dependency that would arise from injecting <see cref="IEvalAssertionFactoryRegistry"/>
/// directly in the constructor (registry → factories → custom factory → registry).
/// </summary>
internal sealed class CustomAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "custom";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        var name = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
            ? nEl.GetString() ?? throw new InvalidOperationException("custom assertion 'name' must be a non-empty string")
            : throw new InvalidOperationException("custom assertion requires a params object with 'name'");

        var innerArgs = args.TryGetProperty("args", out var aEl) ? aEl : default;

        var registry = services.GetService(typeof(IEvalAssertionFactoryRegistry)) as IEvalAssertionFactoryRegistry
            ?? throw new InvalidOperationException("IEvalAssertionFactoryRegistry is not registered. Call services.AddVaisAgentsEval().");

        return new CustomAssertion(name, innerArgs, registry, services);
    }
}
