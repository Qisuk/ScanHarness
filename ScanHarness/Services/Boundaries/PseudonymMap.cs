using System.Text.RegularExpressions;

namespace ScanHarness.Services.Boundaries
{
    /// <summary>
    /// Swaps identifying values (email addresses, hostnames, IP addresses) for stable placeholders such as
    /// HOST_1 before data leaves the boundary, and swaps them back in what comes back. The same value always
    /// gets the same placeholder, so a component can still reason about "HOST_1" across a conversation. The
    /// map itself never leaves the harness; logs record placeholders, never values.
    /// </summary>
    public sealed class PseudonymMap
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

        private static readonly Regex Email = new(@"\b[A-Z0-9._%+-]+@(?:[A-Z0-9-]+\.)+[A-Z]{2,24}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
        private static readonly Regex IpAddress = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.CultureInvariant, Timeout);
        private static readonly Regex Host = new(@"(?<![\w.-])(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
        private static readonly Regex Placeholder = new(@"\b(?:HOST|EMAIL|IP)_\d+\b", RegexOptions.CultureInvariant, Timeout);

        // "controls.json" looks like a hostname; file extensions aren't top-level domains worth hiding.
        private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "json", "js", "cs", "css", "html", "htm", "txt", "md", "xml", "yml", "yaml", "png", "jpg", "gif", "svg", "dll", "exe", "log", "db"
        };

        private readonly Dictionary<string, string> _toPlaceholder = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _toValue = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        /// <summary>Number of distinct values replaced so far.</summary>
        public int Count
        {
            get { lock (_gate) return _toPlaceholder.Count; }
        }

        /// <summary>Replaces identifying values in <paramref name="text"/> with placeholders.</summary>
        public string Pseudonymise(string text)
        {
            lock (_gate)
            {
                var result = Email.Replace(text, m => PlaceholderFor(m.Value, "EMAIL"));
                result = IpAddress.Replace(result, m => PlaceholderFor(m.Value, "IP"));
                return Host.Replace(result, m => FileExtensions.Contains(m.Value[(m.Value.LastIndexOf('.') + 1)..])
                    ? m.Value
                    : PlaceholderFor(m.Value, "HOST"));
            }
        }

        /// <summary>Puts the original values back in place of any placeholders this map issued.</summary>
        public string Restore(string text)
        {
            lock (_gate)
            {
                return Placeholder.Replace(text, m => _toValue.TryGetValue(m.Value, out var value) ? value : m.Value);
            }
        }

        private string PlaceholderFor(string value, string kind)
        {
            if (!_toPlaceholder.TryGetValue(value, out var placeholder))
            {
                placeholder = $"{kind}_{_toValue.Keys.Count(k => k.StartsWith(kind + "_", StringComparison.Ordinal)) + 1}";
                _toPlaceholder[value] = placeholder;
                _toValue[placeholder] = value;
            }

            return placeholder;
        }
    }
}
