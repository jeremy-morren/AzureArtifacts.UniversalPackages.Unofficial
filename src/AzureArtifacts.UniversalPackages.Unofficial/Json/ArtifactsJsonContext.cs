using System.Text.Json.Serialization;

namespace AzureArtifacts.UniversalPackages.Unofficial.Json;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FeedListDto))]
[JsonSerializable(typeof(PackageListDto))]
[JsonSerializable(typeof(VersionMetadataDto))]
[JsonSerializable(typeof(ManifestDto))]
[JsonSerializable(typeof(IEnumerable<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ArtifactsJsonContext : JsonSerializerContext;