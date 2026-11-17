using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDeskHub.Shared;

/// <summary>Shared JSON configuration used on both ends of the wire protocol.</summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static readonly JsonSerializerOptions PrettyOptions = new(Options)
    {
        WriteIndented = true
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

public sealed record OperationResult(bool Success, string? Message = null, string? Error = null)
{
    public static OperationResult Ok(string? message = null) => new(true, message);
    public static OperationResult Fail(string error) => new(false, null, error);
}

public sealed record OperationResult<T>(bool Success, T? Value, string? Message = null, string? Error = null)
{
    public static OperationResult<T> Ok(T value, string? message = null) => new(true, value, message);
    public static OperationResult<T> Fail(string error) => new(false, default, null, error);
}

/// <summary>A single page of data returned by the hub to the desktop shell.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);
