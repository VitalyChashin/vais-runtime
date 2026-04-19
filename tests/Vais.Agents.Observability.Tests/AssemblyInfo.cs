// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Xunit;

// ActivityListener is process-global. Running tests in parallel lets one test's
// listener observe another's activities and vice-versa, producing flaky results
// (and obscure KeyNotFoundException failures when an alien activity lacks the
// tags the assertion expects). Tests here are fast — serialise them.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
