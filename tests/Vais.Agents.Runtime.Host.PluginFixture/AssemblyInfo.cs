// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;

// Declares this fixture assembly as a v0.18 plugin. The integration test
// copies the built DLL into a temp plugins directory, spins up the runtime
// composition root against it, and asserts that handler type
// Vais.Agents.Runtime.Host.PluginFixture.WeatherAgent materialises on the
// plugin branch of the translator.
[assembly: VaisPlugin(
    targetApiVersion: "0.18",
    "Vais.Agents.Runtime.Host.PluginFixture.WeatherAgent")]
