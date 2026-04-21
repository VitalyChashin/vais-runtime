// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents.Core.Guardrails;

namespace Vais.Agents.Runtime.Instantiation.Guardrails;

/// <summary>Factory for <see cref="LengthCapInputGuardrail"/>. Expects a <c>maxChars</c> int param.</summary>
public sealed class LengthCapInputGuardrailFactory : IGuardrailFactory
{
    /// <summary>Factory name — matched case-insensitive against <c>GuardrailRef.Name</c>.</summary>
    public const string FactoryName = "LengthCap";

    /// <inheritdoc />
    public string Name => FactoryName;

    /// <inheritdoc />
    public GuardrailLayer Layer => GuardrailLayer.Input;

    /// <inheritdoc />
    public object Create(JsonElement? parameters, IServiceProvider serviceProvider)
    {
        var maxChars = ParamHelpers.RequireInt(parameters, "maxChars", FactoryName);
        return new LengthCapInputGuardrail(maxChars);
    }
}

/// <summary>Factory for <see cref="RegexAllowlistInputGuardrail"/>. Expects a <c>pattern</c> string param.</summary>
public sealed class RegexAllowlistInputGuardrailFactory : IGuardrailFactory
{
    /// <summary>Factory name — matched case-insensitive against <c>GuardrailRef.Name</c>.</summary>
    public const string FactoryName = "RegexAllowlist";

    /// <inheritdoc />
    public string Name => FactoryName;

    /// <inheritdoc />
    public GuardrailLayer Layer => GuardrailLayer.Input;

    /// <inheritdoc />
    public object Create(JsonElement? parameters, IServiceProvider serviceProvider)
    {
        var pattern = ParamHelpers.RequireRegex(parameters, "pattern", FactoryName);
        return new RegexAllowlistInputGuardrail(pattern);
    }
}

/// <summary>Factory for <see cref="RegexAllowlistOutputGuardrail"/>.</summary>
public sealed class RegexAllowlistOutputGuardrailFactory : IGuardrailFactory
{
    /// <summary>Factory name.</summary>
    public const string FactoryName = "RegexAllowlist";

    /// <inheritdoc />
    public string Name => FactoryName;

    /// <inheritdoc />
    public GuardrailLayer Layer => GuardrailLayer.Output;

    /// <inheritdoc />
    public object Create(JsonElement? parameters, IServiceProvider serviceProvider)
    {
        var pattern = ParamHelpers.RequireRegex(parameters, "pattern", FactoryName);
        return new RegexAllowlistOutputGuardrail(pattern);
    }
}

/// <summary>Factory for <see cref="RegexDenylistInputGuardrail"/>.</summary>
public sealed class RegexDenylistInputGuardrailFactory : IGuardrailFactory
{
    /// <summary>Factory name.</summary>
    public const string FactoryName = "RegexDenylist";

    /// <inheritdoc />
    public string Name => FactoryName;

    /// <inheritdoc />
    public GuardrailLayer Layer => GuardrailLayer.Input;

    /// <inheritdoc />
    public object Create(JsonElement? parameters, IServiceProvider serviceProvider)
    {
        var pattern = ParamHelpers.RequireRegex(parameters, "pattern", FactoryName);
        return new RegexDenylistInputGuardrail(pattern);
    }
}

/// <summary>Factory for <see cref="RegexDenylistOutputGuardrail"/>.</summary>
public sealed class RegexDenylistOutputGuardrailFactory : IGuardrailFactory
{
    /// <summary>Factory name.</summary>
    public const string FactoryName = "RegexDenylist";

    /// <inheritdoc />
    public string Name => FactoryName;

    /// <inheritdoc />
    public GuardrailLayer Layer => GuardrailLayer.Output;

    /// <inheritdoc />
    public object Create(JsonElement? parameters, IServiceProvider serviceProvider)
    {
        var pattern = ParamHelpers.RequireRegex(parameters, "pattern", FactoryName);
        return new RegexDenylistOutputGuardrail(pattern);
    }
}

