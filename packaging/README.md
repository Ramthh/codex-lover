# Packaging

Ordinary contributors should use the build/test/demo instructions in the root
README. Packaging is optional and is not run during an ordinary source build.

## Local developer package

~~~powershell
.\scripts\Publish.ps1 -LocalTest -Runtime win-x64 -Deployment SelfContained
~~~

The helper creates a new timestamped directory under artifacts/publish and refuses
to overwrite a previous run. LocalTest builds are unsigned evaluation artifacts.
For a public signed release, omit LocalTest and provide CertificateThumbprint and
SignToolPath. Signing keys and certificates are never included in the repository.
The explicit UnsignedBeta channel retains its warning and release-channel checks.

SelfContained bundles .NET. IncludeCodexRuntime additionally downloads the pinned
official x64 Codex runtime, checks its hashes/signatures and copies its notices.
Review THIRD-PARTY-NOTICES.md before binary distribution. Native Arm64 source
builds require separate validation; the pinned external runtime bundle is x64.

## Store packaging (publisher tooling)

Use PowerShell 7, the pinned .NET SDK, Node.js and the Windows SDK MakeAppx tool.
Install the optional asset dependency using npm ci --prefix packaging.

~~~powershell
.\packaging\Build-StorePackage.ps1 -OutputDirectory .\artifacts\store-source-check
~~~

The script accepts Dotnet and MakeAppx paths and creates an unsigned MSIX. It
uses the original publisher's registered Store identity. Forks must substitute
their own registered identity and matching validation expectations before Store
submission; MIT does not transfer a publisher account or signing authority.
No automated command here submits a package to Microsoft or changes a live site.

The package-content checker verifies the exact hash, entries and pinned runtime.
Clean-Windows lifecycle helpers operate only inside their required disposable
Windows environment. See STORE-VALIDATION.md. Do not weaken host security policy
to run them. A build or unit-test pass is not a Store certification result.
