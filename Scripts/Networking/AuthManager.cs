// ═══════════════════════════════════════════════════
// AuthManager.cs
// Static singleton that tracks the current auth session
// and broadcasts login / logout / failure events.
// ═══════════════════════════════════════════════════
using System;

/// <summary>
/// Static singleton that tracks authentication state and broadcasts auth events.
/// Does not need to be in the scene tree — subscribe from any Node._Ready().
/// Always unsubscribe in _ExitTree() to avoid dangling delegates.
/// </summary>
public static class AuthManager
{
	// ── Session state ─────────────────────────────────────────────────────────

	/// <summary>True while a user is logged in.</summary>
	public static bool   IsLoggedIn { get; private set; }

	/// <summary>The authenticated username, or an empty string when logged out.</summary>
	public static string Username   { get; private set; } = "";

	/// <summary>The server-assigned user ID, or -1 when logged out.</summary>
	public static int    UserId     { get; private set; } = -1;

	/// <summary>The skin ID saved on the server, or 101 (default) when logged out.</summary>
	public static int    SkinId     { get; private set; } = 101;

	// ── Events ────────────────────────────────────────────────────────────────

	/// <summary>Fired after a successful login or register+auto-login.</summary>
	/// <param name="username">The authenticated username.</param>
	/// <param name="isNewUser">True when this is a fresh registration.</param>
	public static event Action<string, bool> OnAuthSuccess;

	/// <summary>Fired when login or registration fails.</summary>
	/// <param name="isRegistration">True when the failed attempt was a registration.</param>
	/// <param name="errorMessage">Human-readable error from the server.</param>
	public static event Action<bool, string> OnAuthFailed;

	// ── State mutators ────────────────────────────────────────────────────────

	/// <summary>
	/// Called by <see cref="ClipboardUI"/> after a successful network round-trip.
	/// Updates session state and raises <see cref="OnAuthSuccess"/>.
	/// </summary>
	public static void NotifySuccess(string username, int userId, int skinId, bool isNewUser)
	{
		IsLoggedIn = true;
		Username   = username;
		UserId     = userId;
		SkinId     = skinId;
		OnAuthSuccess?.Invoke(username, isNewUser);

		// Open the persistent live connection so the server can push user-list
		// updates. Called after session state is set so any event subscriber that
		// reads IsLoggedIn/Username already sees the updated values.
		LiveConnectionManager.Connect(
			ServerProtocol.DefaultHost,
			ServerProtocol.DefaultPort,
			username,
			userId);
	}

	/// <summary>
	/// Called by <see cref="ClipboardUI"/> when the server rejects credentials.
	/// Raises <see cref="OnAuthFailed"/> without touching session state.
	/// </summary>
	public static void NotifyFailure(bool isRegistration, string errorMessage)
	{
		OnAuthFailed?.Invoke(isRegistration, errorMessage);
	}

	/// <summary>
	/// Clears the current session. Called when the player chooses to log out
	/// via the NPC interaction.
	/// </summary>
	public static void NotifyLogout()
	{
		IsLoggedIn = false;
		Username   = "";
		UserId     = -1;
		SkinId     = 101;

		// Notify the server of intentional logout, then close the connection.
		LiveConnectionManager.SendLogout();
		LiveConnectionManager.Disconnect();
	}
}
