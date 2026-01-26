using System;

namespace JunimoServer.Services.PasswordProtection
{
    public enum AuthState
    {
        Unauthenticated,
        Authenticated
    }

    public class PlayerAuthData
    {
        /// <summary>
        /// Lock object for thread-safe state transitions.
        /// Used to prevent race conditions during authentication.
        /// </summary>
        public object StateLock { get; } = new();

        public long PlayerId { get; }

        /// <summary>
        /// Current authentication state.
        /// Internal setter: only PasswordProtectionService should modify this.
        /// </summary>
        public AuthState State { get; internal set; }

        /// <summary>
        /// Number of failed authentication attempts.
        /// Internal setter: only PasswordProtectionService should modify this.
        /// </summary>
        public int FailedAttempts { get; internal set; }

        public DateTime JoinTime { get; }

        /// <summary>
        /// Whether this is a new player (first time joining) vs returning player.
        /// Internal setter: set during initial farmhand request processing.
        /// </summary>
        public bool IsNewPlayer { get; internal set; }

        /// <summary>
        /// Whether welcome message has been sent.
        /// Internal setter: set when welcome message is delivered.
        /// </summary>
        public bool WelcomeMessageSent { get; internal set; }

        /// <summary>
        /// Last reminder time.
        /// Internal setter: updated when reminders are sent.
        /// </summary>
        public DateTime LastReminderTime { get; internal set; }

        /// <summary>
        /// Time when the player finished character customization (for new players).
        /// Null if customization not yet completed or if returning player.
        /// Used to properly calculate auth timeout for new players.
        /// Internal setter: set when customization is detected as complete.
        /// </summary>
        public DateTime? CustomizationCompleteTime { get; internal set; }

        public PlayerAuthData(long playerId)
        {
            PlayerId = playerId;
            State = AuthState.Unauthenticated;
            FailedAttempts = 0;
            JoinTime = DateTime.UtcNow;
            WelcomeMessageSent = false;
            LastReminderTime = DateTime.MinValue;
            IsNewPlayer = false;
            CustomizationCompleteTime = null;
        }
    }
}
