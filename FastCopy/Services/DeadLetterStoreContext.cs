using System.Text.Json.Serialization;

namespace FastCopy.Services;

[JsonSerializable(typeof(FailedJobEntry))]
internal sealed partial class DeadLetterStoreContext : JsonSerializerContext
{
}
