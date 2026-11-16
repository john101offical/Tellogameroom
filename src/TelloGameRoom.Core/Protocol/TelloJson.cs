using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelloGameRoom.Core.Protocol;

/// <summary>The JSON conventions of the REST API: camelCase properties and enums as strings.</summary>
public static class TelloJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
