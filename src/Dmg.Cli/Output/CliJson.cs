using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dmg.Cli.Output;

/// <summary>
/// The compile-time serializer for everything <c>--json</c> writes to stdout.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source generation is not an optimisation here; it is the only thing that
/// works.</b> dmg ships as a single NativeAOT binary, and reflection-based
/// serialization needs metadata that AOT publishing trims away. A
/// <c>JsonSerializer.Serialize(payload)</c> compiles, passes every test on a JIT
/// test host, and then fails on the shipped executable - the kind of bug that
/// reaches a user rather than a build. The same reasoning, and the same shape, as
/// <c>MountRegistryJson</c> in Dmg.Windows.
/// </para>
/// <para>
/// Indented, because a person reads this output at least as often as a script does,
/// and <c>dmg info --json</c> piped into a pager is a normal thing to do. camelCase,
/// because it is JSON.
/// </para>
/// <para>
/// <b>The relaxed encoder, and why "unsafe" is the wrong word for it here.</b> The
/// default encoder escapes everything that could be dangerous inside an HTML
/// document, which turns a version of <c>0.1.0+abc123</c> into
/// <c>0.1.0+abc123</c> and a volume label with an accent in it into a row of
/// escapes. Neither is wrong and both are unreadable.
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> is unsafe only when the
/// output is interpolated into HTML, and this output goes to a pipe. Quotes,
/// backslashes and control characters are still escaped - that is JSON itself, not
/// the encoder - so the document stays valid whatever a volume label turns out to
/// contain.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(VersionPayload))]
[JsonSerializable(typeof(InfoPayload))]
[JsonSerializable(typeof(MountPayload))]
[JsonSerializable(typeof(UnmountPayload))]
[JsonSerializable(typeof(UnmountAllPayload))]
public sealed partial class CliJson : JsonSerializerContext
{
    private static CliJson? _readable;

    /// <summary>
    /// The context every verb serializes through: the generated one, rebuilt over
    /// options that read well in a terminal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from <c>Default.Options</c> so the naming policy and the indentation
    /// declared on the attribute above carry over rather than being restated here
    /// and left to drift, and handed to the generated options constructor so the
    /// resolver is still the compile-time one. Nothing here can fall back to
    /// reflection.
    /// </para>
    /// <para>
    /// Lazy, and deliberately not a field initializer: <c>Default</c> is a static of
    /// this same class, so reading it from this class's static constructor reads it
    /// before the generated initializer has run and throws a
    /// <see cref="NullReferenceException"/> wrapped in a
    /// <see cref="TypeInitializationException"/>. Found by running it.
    /// </para>
    /// </remarks>
    public static CliJson Readable =>
        _readable ??= new CliJson(new JsonSerializerOptions(Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
}
