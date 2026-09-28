using ScanHarness.Models;

namespace ScanHarness.Services
{
    /// <summary>
    /// The "brain" of the harness. Given a natural-language request from the user, it
    /// decides which tool (if any) to call. Implementations are interchangeable so the
    /// harness can use different LLMs (or a rule-based stub) as the decision maker.
    /// </summary>
    public interface IAgentBrain
    {
        /// <summary>Human-readable name of the brain (shown in the UI/logs).</summary>
        string Name { get; }

        /// <summary>Interpret the user's prompt and decide which tool to call.</summary>
        Task<ToolDecision> DecideAsync(string prompt);

        /// <summary>
        /// Summarise a finished tool call for the user: what they asked for and what the tool returned
        /// (or that it failed or was rejected). Called after every tool call, whatever its outcome.
        /// </summary>
        /// <param name="prompt">The user's original request.</param>
        /// <param name="toolCall">The finished call, including its status and the tool's response.</param>
        Task<ActionSummary> SummarizeAsync(string prompt, ToolCall toolCall);

        /// <summary>
        /// Summarise a whole session from its per-action summaries, in at most
        /// <see cref="SessionReportBuilder.MaxSummaryWords"/> words.
        /// </summary>
        /// <param name="toolCalls">Every tool call in the session, with their summaries.</param>
        Task<ActionSummary> SummarizeSessionAsync(IReadOnlyList<ToolCall> toolCalls);

        /// <summary>
        /// Summarise activity across sessions and users (the admin's All activity view), in at most
        /// <see cref="SessionReportBuilder.MaxSummaryWords"/> words.
        /// </summary>
        /// <param name="rows">The requests to summarise, newest first.</param>
        /// <param name="window">Plain description of the filters, e.g. "all users, 28 Sep 09:00 to 12:00".</param>
        Task<ActionSummary> SummarizeActivityAsync(IReadOnlyList<ActivityRow> rows, string window);
    }
}
