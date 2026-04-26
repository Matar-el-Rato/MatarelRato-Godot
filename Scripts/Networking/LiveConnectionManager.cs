// ═══════════════════════════════════════════════════
// LiveConnectionManager.cs
// Manages a single persistent TCP connection to the server used
// for server-push notifications (MSG_USER_LIST, MSG_CHAT broadcasts)
// and for sending chat messages (REQ_SEND_CHAT).
//
// After a successful login, Connect() opens the socket and spawns
// a dedicated System.Threading.Thread — not a Task — that blocks
// on NetworkStream.Read() and fires events whenever a server-push
// packet arrives.
//
// All subscribers that touch Godot nodes MUST marshal updates to
// the main thread (e.g. via ConcurrentQueue<Action>) because events
// fire from the background listener thread.
// ═══════════════════════════════════════════════════
using Godot;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public static class LiveConnectionManager
{
	private const byte ReqConnectLive = 9;
	private const byte ReqLogout      = 4;
	private const byte ReqSendChat    = 6;
	private const byte ReqJoinRoom    = 5;
	private const byte ReqLeaveRoom   = 8;
	private const byte ReqReady       = 13;
	private const byte ReqUnready     = 16;
	private const byte MsgUserList    = 10;
	private const byte MsgChat        = 11;
	private const byte MsgRoomState   = 12;
	private const byte MsgCountdown   = 14;
	private const byte MsgGameStart   = 15;
	private const int  MaxUsername    = 12;
	private const int  MaxClients     = 64;
	private const int  MaxChatMessage = 100;
	private const int  MaxRoomPlayers = 4;
	private const int  NumRooms       = 3;

	// ── Shared state ──────────────────────────────────────────────────────────
	//
	// MUTUAL EXCLUSION — _client / _stream:
	// _client and _stream are written in Connect() / Disconnect() on the Godot
	// main thread and read in ListenerLoop() on the background listener thread.
	// We protect lifecycle accesses with _lock. NetworkStream.Read() and Write()
	// are safe to call simultaneously from different threads per .NET docs, so
	// concurrent listen (read) and send (write) do not need further locking.
	//
	// _running is volatile: written by Disconnect() on the main thread and read
	// in ListenerLoop() on the background thread without a full lock acquisition.
	private static readonly object _lock    = new();
	private static volatile bool   _running = false;
	private static TcpClient       _client;
	private static NetworkStream   _stream;
	private static Thread          _listenerThread;
	private static string          _localUsername = "";

	/// <summary>Fired on the background thread when the server pushes an updated user list.</summary>
	public static event Action<List<string>> OnUserListUpdated;

	/// <summary>Fired on the background thread when the server broadcasts a chat message.</summary>
	public static event Action<string, string> OnChatMessageReceived;

	/// <summary>
	/// Fired on the background thread when a room's player list changes.
	/// roomId: 1-3. players: current occupants (empty when room is cleared).
	/// Subscribers MUST NOT touch Godot nodes directly.
	/// </summary>
	public static event Action<int, string[]> OnRoomStateUpdated;

	// ── Countdown / game-start state ──────────────────────────────────────────
	// Written by the background listener thread; read by the Godot main thread
	// in _Process via polling. int/bool reads and writes are atomic on x64.
	private static volatile int  _countdownRoom    = 0;
	private static volatile int  _countdownSeconds = -1;
	private static volatile int  _gameStartRoom    = 0;
	private static volatile bool _gameStartPending = false;

	/// <summary>Room id of the most recent countdown tick (0 if none received).</summary>
	public static int  CountdownRoom    => _countdownRoom;
	/// <summary>Seconds remaining in the most recent countdown tick (-1 if no countdown).</summary>
	public static int  CountdownSeconds => _countdownSeconds;
	/// <summary>Room id of the pending game-start broadcast (check GameStartPending first).</summary>
	public static int  GameStartRoom    => _gameStartRoom;
	/// <summary>True when a MSG_GAME_START has been received and not yet consumed.</summary>
	public static bool GameStartPending => _gameStartPending;
	/// <summary>Consume the pending game-start flag. Call from the main thread.</summary>
	public static void ClearGameStart() => _gameStartPending = false;

	/// <summary>Last player list received from the server.</summary>
	public static List<string> LastKnownPlayers { get; private set; } = new();

	/// <summary>The room the local player is currently in (0 = lobby).</summary>
	public static int CurrentRoomId { get; private set; } = 0;

	/// <summary>Last known player lists per room (index 1-3; index 0 unused).</summary>
	public static string[][] RoomPlayers { get; private set; } = new string[NumRooms + 1][];

	/// <summary>True while the live connection is active.</summary>
	public static bool IsConnected => _running;

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Opens a persistent connection to the server and starts the listener
	/// thread. If already connected, disconnects cleanly first.
	/// </summary>
	public static void Connect(string host, int port, string username, int userId)
	{
		_localUsername = username;
		CurrentRoomId  = 0;
		for (int i = 0; i <= NumRooms; i++) RoomPlayers[i] = System.Array.Empty<string>();

		// RISK: double-connect guard.
		Disconnect();

		try
		{
			var client = new TcpClient();
			bool connected = client.ConnectAsync(host, port).Wait(5000);
			if (!connected || !client.Connected)
			{
				client.Dispose();
				GD.PrintErr("[LCM] Could not connect to server.");
				return;
			}

			// Build REQ_CONNECT_LIVE packet: [type 1B][username 12B][user_id 4B]
			var packet = new byte[1 + MaxUsername + 4];
			packet[0] = ReqConnectLive;

			var userBytes = Encoding.ASCII.GetBytes(username);
			Array.Copy(userBytes, 0, packet, 1, Math.Min(userBytes.Length, MaxUsername));

			// ENDIANNESS: user_id transmitted in network byte order (big-endian).
			int beId    = IPAddress.HostToNetworkOrder(userId);
			var idBytes = BitConverter.GetBytes(beId);
			Array.Copy(idBytes, 0, packet, 1 + MaxUsername, 4);

			var stream = client.GetStream();
			stream.Write(packet, 0, packet.Length);

			// MUTUAL EXCLUSION: publish _client, _stream and _running together.
			lock (_lock)
			{
				_client  = client;
				_stream  = stream;
				_running = true;
			}

			_listenerThread = new Thread(ListenerLoop)
			{
				IsBackground = true,
				Name         = "MER-LiveConnectionListener"
			};
			_listenerThread.Start();
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[LCM] Connect failed: {ex}");
		}
	}

	/// <summary>
	/// Closes the persistent connection and waits for the listener thread to
	/// exit cleanly. Safe to call when not connected.
	/// </summary>
	public static void Disconnect()
	{
		_running = false;

		lock (_lock)
		{
			_client?.Close();
			_client = null;
			_stream = null;
		}

		if (_listenerThread != null && _listenerThread.IsAlive)
			_listenerThread.Join(2000);
		_listenerThread = null;
	}

	/// <summary>
	/// Sends a REQ_SEND_CHAT packet on the live connection.
	/// Silently no-ops if not connected. Max 100 chars enforced client-side.
	/// </summary>
	public static void SendChatMessage(string message)
	{
		if (message.Length > MaxChatMessage)
			message = message.Substring(0, MaxChatMessage);

		NetworkStream stream;
		lock (_lock) { stream = _stream; }
		if (stream == null) return;

		var packet = new byte[1 + MaxChatMessage];
		packet[0] = ReqSendChat;
		var msgBytes = Encoding.ASCII.GetBytes(message);
		Array.Copy(msgBytes, 0, packet, 1, Math.Min(msgBytes.Length, MaxChatMessage));

		// NetworkStream.Write is safe to call concurrently with Read per .NET docs.
		try { stream.Write(packet, 0, packet.Length); }
		catch (Exception ex) { GD.PrintErr($"[LCM] SendChatMessage failed: {ex.Message}"); }
	}

	/// <summary>
	/// Sends REQ_LOGOUT on the live connection so the server removes this client
	/// gracefully before the socket is closed. Safe to call when not connected.
	/// </summary>
	public static void SendLogout()
	{
		NetworkStream stream;
		lock (_lock) { stream = _stream; }
		if (stream == null) return;

		var packet = new byte[] { ReqLogout };
		try { stream.Write(packet, 0, packet.Length); }
		catch (Exception ex) { GD.PrintErr($"[LCM] SendLogout failed: {ex.Message}"); }
	}

	/// <summary>Sends REQ_JOIN_ROOM on the live connection. roomId must be 1-3.</summary>
	public static void SendJoinRoom(int roomId)
	{
		NetworkStream stream;
		lock (_lock) { stream = _stream; }
		if (stream == null) return;

		var packet = new byte[] { ReqJoinRoom, (byte)roomId };
		try { stream.Write(packet, 0, packet.Length); }
		catch (Exception ex) { GD.PrintErr($"[LCM] SendJoinRoom failed: {ex.Message}"); }
	}

	/// <summary>Sends REQ_LEAVE_ROOM on the live connection.</summary>
	public static void SendLeaveRoom()
	{
		NetworkStream stream;
		lock (_lock) { stream = _stream; }
		if (stream == null) return;

		var packet = new byte[] { ReqLeaveRoom };
		try { stream.Write(packet, 0, packet.Length); }
		catch (Exception ex) { GD.PrintErr($"[LCM] SendLeaveRoom failed: {ex.Message}"); }
	}

	/// <summary>
	/// Sends REQ_READY on the live connection. The server checks if all players
	/// in the sender's room are ready and starts a 10-second countdown if so.
	/// </summary>
	public static void SendReady()
	{
		NetworkStream stream;
		lock (_lock) { stream = _stream; }
		if (stream == null) return;

		var packet = new byte[] { ReqReady };
		try { stream.Write(packet, 0, packet.Length); }
		catch (Exception ex) { GD.PrintErr($"[LCM] SendReady failed: {ex.Message}"); }
	}

	/// <summary>Sends REQ_UNREADY on the live connection to cancel a previous ready state.</summary>
	public static void SendUnready()
	{
		NetworkStream stream;
		lock (_lock) { stream = _stream; }
		if (stream == null) return;

		var packet = new byte[] { ReqUnready };
		try { stream.Write(packet, 0, packet.Length); }
		catch (Exception ex) { GD.PrintErr($"[LCM] SendUnready failed: {ex.Message}"); }
	}

	// ── Listener thread ───────────────────────────────────────────────────────

	/// <summary>
	/// Runs on the dedicated listener thread. Blocks on NetworkStream reads
	/// and processes MSG_USER_LIST and MSG_CHAT packets from the server.
	/// </summary>
	private static void ListenerLoop()
	{
		NetworkStream stream;
		lock (_lock)
		{
			if (_client == null) return;
			stream = _stream;
		}

		var typeBuf  = new byte[1];
		var usersBuf = new byte[MaxClients * MaxUsername];
		var chatBuf  = new byte[MaxUsername + MaxChatMessage];

		try
		{
			while (_running)
			{
				// Read 1-byte message type.
				if (!ReadExact(stream, typeBuf, 1)) break;
				byte msgType = typeBuf[0];

				if (msgType == MsgUserList)
				{
					var countBuf = new byte[1];
					if (!ReadExact(stream, countBuf, 1)) break;
					int msgCount = countBuf[0];

					int toRead = msgCount * MaxUsername;
					if (toRead > 0 && !ReadExact(stream, usersBuf, toRead)) break;

					var users = new List<string>(msgCount);
					for (int i = 0; i < msgCount; i++)
					{
						int start = i * MaxUsername;
						int len   = 0;
						while (len < MaxUsername && usersBuf[start + len] != 0) len++;
						users.Add(Encoding.ASCII.GetString(usersBuf, start, len));
					}

					LastKnownPlayers = users;
					OnUserListUpdated?.Invoke(users);
				}
				else if (msgType == MsgChat)
				{
					// Payload: username[12B] + message[100B] = 112 bytes
					if (!ReadExact(stream, chatBuf, MaxUsername + MaxChatMessage)) break;

					int uLen = 0;
					while (uLen < MaxUsername && chatBuf[uLen] != 0) uLen++;
					string sender = Encoding.ASCII.GetString(chatBuf, 0, uLen);

					int mLen = 0;
					while (mLen < MaxChatMessage && chatBuf[MaxUsername + mLen] != 0) mLen++;
					string message = Encoding.ASCII.GetString(chatBuf, MaxUsername, mLen);

					OnChatMessageReceived?.Invoke(sender, message);
				}
				else if (msgType == MsgRoomState)
				{
					// Payload: room_id[1B] + count[1B] + players[4][12B] = 50 bytes
					var roomBuf = new byte[2 + MaxRoomPlayers * MaxUsername];
					if (!ReadExact(stream, roomBuf, roomBuf.Length)) break;

					int roomId = roomBuf[0];
					int count  = roomBuf[1];
					if (count > MaxRoomPlayers) count = MaxRoomPlayers;

					var players = new string[count];
					for (int i = 0; i < count; i++)
					{
						int start = 2 + i * MaxUsername;
						int len   = 0;
						while (len < MaxUsername && roomBuf[start + len] != 0) len++;
						players[i] = Encoding.ASCII.GetString(roomBuf, start, len);
					}

					if (roomId >= 1 && roomId <= NumRooms)
					{
						RoomPlayers[roomId] = players;

						// Update CurrentRoomId: check if our name is in this room.
						bool myNameHere = System.Array.IndexOf(players, _localUsername) >= 0;
						if (myNameHere)
							CurrentRoomId = roomId;
						else if (CurrentRoomId == roomId)
							CurrentRoomId = 0;
					}

					OnRoomStateUpdated?.Invoke(roomId, players);
				}
				else if (msgType == MsgCountdown)
				{
					// Payload: room_id(1B) + seconds(1B)
					var cntBuf = new byte[2];
					if (!ReadExact(stream, cntBuf, 2)) break;
					_countdownRoom    = cntBuf[0];
					_countdownSeconds = cntBuf[1];
				}
				else if (msgType == MsgGameStart)
				{
					// Payload: room_id(1B)
					var gsBuf = new byte[1];
					if (!ReadExact(stream, gsBuf, 1)) break;
					_gameStartRoom    = gsBuf[0];
					_gameStartPending = true;
				}
			}
		}
		catch (Exception ex)
		{
			if (_running)
				GD.PrintErr($"[LCM] ListenerLoop exception: {ex}");
		}

		_running = false;
	}

	/// <summary>
	/// Reads exactly <paramref name="count"/> bytes into <paramref name="buf"/>
	/// starting at offset 0, looping until all bytes arrive or the stream ends.
	/// </summary>
	private static bool ReadExact(NetworkStream stream, byte[] buf, int count)
	{
		int received = 0;
		while (received < count)
		{
			int n = stream.Read(buf, received, count - received);
			if (n <= 0) return false;
			received += n;
		}
		return true;
	}
}
