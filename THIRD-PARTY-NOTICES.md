# Third-party components

The root MIT license covers original Codex Lover source. It does not replace
licenses on dependencies or copied upstream notices.

| Component | Use | License / source |
| --- | --- | --- |
| .NET and WPF | Runtime and Windows UI | MIT; https://github.com/dotnet/runtime and https://github.com/dotnet/wpf |
| xUnit, Visual Studio runner | Regression tests | Apache-2.0; https://github.com/xunit/xunit and https://github.com/xunit/visualstudio.xunit |
| Microsoft.NET.Test.Sdk | Test execution | MIT; https://github.com/microsoft/vstest |
| coverlet.collector | Test coverage tooling | MIT; https://github.com/coverlet-coverage/coverlet |
| sharp | Optional Store image generation | Apache-2.0; https://github.com/lovell/sharp; bundled libvips has its own LGPL notices |
| OpenAI Codex | Optional pinned external runtime | Apache-2.0 plus bundled components; see packaging/codex-runtime-licenses |

Normal and Store NuGet lockfiles record resolved direct and transitive packages.
The optional packaging/package-lock.json records the Node dependency graph.
Dependency packages retain their upstream license files and notices.

No Codex executable or third-party runtime binary is committed here. The optional
runtime helper downloads an unmodified pinned upstream archive, verifies hashes
and signatures, and includes the known upstream notices recorded in
packaging/codex-runtime-manifest.json. That inventory explicitly does not claim to
be an exhaustive audit of every bundled Cargo dependency. Binary distributors
must review the complete license obligations of the exact package they ship.

The terminal-style favicon and desktop artwork are included with the project
source; use of project names or third-party marks must not imply endorsement.
