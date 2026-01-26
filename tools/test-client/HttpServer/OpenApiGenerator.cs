using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;

namespace JunimoTestClient.HttpServer;

/// <summary>
/// Generates OpenAPI 3.1 specification for the Test API.
/// </summary>
public static class OpenApiGenerator
{
    public static OpenApiDocument Generate()
    {
        var document = new OpenApiDocument
        {
            Info = new OpenApiInfo
            {
                Title = "JunimoTestClient API",
                Description = "HTTP API for automated testing of Stardew Valley client",
                Version = "0.1.0",
                Contact = new OpenApiContact
                {
                    Name = "JunimoHost"
                }
            },
            Servers = new List<OpenApiServer>
            {
                new() { Url = "http://localhost:5123", Description = "Local test server" }
            },
            Paths = new OpenApiPaths(),
            Components = new OpenApiComponents
            {
                Schemas = BuildSchemas()
            }
        };

        // Status endpoints
        AddPath(document, "/ping", "GET", "Status", "Health check",
            "Simple ping endpoint to verify the API is responding",
            null, "PingResponse");

        AddPath(document, "/status", "GET", "Status", "Get game status",
            "Returns overall game status including menu, connection, and farmer info",
            null, "StatusResponse");

        AddPath(document, "/menu", "GET", "Status", "Get current menu",
            "Returns information about the currently active menu",
            null, "MenuInfo");

        AddPath(document, "/menu/buttons", "GET", "Status", "Get menu buttons",
            "Returns clickable buttons in the current menu",
            null, "MenuButtonsInfo");

        AddPath(document, "/menu/slots", "GET", "Status", "Get menu slots",
            "Returns slots in the current menu (LoadGameMenu, CoopMenu, etc.)",
            null, "MenuSlotsInfo");

        AddPath(document, "/connection", "GET", "Status", "Get connection status",
            "Returns multiplayer connection status",
            null, "ConnectionStatus");

        AddPath(document, "/farmer", "GET", "Status", "Get farmer info",
            "Returns current farmer information (null if not in game)",
            null, "FarmerInfo");

        // Navigation endpoints
        AddPath(document, "/navigate", "POST", "Navigation", "Navigate to menu",
            "Navigate to a specific menu (e.g., 'coop', 'load', 'title')",
            "NavigateRequest", "NavigationResult");

        AddPath(document, "/coop/tab", "POST", "Navigation", "Switch coop tab",
            "Switch between tabs in the co-op menu (0=Join, 1=Host)",
            "CoopTabRequest", "NavigationResult");

        AddPath(document, "/exit", "POST", "Navigation", "Exit to title",
            "Exit the current game and return to title screen",
            null, "NavigationResult");

        // Co-op endpoints
        AddPath(document, "/coop/join-invite", "POST", "Co-op", "Join via invite code",
            "Enter an invite code to join a co-op game",
            "JoinInviteRequest", "JoinResult");

        AddPath(document, "/coop/join-lan", "POST", "Co-op", "Join via LAN",
            "Join a co-op game via LAN/IP address",
            "JoinLanRequest", "JoinResult");

        AddPath(document, "/farmhands", "GET", "Co-op", "Get farmhand slots",
            "Get available farmhand slots when connecting to a server",
            null, "FarmhandSelectionInfo");

        AddPath(document, "/farmhands/select", "POST", "Co-op", "Select farmhand",
            "Select a farmhand slot to join the game",
            "SelectFarmhandRequest", "JoinResult");

        // Chat endpoints
        AddPath(document, "/chat/send", "POST", "Chat", "Send chat message",
            "Send a chat message to all players",
            "ChatSendRequest", "ChatResult");

        AddPath(document, "/chat/info", "POST", "Chat", "Send local info",
            "Display a local info message (only visible to this client)",
            "ChatSendRequest", "ChatResult");

        AddPath(document, "/chat/history", "GET", "Chat", "Get chat history",
            "Get recent chat messages. Use ?count=N to limit results.",
            null, "ChatHistoryResult",
            new OpenApiParameter { Name = "count", In = ParameterLocation.Query, Schema = IntSchema(), Description = "Number of messages to return (default: 10)" });

        // Wait/polling endpoints
        AddPath(document, "/wait/menu", "GET", "Wait", "Wait for menu",
            "Block until a specific menu type is active",
            null, "WaitResult",
            new OpenApiParameter { Name = "type", In = ParameterLocation.Query, Required = true, Schema = StringSchema(), Description = "Menu type to wait for" },
            new OpenApiParameter { Name = "timeout", In = ParameterLocation.Query, Schema = IntSchema(), Description = "Timeout in milliseconds (default: 30000)" });

        AddPath(document, "/wait/connected", "GET", "Wait", "Wait for connection",
            "Block until connected to a server",
            null, "WaitResult",
            new OpenApiParameter { Name = "timeout", In = ParameterLocation.Query, Schema = IntSchema(), Description = "Timeout in milliseconds (default: 30000)" });

        AddPath(document, "/wait/world-ready", "GET", "Wait", "Wait for world ready",
            "Block until the world is fully loaded and ready",
            null, "WaitResult",
            new OpenApiParameter { Name = "timeout", In = ParameterLocation.Query, Schema = IntSchema(), Description = "Timeout in milliseconds (default: 30000)" });

        AddPath(document, "/wait/farmhands", "GET", "Wait", "Wait for farmhand menu",
            "Block until the farmhand selection menu appears",
            null, "WaitResult",
            new OpenApiParameter { Name = "timeout", In = ParameterLocation.Query, Schema = IntSchema(), Description = "Timeout in milliseconds (default: 30000)" });

        AddPath(document, "/wait/title", "GET", "Wait", "Wait for title screen",
            "Block until returned to the title screen",
            null, "WaitResult",
            new OpenApiParameter { Name = "timeout", In = ParameterLocation.Query, Schema = IntSchema(), Description = "Timeout in milliseconds (default: 30000)" });

        // Diagnostics endpoints
        AddPath(document, "/health", "GET", "Diagnostics", "Health watchdog status",
            "Returns health status including freeze detection",
            null, "HealthStatus");

        AddPath(document, "/stats", "GET", "Diagnostics", "Performance stats",
            "Returns performance statistics (FPS, tick time, memory)",
            null, "PerfStats");

        AddPath(document, "/stats/reset", "POST", "Diagnostics", "Reset stats",
            "Reset max tick tracking",
            null, "SuccessResponse");

        AddPath(document, "/errors", "GET", "Diagnostics", "Get captured errors",
            "Returns captured errors/exceptions",
            null, "ErrorsResponse",
            new OpenApiParameter { Name = "limit", In = ParameterLocation.Query, Schema = IntSchema(), Description = "Limit number of errors returned" },
            new OpenApiParameter { Name = "clear", In = ParameterLocation.Query, Schema = BoolSchema(), Description = "Clear errors after retrieving (true/1)" });

        AddPath(document, "/errors/clear", "POST", "Diagnostics", "Clear errors",
            "Clear all captured errors",
            null, "SuccessResponse");

        AddPath(document, "/screenshot", "POST", "Diagnostics", "Capture screenshot",
            "Capture a screenshot and return as base64 PNG",
            null, "ScreenshotResult");

        AddPath(document, "/screenshot/file", "POST", "Diagnostics", "Save screenshot",
            "Capture a screenshot and save to file",
            "ScreenshotRequest", "ScreenshotResult");

        // Meta endpoints
        AddPath(document, "/openapi.json", "GET", "Meta", "OpenAPI spec (JSON)",
            "Returns this OpenAPI specification in JSON format",
            null, null);

        AddPath(document, "/openapi.yaml", "GET", "Meta", "OpenAPI spec (YAML)",
            "Returns this OpenAPI specification in YAML format",
            null, null);

        AddPath(document, "/docs", "GET", "Meta", "API documentation (Scalar)",
            "Interactive API documentation powered by Scalar",
            null, null);

        AddPath(document, "/swagger", "GET", "Meta", "API documentation (Swagger)",
            "Interactive API documentation powered by Swagger UI",
            null, null);

        return document;
    }

