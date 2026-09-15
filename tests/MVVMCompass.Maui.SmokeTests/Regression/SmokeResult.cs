using System.Text.Json.Serialization;

namespace MVVMCompass.Sample;

internal sealed record SmokeResult(string Result, List<string> Checks, string? Failure,
    string Platform, string OsVersion, string? PackageVersion)
{
    public List<string> Skipped { get; init; } = [];
    public string Suite { get; init; } = "full";
}

[JsonSerializable(typeof(SmokeResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class SmokeResultJsonContext : JsonSerializerContext;
