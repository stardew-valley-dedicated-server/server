using System;

namespace JunimoServer.Services.Api;

/// <summary>
/// Marks a method as an API endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class ApiEndpointAttribute : Attribute
{
    public string Method { get; }
    public string Path { get; }
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? Tag { get; set; }

    /// <summary>
    /// Served without the API key even when <c>API_KEY</c> is set. The request router and the
    /// OpenAPI security model both read this flag, so it is the only place an endpoint's auth
    /// requirement is declared. Never set on a <c>/test/*</c> route: those must stay
    /// indistinguishable from unknown routes in production (401 without a key, 404 with one).
    /// </summary>
    public bool Public { get; set; }

    public ApiEndpointAttribute(string method, string path)
    {
        Method = method.ToUpperInvariant();
        Path = path;
    }
}

/// <summary>
/// Specifies the response type for an API endpoint.
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
