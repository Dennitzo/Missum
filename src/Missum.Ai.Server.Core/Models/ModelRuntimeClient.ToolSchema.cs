using System.Text.Json;
using Missum.Ai.Contracts;

namespace Missum.Ai.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    internal static JsonElement PrepareToolParameters(string modelId, LmToolDefinition tool)
    {
        // llama's tagged-tool parsers (including Qwen and DeepSeek) enumerate
        // only root object properties through foreach_parameter.
        // A root oneOf replaces that object in its schema AST, leaving edit
        // with no parameters and constraining every generated call to {}.
        // Only adapt the known edit transport schema. The original catalog
        // and executor still enforce exclusive single/batch edits and hashes.
        if (tool.Name != ClientToolNames.CodingEdit
            || !tool.Parameters.TryGetProperty("oneOf", out _))
            return tool.Parameters;

        return JsonSerializer.SerializeToElement(tool.Parameters.EnumerateObject()
            .Where(static property => property.Name != "oneOf")
            .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal));
    }
}
