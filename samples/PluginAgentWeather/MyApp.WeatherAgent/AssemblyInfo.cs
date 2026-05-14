// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;

// Declares this assembly as a Vais.Agents plugin targeting the v0.18 ABI.
// The loader reads VaisPluginAttribute at startup and registers every listed
// handler TypeName with the corresponding IAgentHandlerFactory (or, for
// plugins like this one that only ship an IAiAgent implementation, the
// convention-based default handler factory using ActivatorUtilities).
[assembly: VaisPlugin(targetApiVersion: "0.18", "MyApp.WeatherAgent.WeatherAgent")]
