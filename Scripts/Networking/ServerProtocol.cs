// ═══════════════════════════════════════════════════
// ServerProtocol.cs
// Low-level TCP client that speaks the MER binary protocol.
// Handles register and login requests synchronously (meant to be
// called from Task.Run on a background thread).
// ═══════════════════════════════════════════════════
using System;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// Low-level TCP binary protocol matching the MER server spec.
/// Packet layout: [1 byte requestType][12 bytes username ASCII][12 bytes password ASCII]
/// Response: [1 byte code][128 bytes message ASCII null-terminated]
/// All public methods are blocking — always invoke them from a background thread.
/// </summary>
public static class ServerProtocol
{
	public const string DefaultHost = "bolty.website";
	public const int    DefaultPort = 8888;

	private const int  MaxUsername  = 12;
	private const int  MaxPassword  = 12;
	private const byte ReqRegister  = 1;
	private const byte ReqLogin     = 2;

	/// <summary>Server response codes as defined in the MER protocol.</summary>
	public enum ResponseCode : byte
	{
		Success            = 0,
		UserExists         = 1,
		InvalidCredentials = 2,
		Database           = 3,
		InvalidInput       = 4,
		Unknown            = 99
	}

	/// <summary>Parsed result returned from every server call.</summary>
	public class ServerResult
	{
		/// <summary>True when the server returned <see cref="ResponseCode.Success"/>.</summary>
		public bool         IsSuccess { get; set; }
		/// <summary>Raw response code from the server.</summary>
		public ResponseCode Code      { get; set; }
		/// <summary>Human-readable message from the server, or an error description.</summary>
		public string       Message   { get; set; } = "";
		/// <summary>Server-assigned user ID on login success; -1 otherwise.</summary>
		public int          UserId    { get; set; } = -1;
	}

	// ── Public entry points ───────────────────────────────────────────────────

	/// <summary>
	/// Sends a registration request to the server.
	/// </summary>
	public static ServerResult RegisterUser(string host, int port, string username, string password)
	{
		return SendCredentials(host, port, ReqRegister, username, password);
	}

	/// <summary>
	/// Sends a login request to the server and parses the returned user ID
	/// from the response message ("...ID 42...").
	/// </summary>
	public static ServerResult LoginUser(string host, int port, string username, string password)
	{
		var result = SendCredentials(host, port, ReqLogin, username, password);
		if (!result.IsSuccess) return result;

		// Extract numeric ID from the server message (format: "...ID <number>")
		var msg   = result.Message ?? string.Empty;
		int idIdx = msg.IndexOf("ID ", StringComparison.OrdinalIgnoreCase);
		if (idIdx >= 0)
		{
			var idPart = msg.Substring(idIdx + 3).Trim();
			if (int.TryParse(idPart, out int id))
				result.UserId = id;
		}

		return result;
	}

	// ── Internal helpers ──────────────────────────────────────────────────────

	/// <summary>
	/// Opens a TCP connection, writes a credential packet, and reads the response.
	/// Times out after 5 seconds if the server does not accept the connection.
	/// </summary>
	private static ServerResult SendCredentials(string host, int port, byte requestType, string username, string password)
	{
		try
		{
			using var client = new TcpClient();
			client.ConnectAsync(host, port).Wait(5000);
			if (!client.Connected)
				return Fail(ResponseCode.Unknown, "Connection timed out.");

			using var stream = client.GetStream();

			// Build fixed-size packet: [type][username 12B][password 12B]
			var packet = new byte[1 + MaxUsername + MaxPassword];
			packet[0] = requestType;

			var userBytes = Encoding.ASCII.GetBytes(username);
			var passBytes = Encoding.ASCII.GetBytes(password);
			Array.Copy(userBytes, 0, packet, 1,            Math.Min(userBytes.Length, MaxUsername));
			Array.Copy(passBytes, 0, packet, 1 + MaxUsername, Math.Min(passBytes.Length, MaxPassword));

			stream.Write(packet, 0, packet.Length);
			return ReadResponse(stream);
		}
		catch (Exception ex)
		{
			return Fail(ResponseCode.Unknown, "Network error: " + ex.Message);
		}
	}

	/// <summary>
	/// Reads a 1-byte code followed by up to 128 bytes of message from the stream.
	/// </summary>
	private static ServerResult ReadResponse(NetworkStream stream)
	{
		var header = new byte[1];
		int read   = stream.Read(header, 0, 1);
		if (read != 1)
			return Fail(ResponseCode.Unknown, "Invalid response from server.");

		var code   = (ResponseCode)header[0];
		var msgBuf = new byte[128];
		int offset = 0;

		// Read until the buffer is full or the server closes the connection.
		while (offset < msgBuf.Length)
		{
			int chunk = stream.Read(msgBuf, offset, msgBuf.Length - offset);
			if (chunk <= 0) break;
			offset += chunk;
		}

		var message = Encoding.ASCII.GetString(msgBuf).TrimEnd('\0', '\r', '\n');
		return new ServerResult
		{
			IsSuccess = code == ResponseCode.Success,
			Code      = code,
			Message   = string.IsNullOrWhiteSpace(message) ? "No response." : message
		};
	}

	private static ServerResult Fail(ResponseCode code, string message) =>
		new ServerResult { IsSuccess = false, Code = code, Message = message };
}
