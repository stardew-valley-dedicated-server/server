using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using NJsonSchema;
using NJsonSchema.Generation;
using NSwag;

namespace JunimoServer.Services.Api
{
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
                SchemaType = SchemaType.OpenApi3,
                Info = new OpenApiInfo
                {
                    Title = title,
                    Version = version,
                    Description = description
                }
            };

            var methods = serviceType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(m => m.GetCustomAttribute<ApiEndpointAttribute>() != null);

            foreach (var method in methods)
            {
                var endpoint = method.GetCustomAttribute<ApiEndpointAttribute>()!;
                var responses = method.GetCustomAttributes<ApiResponseAttribute>().ToList();

                var operation = new OpenApiOperation
                {
                    Summary = endpoint.Summary,
                    Description = endpoint.Description,
                    OperationId = method.Name
                };

                if (!string.IsNullOrEmpty(endpoint.Tag))
                {
                    operation.Tags.Add(endpoint.Tag);
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
                        apiResponse.Content["application/json"] = new OpenApiMediaType
                        {
                            Schema = schema
                        };
                    }

                    operation.Responses[response.StatusCode.ToString()] = apiResponse;
                }

                // Ensure at least one response
                if (!operation.Responses.Any())
                {
                    operation.Responses["200"] = new OpenApiResponse { Description = "Success" };
                }

                // Add to document
                if (!document.Paths.ContainsKey(endpoint.Path))
                {
                    document.Paths[endpoint.Path] = new OpenApiPathItem();
                }

                var pathItem = document.Paths[endpoint.Path];
                var operationMethod = endpoint.Method switch
                {
                    "GET" => OpenApiOperationMethod.Get,
                    "POST" => OpenApiOperationMethod.Post,
                    "PUT" => OpenApiOperationMethod.Put,
                    "DELETE" => OpenApiOperationMethod.Delete,
                    "PATCH" => OpenApiOperationMethod.Patch,
                    "HEAD" => OpenApiOperationMethod.Head,
                    "OPTIONS" => OpenApiOperationMethod.Options,
                    _ => OpenApiOperationMethod.Get
                };
                pathItem[operationMethod] = operation;
            }

            return document;
        }

        private static JsonSchema GenerateSchema(Type type, OpenApiDocument document)
        {
            var schemaName = type.Name;

            // Check if schema already exists
            if (document.Components.Schemas.TryGetValue(schemaName, out var existingSchema))
            {
                return new JsonSchema { Reference = existingSchema };
            }

            var schema = new JsonSchema
            {
                Type = JsonObjectType.Object
            };

            // Get XML documentation if available (from DescriptionAttribute)
            var typeDescription = type.GetCustomAttribute<DescriptionAttribute>();
            if (typeDescription != null)
            {
                schema.Description = typeDescription.Description;
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propSchema = GetPropertySchema(prop.PropertyType);

                // Get description from XML doc summary or DescriptionAttribute
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
            return new JsonSchema { Reference = schema };
        }

        private static JsonSchemaProperty GetPropertySchema(Type type)
        {
            // Handle nullable types
            var underlyingType = Nullable.GetUnderlyingType(type);
            if (underlyingType != null)
            {
                var innerSchema = GetPropertySchema(underlyingType);
                innerSchema.IsNullableRaw = true;
                return innerSchema;
            }

            // Handle common types
            if (type == typeof(string))
                return new JsonSchemaProperty { Type = JsonObjectType.String };
            if (type == typeof(int) || type == typeof(long) || type == typeof(short))
                return new JsonSchemaProperty { Type = JsonObjectType.Integer };
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return new JsonSchemaProperty { Type = JsonObjectType.Number };
            if (type == typeof(bool))
                return new JsonSchemaProperty { Type = JsonObjectType.Boolean };
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
                return new JsonSchemaProperty { Type = JsonObjectType.String, Format = "date-time" };

            // Handle arrays and lists
            if (type.IsArray)
            {
                return new JsonSchemaProperty
                {
                    Type = JsonObjectType.Array,
                    Item = GetPropertySchema(type.GetElementType()!)
                };
            }

            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                if (genericDef == typeof(List<>) || genericDef == typeof(IList<>) ||
                    genericDef == typeof(IEnumerable<>) || genericDef == typeof(ICollection<>))
                {
                    return new JsonSchemaProperty
                    {
                        Type = JsonObjectType.Array,
                        Item = GetPropertySchema(type.GetGenericArguments()[0])
                    };
                }
            }

            // For complex types, generate inline schema
            var schema = new JsonSchemaProperty { Type = JsonObjectType.Object };
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propName = char.ToLowerInvariant(prop.Name[0]) + prop.Name.Substring(1);
                schema.Properties[propName] = GetPropertySchema(prop.PropertyType);
            }
            return schema;
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
}
