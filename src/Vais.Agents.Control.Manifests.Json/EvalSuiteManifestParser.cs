// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Manifests;

internal static class EvalSuiteManifestParser
{
    internal static EvalSuiteManifest? Parse(JsonElement root, List<string> errors, string prefix)
    {
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid metadata block");
            return null;
        }

        var id = metadata.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var version = metadata.TryGetProperty("version", out var vEl) ? vEl.GetString() : null;
        var description = metadata.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;

        if (string.IsNullOrEmpty(id))
            errors.Add($"{prefix}metadata.id is required");
        else if (!ManifestValidation.IsValidId(id))
            errors.Add($"{prefix}metadata.id '{id}' does not match ^[a-z][a-z0-9-]{{0,62}}$");

        if (string.IsNullOrEmpty(version))
            errors.Add($"{prefix}metadata.version is required");
        else if (!ManifestValidation.IsValidVersion(version))
            errors.Add($"{prefix}metadata.version '{version}' does not match ^\\d+\\.\\d+(\\.\\d+)?$");

        var labels = ParseStringMap(metadata, "labels");

        if (!root.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid spec block");
            return null;
        }

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version)) return null;

        // ── target ───────────────────────────────────────────────────────────────
        // Support both flat spec.agentId/graphId (legacy) and spec.target block.
        var agentId = spec.TryGetProperty("agentId", out var aiEl) ? aiEl.GetString() : null;
        var graphId = spec.TryGetProperty("graphId", out var giEl) ? giEl.GetString() : null;
        EvalTarget? target = null;

        if (spec.TryGetProperty("target", out var targetEl) && targetEl.ValueKind == JsonValueKind.Object)
        {
            var agentRef = targetEl.TryGetProperty("agentRef", out var arEl) ? arEl.GetString() : null;
            var graphRef = targetEl.TryGetProperty("graphRef", out var grEl) ? grEl.GetString() : null;
            var agentVersion = targetEl.TryGetProperty("agentVersion", out var avEl) ? avEl.GetString() : null;

            if (agentRef is not null && graphRef is not null)
                errors.Add($"{prefix}spec.target.agentRef and spec.target.graphRef are mutually exclusive");

            target = new EvalTarget { AgentRef = agentRef, GraphRef = graphRef, AgentVersion = agentVersion };
            // Propagate for backward-compat accessors
            agentId ??= agentRef;
            graphId ??= graphRef;
        }

        if (agentId is not null && graphId is not null && target is null)
            errors.Add($"{prefix}spec.agentId and spec.graphId are mutually exclusive");

        // ── defaults ─────────────────────────────────────────────────────────────
        EvalDefaults? defaults = null;
        if (spec.TryGetProperty("defaults", out var defaultsEl) && defaultsEl.ValueKind == JsonValueKind.Object)
        {
            var judgeModel = defaultsEl.TryGetProperty("judgeModel", out var jmEl) ? jmEl.GetString() : null;
            TimeSpan? timeout = null;
            if (defaultsEl.TryGetProperty("timeout", out var toEl) && toEl.ValueKind == JsonValueKind.String)
            {
                if (TimeSpan.TryParse(toEl.GetString(), out var ts))
                    timeout = ts;
            }
            defaults = new EvalDefaults { JudgeModel = judgeModel, Timeout = timeout };
        }

        // ── baseline ─────────────────────────────────────────────────────────────
        EvalBaseline? baseline = null;
        if (spec.TryGetProperty("baseline", out var baselineEl) && baselineEl.ValueKind == JsonValueKind.Object)
        {
            var runId = baselineEl.TryGetProperty("runId", out var riEl) ? riEl.GetString() : null;
            if (!string.IsNullOrEmpty(runId))
                baseline = new EvalBaseline(runId);
        }

        // ── replayMode ───────────────────────────────────────────────────────────
        var replayMode = EvalReplayMode.Live;
        if (spec.TryGetProperty("replayMode", out var rmEl) && rmEl.ValueKind == JsonValueKind.String)
        {
            replayMode = ParseReplayMode(rmEl.GetString(), errors, $"{prefix}spec.replayMode");
        }

        // ── cases ────────────────────────────────────────────────────────────────
        if (!spec.TryGetProperty("cases", out var casesEl) || casesEl.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{prefix}spec.cases is required and must be an array");
            return null;
        }

        var cases = new List<EvalCase>();
        var caseIndex = 0;
        foreach (var caseEl in casesEl.EnumerateArray())
        {
            var casePrefix = $"{prefix}spec.cases[{caseIndex}] ";
            var evalCase = ParseCase(caseEl, errors, casePrefix);
            if (evalCase is not null) cases.Add(evalCase);
            caseIndex++;
        }

        if (cases.Count == 0)
            errors.Add($"{prefix}spec.cases must contain at least one case");

        var caseIdsSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            if (!caseIdsSeen.Add(c.Id))
                errors.Add($"{prefix}duplicate case id '{c.Id}'");
        }

        return new EvalSuiteManifest(id, version, description, labels)
        {
            Spec = new EvalSuiteSpec
            {
                AgentId = agentId,
                GraphId = graphId,
                Target = target,
                Defaults = defaults,
                Baseline = baseline,
                Cases = cases,
                ReplayMode = replayMode,
            },
        };
    }

    private static EvalCase? ParseCase(JsonElement el, List<string> errors, string prefix)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}case must be a JSON object");
            return null;
        }

        var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var input = el.TryGetProperty("input", out var inEl) ? inEl.GetString() : null;

        if (string.IsNullOrEmpty(id))
            errors.Add($"{prefix}id is required");

        if (string.IsNullOrEmpty(input))
            errors.Add($"{prefix}input is required");

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(input)) return null;

        var name = el.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
        var description = el.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;
        var expectedOutput = el.TryGetProperty("expectedOutput", out var eoEl) ? eoEl.GetString() : null;

        IReadOnlyDictionary<string, JsonElement>? variables = null;
        if (el.TryGetProperty("variables", out var varsEl) && varsEl.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, JsonElement>();
            foreach (var prop in varsEl.EnumerateObject())
                map[prop.Name] = prop.Value.Clone();
            variables = map;
        }

        IReadOnlyList<EvalHistoryTurn>? initialHistory = null;
        if (el.TryGetProperty("initialHistory", out var histEl) && histEl.ValueKind == JsonValueKind.Array)
        {
            var turns = new List<EvalHistoryTurn>();
            foreach (var turnEl in histEl.EnumerateArray())
            {
                var role = turnEl.TryGetProperty("role", out var rEl) ? rEl.GetString() : null;
                var content = turnEl.TryGetProperty("content", out var cEl) ? cEl.GetString() : null;
                if (!string.IsNullOrEmpty(role) && content is not null)
                    turns.Add(new EvalHistoryTurn(role, content));
            }
            if (turns.Count > 0) initialHistory = turns;
        }

        EvalReplayMode? caseReplay = null;
        if (el.TryGetProperty("replay", out var replayEl) && replayEl.ValueKind == JsonValueKind.String)
            caseReplay = ParseReplayMode(replayEl.GetString(), errors, $"{prefix}replay");

        var assertions = new List<EvalAssertion>();
        if (el.TryGetProperty("assertions", out var assertionsEl) && assertionsEl.ValueKind == JsonValueKind.Array)
        {
            var ai = 0;
            foreach (var a in assertionsEl.EnumerateArray())
            {
                var aPrefix = $"{prefix}assertions[{ai}] ";
                var kind = a.TryGetProperty("kind", out var kEl) ? kEl.GetString() : null;
                if (string.IsNullOrEmpty(kind))
                {
                    errors.Add($"{aPrefix}kind is required");
                }
                else
                {
                    JsonElement? params_ = a.TryGetProperty("params", out var pEl) ? pEl.Clone() : null;
                    assertions.Add(new EvalAssertion(kind, params_));
                }
                ai++;
            }
        }

        return new EvalCase
        {
            Id = id,
            Name = name,
            Description = description,
            Input = input,
            Variables = variables,
            ExpectedOutput = expectedOutput,
            InitialHistory = initialHistory,
            Replay = caseReplay,
            Assertions = assertions,
        };
    }

    private static EvalReplayMode ParseReplayMode(string? value, List<string> errors, string path)
    {
        if (value is null) return EvalReplayMode.Live;
        return value.ToLowerInvariant() switch
        {
            "live" or "none" => EvalReplayMode.Live,      // "none" accepted as backward-compat alias
            "cached" or "record" => EvalReplayMode.Cached, // "record"/"replay" accepted as backward-compat alias
            "replay" => EvalReplayMode.Cached,
            _ => ReportUnknown(errors, path, value),
        };
    }

    private static EvalReplayMode ReportUnknown(List<string> errors, string path, string value)
    {
        errors.Add($"{path} '{value}' is not valid (live, cached)");
        return EvalReplayMode.Live;
    }

    private static IReadOnlyDictionary<string, string>? ParseStringMap(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var mapEl) || mapEl.ValueKind != JsonValueKind.Object)
            return null;
        var map = new Dictionary<string, string>();
        foreach (var prop in mapEl.EnumerateObject())
        {
            var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
            map[prop.Name] = value ?? string.Empty;
        }
        return map;
    }
}
