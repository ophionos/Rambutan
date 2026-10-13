using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.Core.Remote;

namespace Rambutan.LocalApi;

internal sealed record SeekRequest(long PositionMs);

internal sealed record VolumeRequest(double Volume);

/// <summary>Source-generated, like every serializer in the app (NativeAOT).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RemoteState))]
[JsonSerializable(typeof(SeekRequest))]
[JsonSerializable(typeof(VolumeRequest))]
internal sealed partial class LocalApiJsonContext : JsonSerializerContext;
