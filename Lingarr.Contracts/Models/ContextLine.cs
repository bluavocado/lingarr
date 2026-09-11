using System.Text.Json.Serialization;

namespace Lingarr.Contracts.Models;

/// <summary>
/// One neighbouring subtitle line as passed to a translation service when the
/// "Structured context with translations" setting is enabled. Each entry of
/// <c>contextLinesBefore</c> / <c>contextLinesAfter</c> is then the JSON serialisation of this
/// type instead of the plain line text. Keys match the batch models: <c>position</c> and
/// <c>line</c>, plus <c>translation</c> for lines that have already been translated.
/// </summary>
public sealed class ContextLine
{
    /// <summary>
    /// Position of the subtitle line within the file or request.
    /// </summary>
    [JsonPropertyName("position")]
    public int Position { get; init; }

    /// <summary>
    /// Source text of the line.
    /// </summary>
    [JsonPropertyName("line")]
    public string Line { get; init; } = string.Empty;

    /// <summary>
    /// Translation of the line when it already has one; omitted otherwise.
    /// </summary>
    [JsonPropertyName("translation")]
    public string? Translation { get; init; }
}
