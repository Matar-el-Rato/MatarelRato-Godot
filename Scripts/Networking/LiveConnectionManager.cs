// ═══════════════════════════════════════════════════
// LiveConnectionManager.cs
// Manages a single persistent TCP connection to the server used
// exclusively for server-push notifications (MSG_USER_LIST broadcasts).
//
// After a successful login, Connect() opens the socket and spawns
// a dedicated System.Threading.Thread — not a Task — that blocks
// on NetworkStream.Read() and fires OnUserListUpdated whenever a
// MSG_USER_LIST packet arrives from the server.
//
// All subscribers that touch Godot nodes MUST marshal updates to
// the main thread (e.g. via ConcurrentQueue<Action>) because this
// event fires from the background listener thread.
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
	private const byte MsgUserList    = 10;
	private const int  MaxUsername    = 12;
	private const int  MaxClients     = 64;

	// ── Shared state ──────────────────────────────────────────────────────────
	//
	// MUTUAL EXCLUSION — _client / _stream:
	// _client is written in Connect() / Disconnect() on the Godot main thread
	// and read in ListenerLoop() on the background listener thread. We protect
	// accesses with _lock so that Disconnect() cannot Close() and null _client
	// while the listener thread is mid-read on its NetworkStream. Without the
	// lock a race between Close() and Read() can break
	//
	// _running is volatile: written by Disconnect() on the main thread and read
	// in ListenerLoop() on the background thread. volatile guarantees that the
	// loop always sees the latest value without needing a full lock acquisition
	// on every iteration, avoiding unnecessary blocking.
	private static readonly object _lock    = new();
	private static volatile bool   _running = false;
	private static TcpClient       _client;
	private static Thread          _listenerThread;

	/// <summary>
	/// Fired on the background listener thread whenever the server pushes an
	/// updated connected-users list. Subscribers MUST NOT touch Godot nodes
	/// directly — enqueue updates via ConcurrentQueue&lt;Action&gt; and apply
	/// them in _Process().
	/// </summary>
	public static event Action<List<string>> OnUserListUpdated;

	/// <summary>
	/// Last player list received from the server. Updated on the background
	/// listener thread; read by ConnectedPlayersBoard._Ready() to catch up on
	/// any broadcasts that arrived while the board was out of the tree.
	/// Initialized to an empty list so callers never receive null.
	/// </summary>
	public static List<string> LastKnownPlayers { get; private set; } = new();

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>
	/// Opens a persistent connection to the server and starts the listener
	/// thread. If already connected, disconnects cleanly first.
	/// </summary>
	public static void Connect(string host, int port, string username, int userId)
	{
		// RISK: double-connect guard.
		// If the user logs out and immediately logs back in, a previous listener
		// thread may still be alive. Disconnect() joins the thread before we
		// start a new one, so we never have two listener threads running at once.
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

			// ENDIANNESS: user_id must be transmitted in network byte order
			// (big-endian). The C server calls ntohl() on receipt, so we use
			// IPAddress.HostToNetworkOrder to convert from host byte order.
			int beId    = IPAddress.HostToNetworkOrder(userId);
			var idBytes = BitConverter.GetBytes(beId);
			Array.Copy(idBytes, 0, packet, 1 + MaxUsername, 4);

			client.GetStream().Write(packet, 0, packet.Length);

			// MUTUAL EXCLUSION: write _client and _running together under the
			// lock so ListenerLoop never sees a state where _running is true
			// but _client is still null.
			lock (_lock)
			{
				_client  = client;
				_running = true;
			}

			// Spawn a dedicated System.Threading.Thread (not Task.Run) as
			// required: one thread exclusively handles incoming server messages.
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

		// MUTUAL EXCLUSION: closing _client must be done under the lock.
		// Calling Close() causes the blocked NetworkStream.Read() in the
		// listener thread to throw IOException/ObjectDisposedException, which
		// is the intended .NET shutdown signal. Without the lock, a concurrent
		// Connect() on the main thread could have already written a new TcpClient
		// into _client — and we would close the wrong (new) connection.
		lock (_lock)
		{
			_client?.Close();
			_client = null;
		}

		// Join the listener thread so Connect() can safely start a fresh one.
		// RISK: if the thread is stuck in a blocking recv() with no FIN from
		// the server (e.g. abrupt network loss), Close() will still unblock it
		// by disposing the socket. The 2s timeout is a last-resort safeguard.
		if (_listenerThread != null && _listenerThread.IsAlive)
			_listenerThread.Join(2000);
		_listenerThread = null;
	}

	// ── Listener thread ───────────────────────────────────────────────────────

	/// <summary>
	/// Runs on the dedicated listener thread. Blocks on NetworkStream reads
	/// and processes every MSG_USER_LIST packet the server pushes.
	/// </summary>
	private static void ListenerLoop()
	{
		// MUTUAL EXCLUSION: snapshot the stream reference under the lock.
		// After this point we have a stable local reference; Disconnect() can
		// null _client without affecting our local 'stream' variable.
		NetworkStream stream;
		lock (_lock)
		{
			if (_client == null) return;
			stream = _client.GetStream();
		}

		// Reusable read buffers — allocated once outside the loop.
		var headerBuf = new byte[2];                        // [type][count]
		var usersBuf  = new byte[MaxClients * MaxUsername]; // max payload

		try
		{
			while (_running)
			{
				// Read 2-byte header: message type + user count.
				// PARTIAL READ RISK: TCP is a stream protocol. A single send()
				// on the server may be split across multiple Read() calls here.
				// ReadExact() loops until all expected bytes have arrived.
				if (!ReadExact(stream, headerBuf, 2)) break;

				byte msgType  = headerBuf[0];
				int  msgCount = headerBuf[1];

				if (msgType == MsgUserList)
				{
					int toRead = msgCount * MaxUsername;

					// RISK: empty list (count == 0) is a valid state and must
					// be handled — it means everyone disconnected.
					if (toRead > 0 && !ReadExact(stream, usersBuf, toRead)) break;

					var users = new List<string>(msgCount);
					for (int i = 0; i < msgCount; i++)
					{
						int start = i * MaxUsername;
						// Find the null terminator within the 12-byte window.
						int len = 0;
						while (len < MaxUsername && usersBuf[start + len] != 0) len++;
						users.Add(Encoding.ASCII.GetString(usersBuf, start, len));
					}

					// Cache before firing so a newly-subscribing board can always
					// call LastKnownPlayers and get a non-stale snapshot.
					LastKnownPlayers = users;

					// Fire on background thread — subscribers must marshal to main thread.
					OnUserListUpdated?.Invoke(users);
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
	/// Returns false if the stream closes before all bytes are read.
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