/// <summary>
/// Factory for <see cref="LlmAsJudgeOutputGuardrail"/>. Expects params:
/// <c>{ "judgeModel": &lt;ModelSpec&gt;, "judgePrompt": "...", "minScore": 0.7 }</c>.
/// Builds the judge provider through the <see cref="ICompletionProviderPool"/>
/// so multiple judges sharing the same spec reuse a single SDK client.
/// </summary>
public sealed class LlmAsJudgeOutputGuardrailFactory : IGuardrailFactory
{
    /// <summary>Factory name.</summary>
    public const string FactoryName = "LlmAsJudge";

    /// <inheritdoc />
    public string Name => FactoryName;

    /// <inheritdoc />
    public GuardrailLayer Layer => GuardrailLayer.Output;

    /// <inheritdoc />
    public object Create(JsonElement? parameters, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var judgeSpec = ParseJudgeModel(parameters);
        var judgePrompt = ParamHelpers.RequireString(parameters, "judgePrompt", FactoryName);
        var minScore = ParamHelpers.RequireDouble(parameters, "minScore", FactoryName);

        var pool = serviceProvider.GetRequiredService<ICompletionProviderPool>();
        var judge = pool.GetAsync(judgeSpec, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        return new LlmAsJudgeOutputGuardrail(judge, judgePrompt, minScore);
    }

    private static ModelSpec ParseJudgeModel(JsonElement? parameters)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object
            || !parameters.Value.TryGetProperty("judgeModel", out var judgeModel))
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.GuardrailParamsInvalid,
                $"LlmAsJudge guardrail requires a 'judgeModel' object param.");
        }

        string provider = ParamHelpers.RequireString(judgeModel, "provider", FactoryName);
        string id = ParamHelpers.RequireString(judgeModel, "id", FactoryName);
        string? apiKeyRef = ParamHelpers.OptionalString(judgeModel, "apiKeyRef");
        string? baseUrlRef = ParamHelpers.OptionalString(judgeModel, "baseUrlRef");

        return new ModelSpec(Provider: provider, Id: id, ApiKeyRef: apiKeyRef, BaseUrlRef: baseUrlRef);
    }
}

internal static class ParamHelpers
{
    public static int RequireInt(JsonElement? parameters, string key, string factoryName)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object
            || !parameters.Value.TryGetProperty(key, out var value))
        {
            throw Missing(key, factoryName);
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw BadType(key, "integer", factoryName);
        }
        return result;
    }

    public static double RequireDouble(JsonElement? parameters, string key, string factoryName)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object
            || !parameters.Value.TryGetProperty(key, out var value))
        {
            throw Missing(key, factoryName);
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result))
        {
            throw BadType(key, "number", factoryName);
        }
        return result;
    }

    public static string RequireString(JsonElement? parameters, string key, string factoryName)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object
            || !parameters.Value.TryGetProperty(key, out var value))
        {
            throw Missing(key, factoryName);
        }
        return RequireStringCore(value, key, factoryName);
    }

    public static string RequireString(JsonElement container, string key, string factoryName)
    {
        if (!container.TryGetProperty(key, out var value))
        {
            throw Missing(key, factoryName);
        }
        return RequireStringCore(value, key, factoryName);
    }

    public static string? OptionalString(JsonElement container, string key)
    {
        if (!container.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    public static Regex RequireRegex(JsonElement? parameters, string key, string factoryName)
    {
        var pattern = RequireString(parameters, key, factoryName);
        try
        {
            return new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.GuardrailParamsInvalid,
                $"Guardrail '{factoryName}' param '{key}' is not a valid regular expression: {ex.Message}",
                ex);
        }
    }

    private static string RequireStringCore(JsonElement value, string key, string factoryName)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw BadType(key, "string", factoryName);
        }
        var str = value.GetString();
        if (string.IsNullOrWhiteSpace(str))
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.GuardrailParamsInvalid,
                $"Guardrail '{factoryName}' param '{key}' must not be empty.");
        }
        return str;
    }

    private static ManifestInstantiationException Missing(string key, string factoryName) =>
        new(ManifestInstantiationUrns.GuardrailParamsInvalid,
            $"Guardrail '{factoryName}' requires a '{key}' param.");

    private static ManifestInstantiationException BadType(string key, string expected, string factoryName) =>
        new(ManifestInstantiationUrns.GuardrailParamsInvalid,
            $"Guardrail '{factoryName}' param '{key}' must be a {expected}.");
}
