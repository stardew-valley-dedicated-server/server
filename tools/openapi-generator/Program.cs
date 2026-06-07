using System.Reflection;
using System.Text;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: openapi-generator <path-to-JunimoServer.dll> [output-file]");
    Console.Error.WriteLine("  If output-file is omitted, outputs to stdout");
    return 1;
}

var dllPath = args[0];
var outputPath = args.Length > 1 ? args[1] : null;

if (!File.Exists(dllPath))
{
    Console.Error.WriteLine($"Error: File not found: {dllPath}");
    return 1;
}

try
{
    // Load the mod assembly
    var asm = Assembly.LoadFrom(dllPath);

    // Get the types we need
    var generatorType = asm.GetType("JunimoServer.Services.Api.OpenApiGenerator")
        ?? throw new Exception("OpenApiGenerator type not found");
    var apiServiceType = asm.GetType("JunimoServer.Services.Api.ApiService")
        ?? throw new Exception("ApiService type not found");

    // Get the Generate method
    var generateMethod = generatorType.GetMethod("Generate", BindingFlags.Public | BindingFlags.Static)
        ?? throw new Exception("Generate method not found");

    // Exclude test-only endpoints (/test/*) from the published spec — this build-time
    // spec feeds the public operator docs and should match what a production server
    // exposes (which gates /test/* off via Env.IsTest). The prefix is duplicated here
    // because this tool loads the mod by reflection and can't reference its IsTestPath.
    // NOTE: Generate has an optional includeMethod parameter; MethodInfo.Invoke does not
    // apply C# optional-argument defaults, so this argument must be passed explicitly.
    var apiEndpointAttrType = asm.GetType("JunimoServer.Services.Api.ApiEndpointAttribute")
        ?? throw new Exception("ApiEndpointAttribute type not found");
    var apiEndpointPathProp = apiEndpointAttrType.GetProperty("Path")
        ?? throw new Exception("ApiEndpointAttribute.Path property not found");

    // Read ONLY the typed ApiEndpointAttribute. Do not enumerate all attributes
    // (m.GetCustomAttributes()) — that materializes [AsyncStateMachine], whose
    // constructor argument is the compiler-generated state-machine type, and resolving
    // that nested type across the tool/mod runtime boundary (net10 tool reflecting a
    // net6 mod assembly) throws "Could not resolve type ...d__NNN".
    Func<MethodInfo, bool> includeMethod = m =>
    {
        var ep = Attribute.GetCustomAttribute(m, apiEndpointAttrType);
        var path = ep is null ? null : apiEndpointPathProp.GetValue(ep) as string;
        return path is null || !path.StartsWith("/test/", StringComparison.Ordinal);
    };

    // Invoke it
    var document = generateMethod.Invoke(null, new object?[]
    {
        apiServiceType,
        "Stardew Dedicated Server API",
        "v1",
        "HTTP API for monitoring and interacting with the Stardew Valley dedicated server",
        includeMethod
    }) ?? throw new Exception("Generate returned null");

    // Call ToJson() on the OpenApiDocument
    var toJsonMethod = document.GetType().GetMethod("ToJson", Type.EmptyTypes)
        ?? throw new Exception("ToJson method not found");
    var json = (string)(toJsonMethod.Invoke(document, null) ?? "{}");

    // Output - ensure UTF-8 without BOM
    if (outputPath != null)
    {
        File.WriteAllText(outputPath, json, new UTF8Encoding(false));
        Console.Error.WriteLine($"OpenAPI spec written to: {outputPath}");
    }
    else
    {
        Console.WriteLine(json);
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    if (ex.InnerException != null)
    {
        Console.Error.WriteLine($"Inner: {ex.InnerException.Message}");
    }
    return 1;
}
