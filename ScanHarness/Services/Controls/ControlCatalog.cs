using System.Text.Json.Serialization;

namespace ScanHarness.Services.Controls
{
    /// <summary>Where in the flow a control is applied.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ControlStage
    {
        /// <summary>The user's message, before it is passed to the brain.</summary>
        ChatIn,

        /// <summary>Output from the brain: its reply and the summaries it writes.</summary>
        ChatOut,

        /// <summary>A tool call the brain chose, before the tool is executed.</summary>
        ToolRule
    }

    /// <summary>What a control does when it matches.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ControlMode
    {
        /// <summary>No action.</summary>
        Off,

        /// <summary>Flag the message (orange pip) and log a Warning, but let it through.</summary>
        Advise,

        /// <summary>Flag the message (red pip), log a Block, and stop it: input isn't sent to the
        /// brain, output is withheld, and a tool call isn't executed.</summary>
        Block
    }

    /// <summary>A guardrail that checks text at one stage of the flow.</summary>
    /// <param name="Id">Stable identifier, used to store each user's setting.</param>
    /// <param name="Stage">Where the control is applied.</param>
    /// <param name="Name">Short name shown on the Controls page and in pips and the log.</param>
    /// <param name="Description">What the control checks, shown on the Controls page.</param>
    /// <param name="DefaultMode">Mode used until a user changes it.</param>
    /// <param name="Match">Returns a description of what matched, or null if the text passes.</param>
    public sealed record ControlDefinition(
        string Id,
        ControlStage Stage,
        string Name,
        string Description,
        ControlMode DefaultMode,
        Func<string, string?> Match);

    /// <summary>
    /// Every control the harness offers. Add a control here and it appears on the Controls page
    /// and is applied at its stage.
    /// </summary>
    public static class ControlCatalog
    {
        public static IReadOnlyList<ControlDefinition> All { get; } = new[]
        {
            new ControlDefinition(
                Id: "chat-in-contains-badinstruction",
                Stage: ControlStage.ChatIn,
                Name: "Contains \"BadInstruction\"",
                Description: "Flags a message that includes the text \"BadInstruction\" (exact, case-sensitive).",
                DefaultMode: ControlMode.Block,
                Match: text => Contains(text, "BadInstruction", StringComparison.Ordinal)),

            new ControlDefinition(
                Id: "chat-out-contains-script-tag",
                Stage: ControlStage.ChatOut,
                Name: "Contains \"<script>\"",
                Description: "Flags a reply or summary that includes a \"<script>\" tag (any letter case).",
                DefaultMode: ControlMode.Block,
                Match: text => Contains(text, "<script>", StringComparison.OrdinalIgnoreCase)),
        };

        /// <summary>Controls applied at <paramref name="stage"/>, in catalog order.</summary>
        public static IEnumerable<ControlDefinition> ForStage(ControlStage stage) => All.Where(c => c.Stage == stage);

        /// <summary>Finds a control by id, or null.</summary>
        public static ControlDefinition? Find(string id) => All.FirstOrDefault(c => c.Id == id);

        /// <summary>Display name of a stage, e.g. "Chat In".</summary>
        public static string StageName(ControlStage stage) => stage switch
        {
            ControlStage.ChatIn => "Chat In",
            ControlStage.ChatOut => "Chat Out",
            ControlStage.ToolRule => "ToolRule",
            _ => stage.ToString()
        };

        // e.g. Found "<script>" at character 17.
        private static string? Contains(string text, string value, StringComparison comparison)
        {
            var index = text.IndexOf(value, comparison);
            return index < 0 ? null : $"Found \"{text.Substring(index, value.Length)}\" at character {index + 1}.";
        }
    }
}
