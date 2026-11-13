using EfLens.Providers;
using ModelContextProtocol;

namespace EfLens.Tools;

internal static class ToolErrors
{
    /// <summary>
    /// EfLens errors are written for the agent ("table not found, use list_tables"), so they travel as MCP tool errors.
    /// Anything else keeps the SDK's generic message and does not leak driver or connection details.
    /// </summary>
    public static async Task<T> Expose<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (EfLensException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