    public static string GenerateJson()
    {
        var document = Generate();
        return document.SerializeAsJson(OpenApiSpecVersion.OpenApi3_0);
    }

    public static string GenerateYaml()
    {
        var document = Generate();
        return document.SerializeAsYaml(OpenApiSpecVersion.OpenApi3_0);
    }

    private static void AddPath(OpenApiDocument doc, string path, string method, string tag,
        string summary, string description, string? requestSchema, string? responseSchema,
        params OpenApiParameter[] parameters)
    {
        if (!doc.Paths.ContainsKey(path))
        {
            doc.Paths[path] = new OpenApiPathItem();
        }

        var operation = new OpenApiOperation
        {
            Tags = new List<OpenApiTag> { new() { Name = tag } },
            Summary = summary,
            Description = description,
            Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse
                {
                    Description = "Success",
                    Content = responseSchema != null ? new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = responseSchema } }
                        }
                    } : new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = "object" } }
                    }
                },
                ["500"] = new OpenApiResponse
                {
                    Description = "Server error",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "ErrorResponse" } }
                        }
                    }
                }
            }
        };

        if (requestSchema != null)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = requestSchema } }
                    }
                }
            };
        }

        foreach (var param in parameters)
        {
            operation.Parameters.Add(param);
        }

        var operationType = method.ToUpper() switch
        {
            "GET" => OperationType.Get,
            "POST" => OperationType.Post,
            "DELETE" => OperationType.Delete,
            _ => OperationType.Get
        };

        doc.Paths[path].Operations[operationType] = operation;
    }

    private static OpenApiSchema StringSchema() => new() { Type = "string" };
    private static OpenApiSchema IntSchema() => new() { Type = "integer" };
    private static OpenApiSchema BoolSchema() => new() { Type = "boolean" };

    private static Dictionary<string, OpenApiSchema> BuildSchemas()
    {
        return new Dictionary<string, OpenApiSchema>
        {
            // Common responses
            ["ErrorResponse"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["error"] = new() { Type = "string" },
                    ["path"] = new() { Type = "string" }
                }
            },
            ["SuccessResponse"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["success"] = new() { Type = "boolean" },
                    ["message"] = new() { Type = "string" }
                }
            },

            // Status responses
            ["PingResponse"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["pong"] = new() { Type = "boolean" },
                    ["timestamp"] = new() { Type = "integer", Format = "int64" }
                }
            },
            ["StatusResponse"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["menu"] = new() { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "MenuInfo" } },
                    ["connection"] = new() { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "ConnectionStatus" } },
                    ["farmer"] = new() { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "FarmerInfo" }, Nullable = true },
                    ["timestamp"] = new() { Type = "integer", Format = "int64" }
                }
            },
            ["MenuInfo"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["type"] = new() { Type = "string", Description = "Short menu type name" },
                    ["fullType"] = new() { Type = "string", Description = "Full type name including namespace" },
                    ["subMenu"] = new() { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "MenuInfo" }, Nullable = true },
                    ["titleMenuState"] = new() { Type = "string", Nullable = true, Description = "Title menu state (if on title)" },
                    ["coopMenuInfo"] = new() { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "CoopMenuInfo" }, Nullable = true },
                    ["isInGame"] = new() { Type = "boolean" },
                    ["gameMode"] = new() { Type = "integer" }
                }
            },
            ["CoopMenuInfo"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["currentTab"] = new() { Type = "integer" },
                    ["tabNames"] = new() { Type = "array", Items = new OpenApiSchema { Type = "string" } }
                }
            },
            ["MenuButtonsInfo"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["menuType"] = new() { Type = "string" },
                    ["buttons"] = new() { Type = "array", Items = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "ButtonInfo" } } }
                }
            },
            ["ButtonInfo"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["name"] = new() { Type = "string" },
                    ["label"] = new() { Type = "string" },
                    ["bounds"] = new() { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "Bounds" } },
                    ["visible"] = new() { Type = "boolean" }
                }
            },
            ["Bounds"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["x"] = new() { Type = "integer" },
                    ["y"] = new() { Type = "integer" },
                    ["width"] = new() { Type = "integer" },
                    ["height"] = new() { Type = "integer" }
                }
            },
            ["MenuSlotsInfo"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["menuType"] = new() { Type = "string" },
                    ["slots"] = new() { Type = "array", Items = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "SlotInfo" } } }
                }
            },
            ["SlotInfo"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["index"] = new() { Type = "integer" },
                    ["label"] = new() { Type = "string" },
                    ["isEmpty"] = new() { Type = "boolean" },
                    ["farmerName"] = new() { Type = "string", Nullable = true },
                    ["farmName"] = new() { Type = "string", Nullable = true }
                }
            },
            ["ConnectionStatus"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["isMultiplayer"] = new() { Type = "boolean" },
                    ["isClient"] = new() { Type = "boolean" },
                    ["isServer"] = new() { Type = "boolean" },
                    ["isLocalMultiplayer"] = new() { Type = "boolean" },
                    ["isConnected"] = new() { Type = "boolean" },
                    ["worldReady"] = new() { Type = "boolean" },
                    ["hasLoadedGame"] = new() { Type = "boolean" },
                    ["numberOfPlayers"] = new() { Type = "integer" }
                }
            },
            ["FarmerInfo"] = new OpenApiSchema
            {
                Type = "object",
                Nullable = true,
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["name"] = new() { Type = "string" },
                    ["farmName"] = new() { Type = "string" },
                    ["money"] = new() { Type = "integer" },
                    ["totalMoneyEarned"] = new() { Type = "integer" },
                    ["currentLocation"] = new() { Type = "string" },
                    ["position"] = new() { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "Position" } },
                    ["health"] = new() { Type = "integer" },
                    ["maxHealth"] = new() { Type = "integer" },
                    ["stamina"] = new() { Type = "number" },
                    ["maxStamina"] = new() { Type = "integer" }
                }
            },
            ["Position"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["x"] = new() { Type = "number" },
                    ["y"] = new() { Type = "number" }
                }
            },

            // Request schemas
            ["NavigateRequest"] = new OpenApiSchema
            {
                Type = "object",
                Required = new HashSet<string> { "target" },
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["target"] = new() { Type = "string", Description = "Menu to navigate to: 'coop', 'load', 'title', 'new', 'options'" }
                }
            },
            ["CoopTabRequest"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["tab"] = new() { Type = "integer", Description = "Tab index (0=Join, 1=Host)" }
                }
            },
            ["JoinInviteRequest"] = new OpenApiSchema
            {
                Type = "object",
                Required = new HashSet<string> { "inviteCode" },
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["inviteCode"] = new() { Type = "string", Description = "Invite code from host" }
                }
            },
            ["JoinLanRequest"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["address"] = new() { Type = "string", Description = "IP address or hostname (default: localhost)" }
                }
            },
            ["SelectFarmhandRequest"] = new OpenApiSchema
            {
                Type = "object",
                Required = new HashSet<string> { "slotIndex" },
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["slotIndex"] = new() { Type = "integer", Description = "Farmhand slot index to select" }
                }
            },
            ["ChatSendRequest"] = new OpenApiSchema
            {
                Type = "object",
                Required = new HashSet<string> { "message" },
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["message"] = new() { Type = "string", Description = "Message to send" }
                }
            },
            ["ScreenshotRequest"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["filename"] = new() { Type = "string", Nullable = true, Description = "Optional filename (auto-generated if not provided)" }
                }
            },

            // Result schemas
            ["NavigationResult"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["success"] = new() { Type = "boolean" },
                    ["error"] = new() { Type = "string", Nullable = true },
                    ["previousMenu"] = new() { Type = "string", Nullable = true },
                    ["currentMenu"] = new() { Type = "string", Nullable = true }
                }
            },
            ["JoinResult"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["success"] = new() { Type = "boolean" },
                    ["error"] = new() { Type = "string", Nullable = true },
                    ["message"] = new() { Type = "string", Nullable = true }
                }
            },
            ["ChatResult"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["success"] = new() { Type = "boolean" },
                    ["error"] = new() { Type = "string", Nullable = true }
                }
            },
            ["ChatHistoryResult"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["success"] = new() { Type = "boolean" },
                    ["error"] = new() { Type = "string", Nullable = true },
                    ["messages"] = new() { Type = "array", Items = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "ChatMessage" } } }
                }
            },
            ["ChatMessage"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["text"] = new() { Type = "string" },
                    ["senderName"] = new() { Type = "string", Nullable = true },
                    ["timestamp"] = new() { Type = "string", Format = "date-time" }
                }
            },
            ["WaitResult"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["success"] = new() { Type = "boolean" },
                    ["condition"] = new() { Type = "string", Nullable = true },
                    ["error"] = new() { Type = "string", Nullable = true },
                    ["waitedMs"] = new() { Type = "integer" }
                }
            },
            ["FarmhandSelectionInfo"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["available"] = new() { Type = "boolean" },
                    ["error"] = new() { Type = "string", Nullable = true },
                    ["slots"] = new() { Type = "array", Items = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "FarmhandSlot" } } }
                }
            },
            ["FarmhandSlot"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["index"] = new() { Type = "integer" },
                    ["isEmpty"] = new() { Type = "boolean" },
                    ["farmerName"] = new() { Type = "string", Nullable = true },
                    ["farmName"] = new() { Type = "string", Nullable = true }
                }
            },

            // Diagnostics schemas
            ["HealthStatus"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["healthy"] = new() { Type = "boolean" },
                    ["tickCount"] = new() { Type = "integer", Format = "int64" },
                    ["msSinceLastTick"] = new() { Type = "integer" },
                    ["isFrozen"] = new() { Type = "boolean" },
                    ["freezeThresholdMs"] = new() { Type = "integer" },
                    ["lastUnhealthyReason"] = new() { Type = "string", Nullable = true },
                    ["uptimeSeconds"] = new() { Type = "integer" }
                }
            },
            ["PerfStats"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["fps"] = new() { Type = "number" },
                    ["targetFps"] = new() { Type = "integer" },
                    ["lastTickMs"] = new() { Type = "number" },
                    ["avgTickMs"] = new() { Type = "number" },
                    ["maxTickMs"] = new() { Type = "number" },
                    ["memoryMb"] = new() { Type = "number" },
                    ["gcGen0"] = new() { Type = "integer" },
                    ["gcGen1"] = new() { Type = "integer" },
                    ["gcGen2"] = new() { Type = "integer" },
                    ["tickHistorySize"] = new() { Type = "integer" }
                }
            },
            ["ErrorsResponse"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["totalCount"] = new() { Type = "integer" },
                    ["errors"] = new() { Type = "array", Items = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "CapturedError" } } }
                }
            },
            ["CapturedError"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["id"] = new() { Type = "string" },
                    ["timestamp"] = new() { Type = "string", Format = "date-time" },
                    ["source"] = new() { Type = "string" },
                    ["message"] = new() { Type = "string" },
                    ["stackTrace"] = new() { Type = "string", Nullable = true },
                    ["exceptionType"] = new() { Type = "string", Nullable = true }
                }
            },
            ["ScreenshotResult"] = new OpenApiSchema
            {
                Type = "object",
                Properties = new Dictionary<string, OpenApiSchema>
                {
                    ["success"] = new() { Type = "boolean" },
                    ["error"] = new() { Type = "string", Nullable = true },
                    ["filePath"] = new() { Type = "string", Nullable = true },
                    ["filename"] = new() { Type = "string", Nullable = true },
                    ["width"] = new() { Type = "integer" },
                    ["height"] = new() { Type = "integer" },
                    ["sizeBytes"] = new() { Type = "integer", Format = "int64" },
                    ["base64Png"] = new() { Type = "string", Nullable = true, Description = "Base64-encoded PNG image data" }
                }
            }
        };
    }
}
