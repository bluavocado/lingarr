using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(14)]
public class M0014_SeedAiUserPrompt : Migration
{
    /// <summary>
    /// Default user prompt for new installations: the line to translate framed by its context, with
    /// the instructions last so they sit closest to the model's answer. Existing rows are never
    /// changed by this migration, so upgraded installations keep whatever prompt they had.
    /// </summary>
    private const string DefaultUserPrompt =
        "[Context-Before]\n{contextBefore}\n\n" +
        "[Context-After]\n{contextAfter}\n\n" +
        "[Target-to-Translate]\n{lineToTranslate}\n\n" +
        "Translate only the text under [Target-to-Translate]. The lines under [Context-Before] and [Context-After] " +
        "are neighbouring subtitle lines for context only: use them to keep names, terms and tone consistent, " +
        "and never translate or repeat them. When a [Context-Before] entry includes a \"translation\" field, " +
        "follow that earlier translation. Reply with the translated target text alone, as plain text, " +
        "without labels, JSON or position numbers.";

    public override void Up()
    {
        Insert.IntoTable("settings").Row(new { key = "ai_user_prompt", value = DefaultUserPrompt });

        IfDatabase("sqlite", "postgresql").Execute.Sql("""
            UPDATE settings SET "value" =
                (SELECT prompt."value" FROM settings prompt WHERE prompt."key" = 'ai_context_prompt')
            WHERE "key" = 'ai_user_prompt'
              AND EXISTS (SELECT 1 FROM settings prompt
                          WHERE prompt."key" = 'ai_context_prompt')
              AND EXISTS (SELECT 1 FROM settings enabled
                          WHERE enabled."key" = 'ai_context_prompt_enabled'
                            AND enabled."value" = 'true')
            """);

        IfDatabase("mysql").Execute.Sql("""
            UPDATE settings target
            JOIN settings prompt ON prompt.`key` = 'ai_context_prompt'
            JOIN settings enabled ON enabled.`key` = 'ai_context_prompt_enabled'
                                 AND enabled.`value` = 'true'
            SET target.`value` = prompt.`value`
            WHERE target.`key` = 'ai_user_prompt'
            """);

        Delete.FromTable("settings").Row(new { key = "ai_context_prompt" });
        Delete.FromTable("settings").Row(new { key = "ai_context_prompt_enabled" });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = "ai_user_prompt" });
        Insert.IntoTable("settings").Row(new { key = "ai_context_prompt_enabled", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "ai_context_prompt", value = "Use the CONTEXT to translate the TARGET line.\n\n[TARGET] {lineToTranslate}\n\n[CONTEXT]\n{contextBefore}\n{lineToTranslate}\n{contextAfter}\n[/CONTEXT]" });
    }
}
