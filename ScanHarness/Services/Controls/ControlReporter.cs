namespace ScanHarness.Services.Controls
{
    /// <summary>Writes control hits to the activity log.</summary>
    public static class ControlReporter
    {
        /// <summary>
        /// Logs each control hit as a Warning (Advise) or Block entry. Call it right after logging the entry
        /// that was checked: the chat transcript and session reports rely on that order, and the data holds
        /// the violation (with the checked text) and its target so pips and reports can be recreated.
        /// </summary>
        public static void Report(LogService log, ControlCheck check, string brainName, string? toolCallId = null)
        {
            foreach (var violation in check.Violations)
            {
                var outcome = violation.Mode != ControlMode.Block
                    ? "flagged"
                    : violation.Target switch
                    {
                        ChatTranscript.Targets.Request => "blocked; it was not sent to the brain",
                        ChatTranscript.Targets.ToolCall => "blocked; the tool was not run",
                        _ => "blocked; it was withheld",
                    };
                var message = $"{(violation.Mode == ControlMode.Block ? "Block" : "Warning")}: {violation.StageName} control " +
                              $"'{violation.Name}' {outcome} ({violation.Target}). {violation.Detail}";
                var data = new Dictionary<string, object>
                {
                    ["controls"] = ControlService.ToJson(new[] { violation }),
                    ["target"] = violation.Target,
                };

                if (violation.Mode == ControlMode.Block)
                    log.LogBlock(message, toolCallId, data, brainName);
                else
                    log.LogWarning(message, toolCallId, data, brainName);
            }
        }
    }
}
