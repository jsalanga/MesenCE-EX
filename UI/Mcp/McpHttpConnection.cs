using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp
{
	/// <summary>
	/// Minimal HTTP/1.1 server-side connection (request parsing + response writing).
	/// Only what's needed for MCP's Streamable HTTP transport: Content-Length/chunked bodies,
	/// keep-alive, Expect: 100-continue, and detection of the client closing the connection.
	/// </summary>
	public class McpHttpConnection : IDisposable
	{
		public const int MaxHeaderSize = 32 * 1024;
		public const int MaxBodySize = 1024 * 1024;

		private NetworkStream _stream;
		private byte[] _buffer = new byte[64 * 1024];
		private int _start;
		private int _end;
		private bool _eof;
		private Task<bool>? _pendingFill;

		public McpHttpConnection(TcpClient client)
		{
			client.NoDelay = true;
			_stream = client.GetStream();
		}

		public void Dispose()
		{
			_stream.Dispose();
		}

		/// <summary>Reads the next request. Returns null if the connection was closed or timed out before a request arrived.</summary>
		public async Task<McpRawHttpRequest?> ReadRequestAsync(TimeSpan idleTimeout, CancellationToken ct)
		{
			int headerEnd;
			while((headerEnd = FindHeaderEnd()) < 0) {
				if(_end - _start >= MaxHeaderSize) {
					throw new McpHttpException(431, "Request headers too large");
				}

				Task<bool> fill = FillAsync(ct);
				Task completed = await Task.WhenAny(fill, Task.Delay(idleTimeout, ct));
				if(completed != fill) {
					return null;
				}
				if(!await fill) {
					return null;
				}
			}

			string headerText = Encoding.ASCII.GetString(_buffer, _start, headerEnd - _start);
			_start = headerEnd + 4;

			string[] lines = headerText.Split("\r\n");
			string[] requestLine = lines[0].Split(' ');
			if(requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.")) {
				throw new McpHttpException(400, "Malformed request line");
			}

			McpRawHttpRequest request = new McpRawHttpRequest(requestLine[0], requestLine[1], requestLine[2]);
			for(int i = 1; i < lines.Length; i++) {
				int colon = lines[i].IndexOf(':');
				if(colon <= 0) {
					throw new McpHttpException(400, "Malformed header");
				}
				request.AddHeader(lines[i].Substring(0, colon).Trim(), lines[i].Substring(colon + 1).Trim());
			}

			string? transferEncoding = request.GetHeader("Transfer-Encoding");
			string? contentLength = request.GetHeader("Content-Length");
			bool chunked = transferEncoding != null && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);

			if(chunked || contentLength != null) {
				if(request.GetHeader("Expect")?.Equals("100-continue", StringComparison.OrdinalIgnoreCase) == true) {
					byte[] continueResponse = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
					await _stream.WriteAsync(continueResponse, ct);
				}

				if(chunked) {
					request.Body = await ReadChunkedBodyAsync(ct);
				} else {
					if(!int.TryParse(contentLength, out int length) || length < 0) {
						throw new McpHttpException(400, "Invalid Content-Length");
					} else if(length > MaxBodySize) {
						throw new McpHttpException(413, "Request body too large");
					}
					request.Body = await ReadBytesAsync(length, ct);
				}
			}

			return request;
		}

		/// <summary>
		/// Starts reading ahead from the connection while a request is being processed.
		/// The returned task completes with false if the client closes the connection (used to cancel the request).
		/// Any data received is kept for the next request.
		/// </summary>
		public Task<bool> MonitorForDisconnect(CancellationToken ct)
		{
			if(_pendingFill == null) {
				_pendingFill = FillCoreAsync(ct);
			}
			return _pendingFill;
		}

		public async Task WriteResponseAsync(int statusCode, IReadOnlyDictionary<string, string> headers, byte[]? body, bool keepAlive, CancellationToken ct)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(GetReasonPhrase(statusCode)).Append("\r\n");
			foreach(KeyValuePair<string, string> header in headers) {
				sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
			}
			sb.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n");
			sb.Append("Cache-Control: no-store\r\n");
			sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
			sb.Append("\r\n");

			await _stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
			if(body != null && body.Length > 0) {
				await _stream.WriteAsync(body, ct);
			}
			await _stream.FlushAsync(ct);
		}

		private int FindHeaderEnd()
		{
			for(int i = _start; i + 3 < _end; i++) {
				if(_buffer[i] == '\r' && _buffer[i + 1] == '\n' && _buffer[i + 2] == '\r' && _buffer[i + 3] == '\n') {
					return i;
				}
			}
			return -1;
		}

		private async Task<bool> FillAsync(CancellationToken ct)
		{
			if(_pendingFill != null) {
				Task<bool> pending = _pendingFill;
				_pendingFill = null;
				return await pending;
			}
			return await FillCoreAsync(ct);
		}

		private async Task<bool> FillCoreAsync(CancellationToken ct)
		{
			if(_eof) {
				return false;
			}

			if(_start > 0) {
				Array.Copy(_buffer, _start, _buffer, 0, _end - _start);
				_end -= _start;
				_start = 0;
			}

			if(_end == _buffer.Length) {
				//Buffer is full, caller will handle the size limit
				return true;
			}

			int bytesRead;
			try {
				bytesRead = await _stream.ReadAsync(_buffer.AsMemory(_end), ct);
			} catch(IOException) {
				bytesRead = 0;
			} catch(ObjectDisposedException) {
				bytesRead = 0;
			}

			if(bytesRead == 0) {
				_eof = true;
				return false;
			}
			_end += bytesRead;
			return true;
		}

		private async Task<byte[]> ReadBytesAsync(int length, CancellationToken ct)
		{
			byte[] result = new byte[length];
			int copied = 0;
			while(copied < length) {
				if(_start == _end && !await FillAsync(ct)) {
					throw new McpHttpException(400, "Connection closed while reading the request body");
				}
				int count = Math.Min(length - copied, _end - _start);
				Array.Copy(_buffer, _start, result, copied, count);
				_start += count;
				copied += count;
			}
			return result;
		}

		private async Task<string> ReadLineAsync(CancellationToken ct)
		{
			while(true) {
				for(int i = _start; i + 1 < _end; i++) {
					if(_buffer[i] == '\r' && _buffer[i + 1] == '\n') {
						string line = Encoding.ASCII.GetString(_buffer, _start, i - _start);
						_start = i + 2;
						return line;
					}
				}
				if(_end - _start >= 1024) {
					throw new McpHttpException(400, "Malformed chunked body");
				}
				if(!await FillAsync(ct)) {
					throw new McpHttpException(400, "Connection closed while reading the request body");
				}
			}
		}

		private async Task<byte[]> ReadChunkedBodyAsync(CancellationToken ct)
		{
			using MemoryStream body = new MemoryStream();
			while(true) {
				string sizeLine = await ReadLineAsync(ct);
				int semicolon = sizeLine.IndexOf(';');
				if(semicolon >= 0) {
					sizeLine = sizeLine.Substring(0, semicolon);
				}
				if(!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int size) || size < 0) {
					throw new McpHttpException(400, "Malformed chunked body");
				}

				if(size == 0) {
					//Skip trailers
					while((await ReadLineAsync(ct)).Length > 0) {
					}
					return body.ToArray();
				}

				if(body.Length + size > MaxBodySize) {
					throw new McpHttpException(413, "Request body too large");
				}

				byte[] chunk = await ReadBytesAsync(size, ct);
				body.Write(chunk, 0, chunk.Length);
				if((await ReadLineAsync(ct)).Length != 0) {
					throw new McpHttpException(400, "Malformed chunked body");
				}
			}
		}

		private static string GetReasonPhrase(int statusCode)
		{
			return statusCode switch {
				200 => "OK",
				202 => "Accepted",
				400 => "Bad Request",
				403 => "Forbidden",
				404 => "Not Found",
				405 => "Method Not Allowed",
				413 => "Content Too Large",
				415 => "Unsupported Media Type",
				431 => "Request Header Fields Too Large",
				500 => "Internal Server Error",
				503 => "Service Unavailable",
				_ => "Unknown"
			};
		}
	}

	public class McpRawHttpRequest
	{
		private Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

		public string Method { get; }
		public string Target { get; }
		public string Version { get; }
		public byte[] Body { get; set; } = Array.Empty<byte>();

		public McpRawHttpRequest(string method, string target, string version)
		{
			Method = method;
			Target = target;
			Version = version;
		}

		public void AddHeader(string name, string value)
		{
			if(_headers.TryGetValue(name, out string? existing)) {
				_headers[name] = existing + ", " + value;
			} else {
				_headers[name] = value;
			}
		}

		public string? GetHeader(string name)
		{
			return _headers.TryGetValue(name, out string? value) ? value : null;
		}

		public bool KeepAlive
		{
			get
			{
				string? connection = GetHeader("Connection");
				if(Version == "HTTP/1.0") {
					return connection != null && connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
				}
				return connection == null || !connection.Contains("close", StringComparison.OrdinalIgnoreCase);
			}
		}
	}

	public class McpHttpException : Exception
	{
		public int StatusCode { get; }

		public McpHttpException(int statusCode, string message) : base(message)
		{
			StatusCode = statusCode;
		}
	}
}
