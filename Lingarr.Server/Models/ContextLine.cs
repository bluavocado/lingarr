using System.Text.Json.Serialization;

namespace Lingarr.Server.Models;

/// <summary>
/// One neighbouring subtitle line as handed to AI providers when translated context is enabled.
/// Serialised as a single JSON object per line so line breaks inside the text stay escaped.
/// </summary>
/// <param name="Position">Position of the subtitle line within the file or request.</param>
/// <param name="Source">Source text of the line.</param>
/// <param name="Translation">Translation of the line when it has one; omitted otherwise.</param>
public sealed record ContextLine(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("translation")] string? Translation);
