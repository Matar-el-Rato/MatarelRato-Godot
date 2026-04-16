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
	private const byte ReqSendChat    = 6;
	private const byte MsgUserList    = 10;
	private const byte MsgChat        = 11;
	private const int  MaxUsername    = 12;
	private const int  MaxClients     = 64;
	private const int  MaxChatMessage = 100;

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

	/// <summary>
	/// Fired on the background listener thread whenever the server pushes an
	/// updated connected-users list. Subscribers MUST NOT touch Godot nodes
	/// directly — enqueue updates via ConcurrentQueue&lt;Action&gt; and apply
	/// them in _Process().
	/// </summary>
	public static event Action<List<string>> OnUserListUpdated;

	/// <summary>
	/// Fired on the background listener thread whenever the server broadcasts
	/// a chat message. Subscribers MUST marshal to the Godot main thread before
	/// touching any Godot node (e.g. via CallDeferred or ConcurrentQueue).
	/// </summary>
	public static event Action<string, string> OnChatMessageReceived;

	/// <summary>
	/// Last player list received from the server. Initialized to an empty list
	/// so callers never receive null.
	/// </summary>
	public static List<string> LastKnownPlayers { get; private set; } = new();

	/// <summary>True while the live connection is active.</summary>
	public static bool IsConnected => _running;

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Opens a persistent connection to the server and starts the listener
	/// thread. If already connected, disconnects cleanly first.
	/// </summary>
	public static void Connect(string host, int port, string username, int userId)
	{
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
