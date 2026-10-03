namespace DynCMS.Core.Api;

/// <summary>Settings for the management REST API and the MCP server.</summary>
public sealed class DynCmsApiOptions
{
    /// <summary>Map the REST API. Turn off to run a back office without any programmatic access.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base path of the REST API.</summary>
    public string BasePath { get; set; } = "/api/v1";

    /// <summary>Map the MCP server (Streamable HTTP) for AI agents.</summary>
    public bool McpEnabled { get; set; } = true;

    /// <summary>Path of the MCP endpoint.</summary>
    public string McpPath { get; set; } = "/mcp";

    /// <summary>Serve the OpenAPI document at <c>{BasePath}/openapi.json</c> without authentication.</summary>
    public bool PublicOpenApi { get; set; } = true;

    /// <summary>Largest JSON body (base64 uploads included) the API accepts, in bytes.</summary>
    public long MaxRequestBodyBytes { get; set; } = 32 * 1024 * 1024;
}
