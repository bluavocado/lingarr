using Lingarr.Contracts.Models;

namespace Lingarr.Contracts.Translation;

/// <summary>
/// Core interface for subtitle translation providers.
/// Implementations can be built-in Lingarr services or third-party plugins.
/// </summary>
public interface ITranslationService
{
    string? ModelName { get; }

    /// <summary>
    /// Translates a single piece of text from the source language to the target language.
    /// Optionally accepts surrounding subtitle lines as context.
    /// </summary>
    /// <param name="text">The subtitle line to translate.</param>
    /// <param name="sourceLanguage">Language code of <paramref name="text"/>.</param>
    /// <param name="targetLanguage">Language code to translate into.</param>
    /// <param name="contextLinesBefore">
    /// The subtitle lines preceding <paramref name="text"/>, oldest first, or null when no context is
    /// configured. By default each entry is the plain line text. When the user has enabled the
    /// "Structured context with translations" setting, each entry is instead the JSON serialisation of a
    /// <see cref="ContextLine"/> carrying the position, the line and, for lines already translated in
    /// this run, the translation. Implementations that do not need the structure can pass the entries
    /// through as text.
    /// </param>
    /// <param name="contextLinesAfter">
    /// The subtitle lines following <paramref name="text"/>, or null when no context is configured.
    /// Same format as <paramref name="contextLinesBefore"/>, without translations.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        List<string>? contextLinesBefore,
        List<string>? contextLinesAfter,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the list of supported source languages and their available target languages.
    /// </summary>
    Task<List<SourceLanguage>> GetLanguages();

    /// <summary>
    /// Returns the list of available models for providers that expose a model catalog.
    /// </summary>
    Task<ModelsResponse> GetModels();

    /// <summary>
    /// Resolves a requested source and target language pair to the actual language codes.
    /// </summary>
    Task<LanguagePair?> GetLanguagePair(
        string requestedSource,
        string requestedTarget,
        CancellationToken cancellationToken);
}
