using ScanHarness.Models;
using ScanHarness.Services.Persistence;

namespace ScanHarness.Services
{
    /// <summary>
    /// The current user and session for one Blazor circuit. Every log entry and tool call is stamped
    /// with these, and they decide which stored sessions the user may list or open: users see only
    /// their own sessions, admins see everyone's.
    /// </summary>
    /// <remarks>
    /// A new session is written to the store only when its first activity is recorded, so opening the
    /// page or switching users doesn't leave empty sessions behind.
    /// </remarks>
    public class SessionState
    {
        private const int MaxTitleLength = 80;

        private readonly IActivityStore? _store;

        /// <param name="store">Where sessions are saved; null keeps everything in memory (e.g. in unit tests).</param>
        public SessionState(IActivityStore? store = null)
        {
            _store = store;
            CurrentUser = HarnessUsers.Default;
            Current = NewSession(CurrentUser);
        }

        /// <summary>Raised when the user or session changes.</summary>
        public event Action? OnChanged;

        /// <summary>The selected user; every new entry is recorded as performed by them.</summary>
        public HarnessUser CurrentUser { get; private set; }

        /// <summary>The session new activity is added to.</summary>
        public HarnessSession Current { get; private set; }

        /// <summary>True once <see cref="Current"/> has been written to the store.</summary>
        public bool IsSaved { get; private set; }

        /// <summary>Switches user and starts a fresh session for them. Unknown names are ignored.</summary>
        /// <returns>True when the user changed.</returns>
        public bool SwitchUser(string name)
        {
            var user = HarnessUsers.Find(name);
            if (user == null || user == CurrentUser)
            {
                return false;
            }

            CurrentUser = user;
            StartNewSession();
            return true;
        }

        /// <summary>Starts a new, empty session for the current user.</summary>
        public void StartNewSession()
        {
            Current = NewSession(CurrentUser);
            IsSaved = false;
            OnChanged?.Invoke();
        }

        /// <summary>Whether the current user may see <paramref name="session"/>.</summary>
        public bool CanView(HarnessSession session) =>
            CurrentUser.IsAdmin || string.Equals(session.UserName, CurrentUser.Name, StringComparison.Ordinal);

        /// <summary>Makes a stored session current, if it exists and the current user may see it.</summary>
        /// <returns>The opened session, or null if it wasn't found or isn't visible to this user.</returns>
        public HarnessSession? OpenSession(string sessionId)
        {
            var session = _store?.GetSession(sessionId);
            if (session == null || !CanView(session))
            {
                return null;
            }

            Current = session;
            IsSaved = true;
            OnChanged?.Invoke();
            return session;
        }

        /// <summary>Stored sessions the current user may see, newest first (all users' for admins).</summary>
        public IReadOnlyList<HarnessSession> VisibleSessions() =>
            _store?.ListSessions(CurrentUser.IsAdmin ? null : CurrentUser.Name) ?? Array.Empty<HarnessSession>();

        /// <summary>Writes the current session to the store if it hasn't been yet. Call before saving anything that belongs to it.</summary>
        public void EnsureSaved()
        {
            if (IsSaved || _store == null)
            {
                return;
            }

            _store.SaveSession(Current);
            IsSaved = true;
        }

        /// <summary>Labels the session with its first request (later requests don't change the title).</summary>
        public void RecordTitle(string prompt)
        {
            if (Current.Title != null)
            {
                return;
            }

            // Multi-line requests become a single-line title for the session picker.
            var text = string.Join(" ", prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Current.Title = text.Length <= MaxTitleLength ? text : text[..MaxTitleLength] + "…";
            if (_store != null)
            {
                _store.SaveSession(Current);
                IsSaved = true;
            }
        }

        private static HarnessSession NewSession(HarnessUser user) => new() { UserName = user.Name };
    }
}
