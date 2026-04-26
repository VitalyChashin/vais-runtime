// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;

// Declares this assembly as a Vais.Agents plugin targeting the v0.24 ABI.
// The loader registers the listed handler TypeName with the convention-based
// handler factory (ActivatorUtilities.CreateInstance) so constructor-injected
// services resolve from the runtime's full IServiceProvider.
[assembly: VaisPlugin(targetApiVersion: "0.18", "MyApp.ResearchPlannerAgent.ResearchPlannerAgent")]
