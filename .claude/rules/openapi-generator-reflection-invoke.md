---
paths:
  - "tools/openapi-generator/**"
  - "mod/JunimoServer/Services/Api/OpenApiGenerator.cs"
---

# openapi-generator calls Generate by reflection — optional params break the Docker build, not `dotnet build`

`tools/openapi-generator/Program.cs` loads the built `JunimoServer.dll` via `Assembly.LoadFrom` and calls `OpenApiGenerator.Generate` through `MethodInfo.Invoke` with a **fixed positional `object?[]`**. The Docker server-image build runs this tool (`docker/Dockerfile`) to emit `/data/openapi.json` for the public docs.

Adding an **optional** parameter to `Generate` is source-compatible for normal callers but breaks this one: `MethodInfo.Invoke` does NOT apply C# optional-argument defaults (compile-time only). An N-arg Invoke against an (N+1)-param method throws `TargetParameterCountException` → tool exits non-zero → Docker `RUN` fails (exit 2), while `dotnet build` of the mod stays green. A no-consumer trap where the consumer is a reflection caller, invisible to grep and the compiler. (Already enforced: `Generate` takes an `includeMethod` predicate that `Program.cs` passes explicitly for this reason.)

**How to apply:** When changing `Generate`'s signature (or any reflection-invoked method), pass an explicit arg for every parameter in `Program.cs` — never rely on defaults. Verify against the container path, not `dotnet build`: the tool is net10.0, the mod net6.0, so reproduce by rebuilding BOTH in Release, then `dotnet <net10-tool>.dll <Release/net6.0/JunimoServer.dll> out.json` and assert exit 0. Cross-runtime + stale Debug/Release DLLs give misleading errors (`Could not resolve type ...d__NNN`, "Parameter count mismatch") that vanish once both artifacts are fresh.
