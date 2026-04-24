// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;

// V2 of the hot-reload fixture. Shares the same AssemblyName and handler type
// name as V1 so DefaultPluginReloader can swap the registry atomically. V2
// returns "Rainy!" from AskAsync; V1 returns "Sunny!".
[assembly: VaisPlugin(
    targetApiVersion: "0.18",
    "Vais.Agents.Runtime.Host.PluginFixtureHotReload.WeatherAgent")]
