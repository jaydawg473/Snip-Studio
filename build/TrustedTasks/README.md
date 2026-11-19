# Signed Windows App SDK build task

Windows App SDK Base 2.7.0 uses `RoslynCodeTaskFactory` to compile its manifest-fragment task into a temporary managed executable assembly during a self-contained build. That assembly cannot satisfy this project's verify-before-execution signing requirement.

This project precompiles the exact pinned Microsoft source fragment into `SnipStudio.TrustedBuildTasks.dll`. It references only the audited .NET 10 SDK's `Microsoft.Build.Framework.dll` and `Microsoft.Build.Utilities.Core.dll`, without adding package dependencies or copying those vendor assemblies. Attribution, the source hash and the MIT license are beside the source.

The build workflow is sequential:

1. Verify the SDK/compiler/MSBuild executables and assemblies against their original publisher signatures and reviewed hashes.
2. Build this task library without invoking it. No automatic task execution is attached to this project.
3. Timestamp-sign the resulting task DLL with Automation Forge and verify its signature. Record its final SHA256.
4. Import `build/WinUI.SignedTasks.targets` for the WinUI app through `Directory.Build.targets`. Pass `SnipStudioTrustedTasksAssembly`, `SnipStudioTrustedTasksSHA256`, `SnipStudioTrustedPowerShellPath` and `SnipStudioTrustedPowerShellSHA256` from the reviewed signature receipts. The PowerShell host must already have its valid original publisher signature verified before MSBuild begins.
5. Build the WinUI app. The targets pin the vendor source, check both executable hashes, verify the task's Automation Forge signature and timestamp through the preverified PowerShell host, and register the library with `UsingTask Override="true"`.

The override has no missing-file or invalid-signature fallback to the inline factory. If the task DLL, timestamp, publisher, expected hash, verifier host or vendor source is absent or different, the manifest target fails before loading the task assembly.

This reviewed C# build also fails before `PrepareForBuild`/`GetVCInstallPath` if `VsInstallRoot` is nonempty. That property can activate WinUI's separate `GetLatestMSVCVersion` inline task. Visual C++ build paths require a separate review and signed implementation before enabling them; this migration does not need them.

`Override="true"` is documented for MSBuild 17.2 and newer; this migration uses the audited .NET 10 SDK host. See [Microsoft's UsingTask documentation](https://learn.microsoft.com/en-us/visualstudio/msbuild/usingtask-element-msbuild) and [GetFileHash task](https://learn.microsoft.com/en-us/visualstudio/msbuild/getfilehash-task).

The fixtures exercise native class registration, proxy/stub registration, the two vendor exclusions, remaining redirected DLLs and XML output. Compile/sign verification and build execution must remain separately evidenced; source equivalence does not prove a successful WinUI launch.

After signing, run `ManifestEquivalence.proj` through the audited MSBuild host with the same four trust properties and the restored `NuGetPackageRoot`. This raw fixture project does not import NuGet's generated props, so that package-root property must be supplied explicitly. `FixtureOutputDirectory`, if supplied, must end in a directory separator. The fixture uses the signed task and Microsoft built-in XML checks; it does not compile or execute the original inline fragment or another test executable.
