using Mesen.Config;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp
{
	public enum McpServerState
	{
		Stopped,
		Listening,
		Error
	}

	/// <summary>
	/// Model Context Protocol server (Streamable HTTP transport) exposing the debugger to AI agents.
	/// Listens on the loopback interface only, at http://127.0.0.1:[port]/mcp
	/// </summary>
	public class McpServer
	{
		public static McpServer Instance { get; } = new McpServer();

		public const int DefaultPort = 8765;
		public const string EndpointPath = "/mcp";

		private const int MaxConnections = 32;
		private const int MaxConcurrentRequests = 4;
		private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

		private object _lock = new();
		private List<TcpListener> _listeners = new();
		private CancellationTokenSource? _cts;
		private McpProtocol? _protocol;
		private SemaphoreSlim _requestSemaphore = new(MaxConcurrentRequests);
		private int _connectionCount;
		private Timer? _applyTimer;
		private long _requestCount;

		public McpServerState State { get; private set; } = McpServerState.Stopped;
		public string? LastError { get; private set; }
		public int Port { get; private set; }
		public DateTime? LastRequestTime { get; private set; }
		public long RequestCount { get { return Interlocked.Read(ref _requestCount); } }
		public int ActiveClientCount { get { return _protocol?.ActiveClientCount ?? 0; } }
		public string Url { get { return $"http://127.0.0.1:{(Port > 0 ? Port : ConfigManager.Config.Debug.Integration.McpServerPort)}{EndpointPath}"; } }

		public event EventHandler? StatusChanged;

		/// <summary>Starts, stops or restarts the server to match the current configuration (debounced)</summary>
		public void ApplyConfig()
		{
			lock(_lock) {
				_applyTimer?.Dispose();
				_applyTimer = new Timer(_ => Reconcile(), null, 500, Timeout.Infinite);
			}
		}

		public void Stop()
		{
			lock(_lock) {
				_applyTimer?.Dispose();
				_applyTimer = null;
				StopInternal();
			}
			StatusChanged?.Invoke(this, EventArgs.Empty);
		}

		private void Reconcile()
		{
			IntegrationConfig cfg = ConfigManager.Config.Debug.Integration;
			lock(_lock) {
				if(cfg.McpServerEnabled) {
					if(State != McpServerState.Listening || Port != cfg.McpServerPort) {
						StopInternal();
						StartInternal(cfg.McpServerPort);
					}
				} else if(State != McpServerState.Stopped) {
					StopInternal();
				}
			}
			StatusChanged?.Invoke(this, EventArgs.Empty);
		}

		private void StartInternal(int port)
		{
			Port = port;
			LastError = null;

			try {
				McpToolRegistry tools = McpTools.CreateRegistry();
				_protocol = new McpProtocol(tools, EmuApi.GetMesenVersion().ToString(3));

				TcpListener ipv4 = new TcpListener(IPAddress.Loopback, port);
				ipv4.Start();
				_listeners.Add(ipv4);

				if(Socket.OSSupportsIPv6) {
					//Some clients resolve "localhost" to ::1 first
					try {
						TcpListener ipv6 = new TcpListener(IPAddress.IPv6Loopback, port);
						ipv6.Start();
						_listeners.Add(ipv6);
					} catch(SocketException) {
						//IPv6 loopback unavailable, IPv4 is enough
					}
				}
			} catch(Exception ex) {
				StopInternal();
				State = McpServerState.Error;
				LastError = ex is SocketException sockEx && sockEx.SocketErrorCode == SocketError.AddressAlreadyInUse ? $"Port {port} is already in use." : ex.Message;
				EmuApi.WriteLogEntry("[MCP] Could not start server: " + LastError);
				return;
			}

			_cts = new CancellationTokenSource();
			foreach(TcpListener listener in _listeners) {
				TcpListener l = listener;
				CancellationToken ct = _cts.Token;
				Task.Run(() => AcceptLoop(l, ct));
			}

			McpDebugSession.Instance.Start();
			State = McpServerState.Listening;
			EmuApi.WriteLogEntry("[MCP] Server listening on " + Url);
		}

		private void StopInternal()
		{
			_cts?.Cancel();
			_cts?.Dispose();
			_cts = null;

			foreach(TcpListener listener in _listeners) {
				try {
					listener.Stop();
				} catch(SocketException) {
				}
			}
			_listeners.Clear();

			if(State == McpServerState.Listening) {
				McpDebugSession.Instance.Stop();
				EmuApi.WriteLogEntry("[MCP] Server stopped");
			}
			State = McpServerState.Stopped;
			_protocol = null;
			Port = 0;
		}

		private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
		{
			while(!ct.IsCancellationRequested) {
				TcpClient client;
				try {
					client = await listener.AcceptTcpClientAsync(ct);
				} catch(OperationCanceledException) {
					break;
				} catch(ObjectDisposedException) {
					break;
				} catch(SocketException) {
					if(ct.IsCancellationRequested) {
						break;
					}
					continue;
				}

				if(Interlocked.Increment(ref _connectionCount) > MaxConnections) {
					Interlocked.Decrement(ref _connectionCount);
					client.Dispose();
					continue;
				}

				_ = Task.Run(async () => {
					try {
						await HandleConnectionAsync(client, ct);
					} finally {
						Interlocked.Decrement(ref _connectionCount);
						client.Dispose();
					}
				});
			}
		}

		private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
		{
			if(client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address)) {
				//Should never happen since we only listen on loopback addresses
				return;
			}

			using McpHttpConnection connection = new McpHttpConnection(client);
			try {
				while(!ct.IsCancellationRequested) {
					McpRawHttpRequest? request;
					try {
						request = await connection.ReadRequestAsync(IdleTimeout, ct);
					} catch(McpHttpException ex) {
						await WriteErrorAsync(connection, ex.StatusCode, ex.Message, ct);
						return;
					}

					if(request == null) {
						return;
					}

					McpHttpResponse response = await ProcessRequestAsync(connection, request, ct);
					byte[]? body = response.Body != null ? Encoding.UTF8.GetBytes(McpJson.Serialize(response.Body)) : null;
					if(body != null) {
						response.Headers["Content-Type"] = "application/json";
					}
					await connection.WriteResponseAsync(response.StatusCode, response.Headers, body, request.KeepAlive, ct);

					if(!request.KeepAlive) {
						return;
					}
				}
			} catch(OperationCanceledException) {
			} catch(System.IO.IOException) {
			} catch(ObjectDisposedException) {
			} catch(Exception ex) {
				EmuApi.WriteLogEntry("[MCP] Connection error: " + ex.Message);
			}
		}

		private async Task<McpHttpResponse> ProcessRequestAsync(McpHttpConnection connection, McpRawHttpRequest request, CancellationToken ct)
		{
			McpProtocol? protocol = _protocol;
			if(protocol == null) {
				return McpHttpResponse.Error(503, null, McpProtocol.InternalError, "Server is shutting down.");
			}

			if(!IsAllowedHost(request.GetHeader("Host"))) {
				return McpHttpResponse.Error(403, null, McpProtocol.InvalidRequest, "Forbidden: invalid Host header.");
			}

			string? origin = request.GetHeader("Origin");
			if(origin != null && !IsAllowedOrigin(origin)) {
				return McpHttpResponse.Error(403, null, McpProtocol.InvalidRequest, "Forbidden: invalid Origin header.");
			}

			string path = request.Target;
			int query = path.IndexOf('?');
			if(query >= 0) {
				path = path.Substring(0, query);
			}
			if(path.TrimEnd('/') != EndpointPath) {
				return new McpHttpResponse(404);
			}

			Interlocked.Increment(ref _requestCount);
			LastRequestTime = DateTime.Now;

			switch(request.Method) {
				case "POST": {
					string? contentType = request.GetHeader("Content-Type");
					if(contentType == null || !contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)) {
						return McpHttpResponse.Error(415, null, McpProtocol.InvalidRequest, "Content-Type must be application/json.");
					}

					string body = Encoding.UTF8.GetString(request.Body);
					McpHttpRequest mcpRequest = new McpHttpRequest(body, request.GetHeader);

					await _requestSemaphore.WaitAsync(ct);
					try {
						//Cancel the request if the client closes the connection (e.g. client-side timeout)
						using CancellationTokenSource requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
						_ = connection.MonitorForDisconnect(ct).ContinueWith(t => {
							if(t.IsCompletedSuccessfully && !t.Result) {
								try {
									requestCts.Cancel();
								} catch(ObjectDisposedException) {
								}
							}
						}, TaskScheduler.Default);

						return await protocol.ProcessPostAsync(mcpRequest, requestCts.Token);
					} catch(OperationCanceledException) when(!ct.IsCancellationRequested) {
						return McpHttpResponse.Error(400, null, McpProtocol.InternalError, "Request cancelled.");
					} finally {
						_requestSemaphore.Release();
					}
				}

				case "DELETE":
					return protocol.ProcessDelete(new McpHttpRequest("", request.GetHeader));

				default: {
					McpHttpResponse response = new McpHttpResponse(405);
					response.Headers["Allow"] = "POST, DELETE";
					return response;
				}
			}
		}

		private static async Task WriteErrorAsync(McpHttpConnection connection, int statusCode, string message, CancellationToken ct)
		{
			try {
				McpHttpResponse response = McpHttpResponse.Error(statusCode, null, McpProtocol.InvalidRequest, message);
				byte[] body = Encoding.UTF8.GetBytes(McpJson.Serialize(response.Body!));
				await connection.WriteResponseAsync(statusCode, new Dictionary<string, string>() { ["Content-Type"] = "application/json" }, body, false, ct);
			} catch(Exception) {
			}
		}

		private bool IsAllowedHost(string? host)
		{
			if(host == null) {
				return false;
			}
			host = host.Trim().ToLowerInvariant();
			string port = ":" + Port;
			return host == "127.0.0.1" + port || host == "localhost" + port || host == "[::1]" + port;
		}

		/// <summary>
		/// Browsers send an Origin header - only loopback origins are accepted (prevents DNS rebinding and drive-by requests from websites).
		/// Non-browser MCP clients do not send an Origin header.
		/// </summary>
		public static bool IsAllowedOrigin(string origin)
		{
			if(!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "http" && uri.Scheme != "https")) {
				return false;
			}
			return uri.Host == "127.0.0.1" || uri.Host == "localhost" || uri.Host == "[::1]";
		}
	}
}
