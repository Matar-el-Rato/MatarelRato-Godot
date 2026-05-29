// ═══════════════════════════════════════════════════
// MatchHistoryBoardGate.cs
// Attached to the Match-History board root. Keeps the board hidden
// AND non-interactable until the player has successfully logged in,
// and hides it again on logout.
//
// Hiding alone is not enough: a Node3D set invisible still keeps its
// StaticBody collision, so the player could click the invisible board
// and focus it. We therefore also toggle the sibling Interactable's
// Enabled flag, which gates focus/highlight/interaction.
// ═══════════════════════════════════════════════════
using Godot;

/// <summary>Gates the match-history board's visibility and interactivity on auth state.</summary>
public partial class MatchHistoryBoardGate : Node3D
{
	private Interactable _interactable;

	// SUBSCRIPTION TIMING (mirrors ConnectedPlayersBoard):
	// The board lives in the Pizzeria, which is removed/re-added to the tree during
	// the intro DitherEffect / camera sweep — often before AuthManager fires
	// OnAuthSuccess. Subscribing in _EnterTree (not _Ready) restores the subscription
	// every time the node re-enters the tree, so a login is never missed. We re-apply
	// the current auth state on each entry as a catch-up.
	public override void _EnterTree()
	{
		AuthManager.OnAuthSuccess += OnAuthSuccess;
		AuthManager.OnLogout      += OnLogout;
		SetActive(AuthManager.IsLoggedIn);
	}

	public override void _Ready()
	{
		// Children are guaranteed in-tree here, so the Interactable resolves; re-apply.
		SetActive(AuthManager.IsLoggedIn);
	}

	public override void _ExitTree()
	{
		AuthManager.OnAuthSuccess -= OnAuthSuccess;
		AuthManager.OnLogout      -= OnLogout;
	}

	private void OnAuthSuccess(string username, bool isNewUser) => SetActive(true);

	private void OnLogout()
	{
		// If the player logs out while focused on this board, release the camera.
		if (FocusController.Instance != null && FocusController.Instance.IsFocusedOn(this))
			FocusController.Instance.ExitFocus();
		SetActive(false);
	}

	private void SetActive(bool active)
	{
		Visible = active;
		// Resolve lazily: on the first _EnterTree the child isn't reachable yet.
		_interactable ??= GetNodeOrNull<Interactable>("Interactable");
		if (_interactable != null) _interactable.Enabled = active;
	}
}
