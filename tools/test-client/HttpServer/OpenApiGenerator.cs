using System.ComponentModel;
using System.Reflection;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;

namespace JunimoTestClient.HttpServer;

/// <summary>
/// Generates OpenAPI specification from methods decorated with API attributes.
/// </summary>
public static class OpenApiGenerator
{
    /// <summary>
    /// Generates an OpenAPI document from a type's methods decorated with ApiEndpoint attributes.
    /// </summary>
    public static OpenApiDocument Generate(Type serviceType, string title, string version, string? description = null)
    {
        var document = new OpenApiDocument
        {
            Info = new OpenApiInfo
            {
                Title = title,
                Version = version,
                Description = description,
                Contact = new OpenApiContact { Name = "JunimoHost" }
            },
            Servers = new List<OpenApiServer>
            {
                new() { Url = "http://localhost:5123", Description = "Local test server" }
            },
            Paths = new OpenApiPaths(),
            Components = new OpenApiComponents { Schemas = new Dictionary<string, OpenApiSchema>() }
        };

        // Add common error schema
        document.Components.Schemas["ErrorResponse"] = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                ["error"] = new() { Type = "string" },
                ["path"] = new() { Type = "string" }
            }
        };

        var methods = serviceType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.GetCustomAttribute<ApiEndpointAttribute>() != null);

        foreach (var method in methods)
        {
            var endpoint = method.GetCustomAttribute<ApiEndpointAttribute>()!;
            var responses = method.GetCustomAttributes<ApiResponseAttribute>().ToList();
            var queryParams = method.GetCustomAttributes<ApiQueryParamAttribute>().ToList();
            var requestBody = method.GetCustomAttribute<ApiRequestBodyAttribute>();

            var operation = new OpenApiOperation
            {
                Summary = endpoint.Summary,
                Description = endpoint.Description,
                OperationId = method.Name,
                Responses = new OpenApiResponses()
            };

            if (!string.IsNullOrEmpty(endpoint.Tag))
            {
                operation.Tags.Add(new OpenApiTag { Name = endpoint.Tag });
            }

            // Add query parameters
            foreach (var param in queryParams)
            {
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = param.Name,
                    In = ParameterLocation.Query,
                    Required = param.Required,
                    Description = param.Description,
                    Schema = GetSchemaForType(param.Type)
                });
            }

            // Add request body
            if (requestBody != null)
            {
                var bodySchema = GenerateSchema(requestBody.BodyType, document);
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = requestBody.Required,
                    Description = requestBody.Description,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType { Schema = bodySchema }
                    }
                };
            }

            // Add responses
            foreach (var response in responses)
            {
                var apiResponse = new OpenApiResponse
                {
                    Description = response.Description ?? GetDefaultDescription(response.StatusCode)
                };

                if (response.ResponseType != typeof(void))
                {
                    var schema = GenerateSchema(response.ResponseType, document);
                    apiResponse.Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType { Schema = schema }
                    };
                }

                operation.Responses[response.StatusCode.ToString()] = apiResponse;
            }

            // Ensure at least one response
            if (!operation.Responses.Any())
            {
                operation.Responses["200"] = new OpenApiResponse { Description = "Success" };
            }

            // Add 500 error response
            operation.Responses["500"] = new OpenApiResponse
            {
                Description = "Server error",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "ErrorResponse" }
                        }
                    }
                }
            };

            // Add to document
            if (!document.Paths.ContainsKey(endpoint.Path))
            {
                document.Paths[endpoint.Path] = new OpenApiPathItem();
            }

            var pathItem = document.Paths[endpoint.Path];
            var operationType = endpoint.Method switch
            {
                "GET" => OperationType.Get,
                "POST" => OperationType.Post,
                "PUT" => OperationType.Put,
                "DELETE" => OperationType.Delete,
                "PATCH" => OperationType.Patch,
                "HEAD" => OperationType.Head,
                "OPTIONS" => OperationType.Options,
                _ => OperationType.Get
            };
            pathItem.Operations[operationType] = operation;
        }

        return document;
    }

    public static string GenerateJson(Type serviceType, string title, string version, string? description = null)
    {
        var document = Generate(serviceType, title, version, description);
        return document.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
    }

    public static string GenerateYaml(Type serviceType, string title, string version, string? description = null)
    {
        var document = Generate(serviceType, title, version, description);
        return document.SerializeAsYaml(OpenApiSpecVersion.OpenApi3_0);
    }

    private static OpenApiSchema GenerateSchema(Type type, OpenApiDocument document)
    {
        var schemaName = type.Name;

        // Check if schema already exists
        if (document.Components.Schemas.ContainsKey(schemaName))
        {
            return new OpenApiSchema
            {
                Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = schemaName }
            };
        }

        var schema = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>()
        };

        // Get description from DescriptionAttribute
        var typeDescription = type.GetCustomAttribute<DescriptionAttribute>();
        if (typeDescription != null)
        {
            schema.Description = typeDescription.Description;
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var propSchema = GetPropertySchema(prop.PropertyType, document);

            // Get description from DescriptionAttribute
            var descAttr = prop.GetCustomAttribute<DescriptionAttribute>();
            if (descAttr != null)
            {
                propSchema.Description = descAttr.Description;
            }

            // Use camelCase for property names
            var propName = char.ToLowerInvariant(prop.Name[0]) + prop.Name.Substring(1);
            schema.Properties[propName] = propSchema;
        }

        // Register schema in components
        document.Components.Schemas[schemaName] = schema;

        // Return a reference to the schema
        return new OpenApiSchema
        {
            Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = schemaName }
        };
    }

    private static OpenApiSchema GetPropertySchema(Type type, OpenApiDocument document)
    {
        // Handle nullable types
        var underlyingType = Nullable.GetUnderlyingType(type);
        if (underlyingType != null)
        {
            var innerSchema = GetPropertySchema(underlyingType, document);
            innerSchema.Nullable = true;
            return innerSchema;
        }

        // Handle common types
        if (type == typeof(string))
            return new OpenApiSchema { Type = "string" };
        if (type == typeof(int) || type == typeof(long) || type == typeof(short))
            return new OpenApiSchema { Type = "integer" };
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return new OpenApiSchema { Type = "number" };
        if (type == typeof(bool))
            return new OpenApiSchema { Type = "boolean" };
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return new OpenApiSchema { Type = "string", Format = "date-time" };

        // Handle arrays
        if (type.IsArray)
        {
            return new OpenApiSchema
            {
                Type = "array",
                Items = GetPropertySchema(type.GetElementType()!, document)
            };
        }

        // Handle generic collections
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            if (genericDef == typeof(List<>) || genericDef == typeof(IList<>) ||
                genericDef == typeof(IEnumerable<>) || genericDef == typeof(ICollection<>))
            {
                return new OpenApiSchema
                {
                    Type = "array",
                    Items = GetPropertySchema(type.GetGenericArguments()[0], document)
                };
            }
        }

        // For complex types, generate and reference the schema
        return GenerateSchema(type, document);
    }

    private static OpenApiSchema GetSchemaForType(Type type)
    {
        if (type == typeof(string))
            return new OpenApiSchema { Type = "string" };
        if (type == typeof(int) || type == typeof(long))
            return new OpenApiSchema { Type = "integer" };
        if (type == typeof(bool))
            return new OpenApiSchema { Type = "boolean" };
        if (type == typeof(float) || type == typeof(double))
            return new OpenApiSchema { Type = "number" };
        return new OpenApiSchema { Type = "string" };
    }

    private static string GetDefaultDescription(int statusCode) => statusCode switch
    {
        200 => "Success",
        201 => "Created",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "Response"
    };
}
