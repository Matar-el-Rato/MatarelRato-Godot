// ═══════════════════════════════════════════════════
// MatchHistoryUI.cs
// 2D Control rendered inside a SubViewport on the 3D Match-History
// board prop. Fetches a player's match history from the server on a
// background thread (ServerProtocol.GetMatchHistory) and renders one
// row per game: room, player count, duration, winner, date, and the
// full placement order.
//
// THREADING: the network call runs on Task.Run; results are marshalled
// back to the main thread through a ConcurrentQueue<Action> drained in
// _Process — the same pattern used by ClipboardUI.cs / ConnectedPlayersBoard.cs.
// Godot's scene tree is NOT thread-safe, so every node mutation happens
// on the main thread.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

/// <summary>
/// UI Control embedded in the Match-History board's SubViewport.
/// Looks up a username's games and lists them newest-first. Defaults to the
/// logged-in player but any username can be typed into the search field.
/// </summary>
public partial class MatchHistoryUI : Control
{
	[Export] public Label          Title;
	[Export] public Button         HistoryTab;
	[Export] public Button         LeaderboardTab;
	[Export] public Control        SearchBar;       // hidden while on the leaderboard tab
	[Export] public LineEdit       UsernameInput;
	[Export] public Button         SearchButton;
	[Export] public Button         MineButton;
	[Export] public Button         CloseButton;
	[Export] public VBoxContainer  ResultsList;
	[Export] public Label          StatusLabel;

	private enum Mode { History, Leaderboard }
	private Mode _mode = Mode.History;

	// Marshals background network results onto the main thread.
	private readonly ConcurrentQueue<Action> _mainThreadQueue = new();

	// Guards against overlapping fetches clobbering the list out of order.
	private int _requestSeq = 0;

	// Board font, loaded once and applied to dynamically created row labels so
	// they match the static scene labels instead of falling back to the default.
	private Font _font;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_font = ResourceLoader.Load<Font>("res://Assets/Fonts/Jersey10-Regular.ttf");

		Title          ??= GetNodeOrNull<Label>("%Title");
		HistoryTab     ??= GetNodeOrNull<Button>("%HistoryTab");
		LeaderboardTab ??= GetNodeOrNull<Button>("%LeaderboardTab");
		SearchBar      ??= GetNodeOrNull<Control>("%SearchBar");
		UsernameInput  ??= GetNodeOrNull<LineEdit>("%UsernameInput");
		SearchButton   ??= GetNodeOrNull<Button>("%SearchButton");
		MineButton     ??= GetNodeOrNull<Button>("%MineButton");
		CloseButton    ??= GetNodeOrNull<Button>("%CloseButton");
		ResultsList    ??= GetNodeOrNull<VBoxContainer>("%ResultsList");
		StatusLabel    ??= GetNodeOrNull<Label>("%StatusLabel");

		if (SearchButton != null)
			SearchButton.Pressed += () => Fetch(UsernameInput?.Text?.Trim() ?? "");

		if (MineButton != null)
			MineButton.Pressed += FetchMine;

		if (UsernameInput != null)
			UsernameInput.TextSubmitted += text => Fetch(text.Trim());

		if (CloseButton != null)
			CloseButton.Pressed += () => FocusController.Instance?.ExitFocus();

		if (HistoryTab != null)
			HistoryTab.Pressed += () => SwitchMode(Mode.History);
		if (LeaderboardTab != null)
			LeaderboardTab.Pressed += () => SwitchMode(Mode.Leaderboard);

