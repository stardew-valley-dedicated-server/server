namespace JunimoTestClient.HttpServer;

/// <summary>
/// Marks a method as an API endpoint for OpenAPI documentation generation.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class ApiEndpointAttribute : Attribute
{
    public string Method { get; }
    public string Path { get; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? Tag { get; set; }

    public ApiEndpointAttribute(string method, string path)
    {
        Method = method.ToUpperInvariant();
        Path = path;
    }
}

/// <summary>
/// Specifies a response type for an API endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class ApiResponseAttribute : Attribute
{
    public Type ResponseType { get; }
    public int StatusCode { get; }
    public string? Description { get; set; }

    public ApiResponseAttribute(Type responseType, int statusCode = 200)
    {
        ResponseType = responseType;
        StatusCode = statusCode;
    }
}

/// <summary>
/// Specifies a query parameter for an API endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class ApiQueryParamAttribute : Attribute
{
    public string Name { get; }
    public Type Type { get; }
    public bool Required { get; set; }
    public string? Description { get; set; }

    public ApiQueryParamAttribute(string name, Type type)
    {
        Name = name;
        Type = type;
    }
}

/// <summary>
/// Specifies the request body type for an API endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class ApiRequestBodyAttribute : Attribute
{
    public Type BodyType { get; }
    public bool Required { get; set; } = true;
    public string? Description { get; set; }

    public ApiRequestBodyAttribute(Type bodyType)
    {
        BodyType = bodyType;
    }
}
