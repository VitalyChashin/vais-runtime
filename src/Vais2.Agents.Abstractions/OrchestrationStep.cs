// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// One participant's contribution to an orchestrated multi-agent run. Emitted by
/// <see cref="IAgentOrchestrator.RunAsync"/> as each participant produces text.
/// </summary>
/// <param name="AgentName">The producing participant's <see cref="AgentParticipant.Name"/>.</param>
/// <param name="Text">The assistant-produced text for this step.</param>
/// <param name="Role">
/// Chat role under which this step should be interpreted downstream. Defaults to
/// <see cref="ChatRole.Assistant"/>; orchestrators may use other roles (e.g. to
/// inject a synthetic system turn) but the common case is assistant output.
/// </param>
public sealed record OrchestrationStep(
    string AgentName,
    string Text,
    ChatRole Role = ChatRole.Assistant);