		// Default to the History tab showing the logged-in player's own games.
		SwitchMode(Mode.History);
	}

	// ── Tabs ──────────────────────────────────────────────────────────────────

	/// <summary>Switches between History and Leaderboard views and fetches that view.</summary>
	private void SwitchMode(Mode mode)
	{
		_mode = mode;

		if (Title != null)
			Title.Text = mode == Mode.History ? "MATCH HISTORY" : "LEADERBOARD";
		if (SearchBar != null)
			SearchBar.Visible = mode == Mode.History;

		// Highlight the active tab with an accent style; the other stays normal
		// (not dimmed, so it doesn't look disabled).
		SetTabSelected(HistoryTab,     mode == Mode.History);
		SetTabSelected(LeaderboardTab, mode == Mode.Leaderboard);

		if (mode == Mode.Leaderboard)
		{
			FetchLeaderboard();
		}
		else
		{
			// History never preloads — the player searches or presses MINE.
			ClearRows();
			SetStatus("Type a username and Search, or press MINE for your own games.");
		}
	}

	/// <summary>Fills the field with the logged-in username and searches it immediately.</summary>
	private void FetchMine()
	{
		string me = AuthManager.IsLoggedIn ? AuthManager.Username : "";
		if (UsernameInput != null) UsernameInput.Text = me;
		Fetch(me);
	}

	public override void _Process(double delta)
	{
		while (_mainThreadQueue.TryDequeue(out var action))
			action();
	}

	// ── Fetch ─────────────────────────────────────────────────────────────────

	/// <summary>
	/// Kicks off a background history fetch for <paramref name="username"/>.
	/// Empty username is treated as "no player selected".
	/// </summary>
	private void Fetch(string username)
	{
		if (string.IsNullOrWhiteSpace(username))
		{
			SetStatus("Enter a username to view their games.");
			ClearRows();
			return;
		}

		int seq = ++_requestSeq;
		if (SearchButton != null) SearchButton.Disabled = true;
		SetStatus($"Loading {username}'s games…");

		_ = Task.Run(() =>
		{
			var result = ServerProtocol.GetMatchHistory(
				ServerProtocol.DefaultHost, ServerProtocol.DefaultPort, username);

			_mainThreadQueue.Enqueue(() =>
			{
				if (SearchButton != null) SearchButton.Disabled = false;
				// Ignore results from a superseded request.
				if (seq != _requestSeq) return;
				Render(username, result);
			});
		});
	}

	// ── Render (main thread) ────────────────────────────────────────────────

	private void Render(string username, ServerProtocol.ServerResult result)
	{
		ClearRows();

		if (result == null || !result.IsSuccess)
		{
			// The server returns InvalidInput specifically when no such user exists.
			if (result != null && result.Code == ServerProtocol.ResponseCode.InvalidInput)
				SetStatus($"No player named \"{username}\".");
			else
				SetStatus(result?.Message ?? "Failed to load history.");
			return;
		}

		var json = new Json();
		if (json.Parse(result.Message) != Error.Ok ||
			json.Data.VariantType != Variant.Type.Array)
		{
			SetStatus("Could not read history data.");
			return;
		}

		var matches = json.Data.AsGodotArray();
		if (matches.Count == 0)
		{
			SetStatus($"{username} has no games yet.");
			return;
		}

		SetStatus($"{matches.Count} game(s) for {username}");

		foreach (var entry in matches)
		{
			if (entry.VariantType != Variant.Type.Dictionary) continue;
			AddMatchRow(entry.AsGodotDictionary());
		}
	}

	/// <summary>Builds and appends one match row to the results list.</summary>
	private void AddMatchRow(Godot.Collections.Dictionary m)
	{
		int    roomId   = GetInt(m, "room_id");
		string status   = GetStr(m, "status");
		long   start    = GetLong(m, "start");
		long   duration = GetLong(m, "duration");
		string winner   = GetStr(m, "winner");
		int    pCount   = GetInt(m, "player_count");

		var panel = new PanelContainer();
		panel.AddThemeStyleboxOverride("panel", RowStyle());
		var box = new VBoxContainer();
		box.AddThemeConstantOverride("separation", 2);
		panel.AddChild(box);

		// Header line: Room · players · duration · winner · date
		string date    = start > 0
			? DateTimeOffset.FromUnixTimeSeconds(start).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
			: "—";
		string durStr  = FormatDuration(duration);
		// Winner name in orange; abandoned/in-progress states stay neutral.
		string winLine = status == "FINISHED" && winner.Length > 0
			? $"Won by [color=#e0700a]{BBEscape(winner)}[/color]"
			: status == "CANCELLED" ? "abandoned" : status.ToLower();

		box.AddChild(MakeRichLabel(
			$"Room {roomId}  ·  {pCount} players  ·  {durStr}  ·  {winLine}",
			30, new Color(0.13f, 0.1f, 0.1f)));

		box.AddChild(MakeLabel(date, 22, new Color(0.4f, 0.4f, 0.42f)));

		// Placement line: "1. Alice   2. Bob   3. Carol"
		if (m.ContainsKey("players") && m["players"].VariantType == Variant.Type.Array)
		{
			var players = m["players"].AsGodotArray();
			var sb = new System.Text.StringBuilder();
			foreach (var p in players)
			{
				if (p.VariantType != Variant.Type.Dictionary) continue;
				var pd  = p.AsGodotDictionary();
				int pos = GetInt(pd, "position");
				string name = GetStr(pd, "name");
				if (sb.Length > 0) sb.Append("   ");
				sb.Append(pos > 0 ? $"{pos}. {name}" : name);
			}
			if (sb.Length > 0)
				box.AddChild(MakeLabel(sb.ToString(), 24, new Color(0.25f, 0.28f, 0.45f)));
		}

		ResultsList?.AddChild(panel);
	}

	// ── Leaderboard ───────────────────────────────────────────────────────────

	/// <summary>Kicks off a background leaderboard fetch.</summary>
	private void FetchLeaderboard()
	{
		int seq = ++_requestSeq;
		SetStatus("Loading leaderboard…");

		_ = Task.Run(() =>
		{
			var result = ServerProtocol.GetLeaderboard(
				ServerProtocol.DefaultHost, ServerProtocol.DefaultPort);

			_mainThreadQueue.Enqueue(() =>
			{
				// Ignore if superseded or if the user switched back to History.
				if (seq != _requestSeq || _mode != Mode.Leaderboard) return;
				RenderLeaderboard(result);
			});
		});
	}

	private void RenderLeaderboard(ServerProtocol.ServerResult result)
	{
		ClearRows();

		if (result == null || !result.IsSuccess)
		{
			SetStatus(result?.Message ?? "Failed to load leaderboard.");
			return;
		}

		var json = new Json();
		if (json.Parse(result.Message) != Error.Ok ||
			json.Data.VariantType != Variant.Type.Array)
		{
			SetStatus("Could not read leaderboard data.");
			return;
		}

		var rows = json.Data.AsGodotArray();
		if (rows.Count == 0)
		{
			SetStatus("No players yet.");
			return;
		}

		SetStatus($"Top {rows.Count} players");

		string me   = AuthManager.IsLoggedIn ? AuthManager.Username : "";
		int    rank = 0;
		foreach (var entry in rows)
		{
			if (entry.VariantType != Variant.Type.Dictionary) continue;
			AddLeaderboardRow(++rank, entry.AsGodotDictionary(), me);
		}
	}

	/// <summary>Builds one leaderboard row: "rank. name" on the left, points on the right.</summary>
	private void AddLeaderboardRow(int rank, Godot.Collections.Dictionary d, string me)
	{
		string name   = GetStr(d, "username");
		int    points = GetInt(d, "points");
		bool   isMe   = !string.IsNullOrEmpty(me) && name == me;

		var panel = new PanelContainer();
		panel.AddThemeStyleboxOverride("panel", RowStyle());
		panel.CustomMinimumSize = new Vector2(0, 54);  // fixed, compact row height
		var hbox = new HBoxContainer();
		hbox.AddThemeConstantOverride("separation", 12);
		panel.AddChild(hbox);

		// The logged-in player's own row is highlighted in orange.
		Color nameColor = isMe ? new Color(0.85f, 0.45f, 0.05f) : new Color(0.13f, 0.1f, 0.1f);
		var left = MakeLabel($"{rank}.  {name}", 30, nameColor);
		left.AutowrapMode        = TextServer.AutowrapMode.Off;   // names are short; no wrapping
		left.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		left.VerticalAlignment   = VerticalAlignment.Center;
		hbox.AddChild(left);

		// Right-aligned points with a reserved width so it never collapses to a
		// zero-width column (which previously wrapped "500 pts" vertically).
		var pts = MakeLabel($"{points} pts", 30, new Color(0.25f, 0.28f, 0.45f));
		pts.AutowrapMode       = TextServer.AutowrapMode.Off;
		pts.HorizontalAlignment = HorizontalAlignment.Right;
		pts.VerticalAlignment   = VerticalAlignment.Center;
		pts.CustomMinimumSize   = new Vector2(160, 0);
		hbox.AddChild(pts);

		ResultsList?.AddChild(panel);
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	// Tab styles, created once. Selected = warm accent with an underline; normal = gray.
	private StyleBoxFlat _tabNormal;
	private StyleBoxFlat _tabSelected;

	/// <summary>Applies the selected/normal accent style to a tab button.</summary>
	private void SetTabSelected(Button tab, bool selected)
	{
		if (tab == null) return;

		_tabNormal ??= new StyleBoxFlat
		{
			BgColor          = new Color(0.82f, 0.82f, 0.85f),
			BorderColor      = new Color(0.45f, 0.45f, 0.48f),
			BorderWidthLeft  = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
			CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
			CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
			ContentMarginLeft = 8, ContentMarginRight = 8
		};
		_tabSelected ??= new StyleBoxFlat
		{
			BgColor          = new Color(0.99f, 0.92f, 0.76f),
			BorderColor      = new Color(0.85f, 0.55f, 0.12f),
			BorderWidthLeft  = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 4,
			CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
			CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
			ContentMarginLeft = 8, ContentMarginRight = 8
		};

		var style = selected ? _tabSelected : _tabNormal;
		tab.Modulate = Colors.White; // clear any earlier dimming
		tab.AddThemeStyleboxOverride("normal",  style);
		tab.AddThemeStyleboxOverride("hover",   style);
		tab.AddThemeStyleboxOverride("pressed", style);
		tab.AddThemeStyleboxOverride("focus",   style);
	}

	// Light row background, created once and reused across all rows.
	private StyleBoxFlat _rowStyle;

	/// <summary>Light, padded rounded panel style for one match row (white theme).</summary>
	private StyleBoxFlat RowStyle()
	{
		if (_rowStyle != null) return _rowStyle;
		_rowStyle = new StyleBoxFlat
		{
			BgColor          = new Color(1f, 1f, 1f, 0.7f),
			BorderColor      = new Color(0.6f, 0.6f, 0.63f),
			BorderWidthLeft  = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
			CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
			CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
			ContentMarginLeft = 12, ContentMarginRight = 12,
			ContentMarginTop = 8, ContentMarginBottom = 8
		};
		return _rowStyle;
	}

	/// <summary>
	/// Creates a wrapping RichTextLabel (BBCode enabled) with the board font and a
	/// default colour. Used for the header so the winner name can be tinted orange.
	/// </summary>
	private RichTextLabel MakeRichLabel(string bbcode, int fontSize, Color defaultColor)
	{
		var label = new RichTextLabel
		{
			BbcodeEnabled = true,
			FitContent    = true,
			ScrollActive  = false,
			AutowrapMode  = TextServer.AutowrapMode.WordSmart
		};
		label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		label.AddThemeColorOverride("default_color", defaultColor);
		label.AddThemeFontSizeOverride("normal_font_size", fontSize);
		if (_font != null) label.AddThemeFontOverride("normal_font", _font);
		label.Text = bbcode;
		return label;
	}

	/// <summary>Escapes square brackets so usernames can't inject BBCode tags.</summary>
	private static string BBEscape(string s) => s.Replace("[", "[lb]");

	/// <summary>Creates a wrapping Label with the board font, given size, and colour.</summary>
	private Label MakeLabel(string text, int fontSize, Color color)
	{
		var label = new Label
		{
			Text         = text,
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		};
		label.AddThemeColorOverride("font_color", color);
		label.AddThemeFontSizeOverride("font_size", fontSize);
		if (_font != null) label.AddThemeFontOverride("font", _font);
		return label;
	}

	private static string FormatDuration(long seconds)
	{
		if (seconds <= 0) return "—";
		var t = TimeSpan.FromSeconds(seconds);
		return t.TotalHours >= 1
			? $"{(int)t.TotalHours}h {t.Minutes}m"
			: $"{t.Minutes}m {t.Seconds:00}s";
	}

	private void ClearRows()
	{
		if (ResultsList == null) return;
		foreach (var child in ResultsList.GetChildren())
			child.QueueFree();
	}

	private void SetStatus(string text)
	{
		if (StatusLabel != null) StatusLabel.Text = text;
	}

	private static int GetInt(Godot.Collections.Dictionary d, string key) =>
		d.ContainsKey(key) ? d[key].AsInt32() : 0;

	private static long GetLong(Godot.Collections.Dictionary d, string key) =>
		d.ContainsKey(key) ? d[key].AsInt64() : 0;

	private static string GetStr(Godot.Collections.Dictionary d, string key) =>
		d.ContainsKey(key) ? d[key].AsString() : "";
}
