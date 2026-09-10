using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dmg.Windows.Mounts;

/// <summary>
/// The compile-time serializer for <c>mounts.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source generation is not an optimisation here; it is the only thing that
/// works.</b> dmg ships as a single NativeAOT binary, and reflection-based
/// serialization needs the metadata that AOT publishing trims away. A
/// <c>JsonSerializer.Serialize(record)</c> compiles, passes every test on a JIT
/// test host, and then fails on the shipped executable - which is exactly the kind
/// of bug that reaches a user rather than a build. The generator writes the
/// converters at compile time, and the AOT analyzers this project enables fail the
/// build if anything reaches for the reflection path instead.
/// </para>
/// <para>
/// The options live on the attribute rather than in a
/// <see cref="JsonSerializerOptions"/> built at runtime, for the same reason: an
/// options object assembled by hand can pull in the reflection-based resolver
/// without anyone noticing.
/// </para>
/// <para>
/// camelCase because it is a JSON file and that is what JSON files look like;
/// indented because a person may well open this one while working out why a drive
/// letter is still taken.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(MountRegistryDocument))]
[JsonSerializable(typeof(MountRecord))]
internal sealed partial class MountRegistryJson : JsonSerializerContext
{
}
