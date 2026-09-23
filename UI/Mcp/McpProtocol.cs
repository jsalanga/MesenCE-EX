using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp
{
	/// <summary>
	/// Transport-independent MCP request processing (JSON-RPC 2.0).
	/// Supports both the stateless protocol (2026-07-28: per-request _meta, server/discover)
	/// and the legacy handshake-based protocol (2025-03-26 to 2025-11-25: initialize + Mcp-Session-Id).
	/// </summary>
	public class McpProtocol
	{
		public const string ModernVersion = "2026-07-28";
		public static readonly string[] LegacyVersions = { "2025-11-25", "2025-06-18", "2025-03-26" };
		public static readonly string[] SupportedVersions = { ModernVersion, "2025-11-25", "2025-06-18", "2025-03-26" };

		public const string MetaProtocolVersion = "io.modelcontextprotocol/protocolVersion";
		public const string MetaClientInfo = "io.modelcontextprotocol/clientInfo";
		public const string MetaServerInfo = "io.modelcontextprotocol/serverInfo";

		public const int ParseError = -32700;
		public const int InvalidRequest = -32600;
		public const int MethodNotFound = -32601;
		public const int InvalidParams = -32602;
		public const int InternalError = -32603;
		public const int HeaderMismatch = -32020;
		public const int UnsupportedProtocolVersion = -32022;

		private const int MaxSessions = 64;
		private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(30);
		private static readonly TimeSpan ClientActivityWindow = TimeSpan.FromMinutes(5);

		private McpToolRegistry _tools;
		private string _serverVersion;
		private ConcurrentDictionary<string, LegacySession> _sessions = new();
		private ConcurrentDictionary<string, DateTime> _recentClients = new();

		public McpProtocol(McpToolRegistry tools, string serverVersion)
		{
			_tools = tools;
			_serverVersion = serverVersion;
		}

		public int ActiveClientCount
		{
			get
			{
				DateTime threshold = DateTime.UtcNow - ClientActivityWindow;
				return _recentClients.Count(kvp => kvp.Value >= threshold);
			}
		}

		public async Task<McpHttpResponse> ProcessPostAsync(McpHttpRequest request, CancellationToken ct)
		{
			JsonNode? root;
			try {
				root = JsonNode.Parse(request.Body);
			} catch(JsonException ex) {
				return McpHttpResponse.Error(400, null, ParseError, "Parse error: " + ex.Message);
			}

			if(root is not JsonObject msg) {
				if(root is JsonArray) {
					return McpHttpResponse.Error(400, null, InvalidRequest, "JSON-RPC batching is not supported.");
				}
				return McpHttpResponse.Error(400, null, InvalidRequest, "Invalid request: expected a JSON-RPC object.");
			}

			JsonNode? id = msg["id"]?.DeepClone();
			string? method = GetPathString(msg, "method");
			if(method == null) {
				if(msg.ContainsKey("result") || msg.ContainsKey("error")) {
					//Response to a server->client request - this server never sends any, ignore it
					return McpHttpResponse.Accepted();
				}
				return McpHttpResponse.Error(400, id, InvalidRequest, "Invalid request: missing 'method'.");
			}
			if((msg["jsonrpc"] as JsonValue)?.TryGetValue(out string? rpcVersion) != true || rpcVersion != "2.0") {
				return McpHttpResponse.Error(400, id, InvalidRequest, "Invalid request: 'jsonrpc' must be \"2.0\".");
			}

			bool isNotification = !msg.ContainsKey("id");
			if(!isNotification && id is not JsonValue) {
				return McpHttpResponse.Error(400, null, InvalidRequest, "Invalid request: 'id' must be a string or a number.");
			}

			JsonObject? parameters = msg["params"] as JsonObject;
			if(msg["params"] != null && parameters == null) {
				return McpHttpResponse.Error(400, id, InvalidRequest, "Invalid request: 'params' must be an object.");
			}

			string? metaVersion = GetMetaString(parameters, MetaProtocolVersion);
			string? headerVersion = request.GetHeader("MCP-Protocol-Version");

			if(method == "initialize" || (metaVersion == null && (headerVersion == null || LegacyVersions.Contains(headerVersion)))) {
				return await ProcessLegacyAsync(request, id, method, parameters, isNotification, headerVersion, ct);
			} else {
				return await ProcessModernAsync(request, id, method, parameters, isNotification, metaVersion, headerVersion, ct);
			}
		}

		public McpHttpResponse ProcessDelete(McpHttpRequest request)
		{
			string? sessionId = request.GetHeader("Mcp-Session-Id");
			if(sessionId != null && _sessions.TryRemove(sessionId, out _)) {
				_recentClients.TryRemove("session:" + sessionId, out _);
				return new McpHttpResponse(200);
			}
			return sessionId != null ? new McpHttpResponse(404) : new McpHttpResponse(405);
		}

		private async Task<McpHttpResponse> ProcessModernAsync(McpHttpRequest request, JsonNode? id, string method, JsonObject? parameters, bool isNotification, string? metaVersion, string? headerVersion, CancellationToken ct)
		{
			if(isNotification) {
				//The stateless protocol defines no client->server notifications over HTTP, accept and ignore any
				return McpHttpResponse.Accepted();
			}

			if(metaVersion == null) {
				return McpHttpResponse.Error(400, id, HeaderMismatch, $"Header mismatch: MCP-Protocol-Version header is '{headerVersion}' but params._meta is missing '{MetaProtocolVersion}'.");
			}

			if(metaVersion != ModernVersion) {
				JsonObject data = new JsonObject() {
					["supported"] = new JsonArray(SupportedVersions.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
					["requested"] = metaVersion
				};
				return McpHttpResponse.Error(400, id, UnsupportedProtocolVersion, "Unsupported protocol version", data);
			}

			//Validate the standard request headers against the body
			if(headerVersion == null) {
				return McpHttpResponse.Error(400, id, HeaderMismatch, "Header mismatch: missing required MCP-Protocol-Version header.");
			} else if(headerVersion != metaVersion) {
				return McpHttpResponse.Error(400, id, HeaderMismatch, $"Header mismatch: MCP-Protocol-Version header value '{headerVersion}' does not match body value '{metaVersion}'.");
			}

			string? headerMethod = request.GetHeader("Mcp-Method");
			if(headerMethod == null) {
				return McpHttpResponse.Error(400, id, HeaderMismatch, "Header mismatch: missing required Mcp-Method header.");
			} else if(headerMethod != method) {
				return McpHttpResponse.Error(400, id, HeaderMismatch, $"Header mismatch: Mcp-Method header value '{headerMethod}' does not match body value '{method}'.");
			}

			if(method == "tools/call" || method == "resources/read" || method == "prompts/get") {
				string? bodyName = GetPathString(parameters, method == "resources/read" ? "uri" : "name");
				string? headerName = DecodeHeaderValue(request.GetHeader("Mcp-Name"));
				if(headerName == null) {
					return McpHttpResponse.Error(400, id, HeaderMismatch, "Header mismatch: missing required Mcp-Name header.");
				} else if(headerName != bodyName) {
					return McpHttpResponse.Error(400, id, HeaderMismatch, $"Header mismatch: Mcp-Name header value '{headerName}' does not match body value '{bodyName}'.");
				}
			}

			string? clientName = GetPathString(parameters, "_meta", MetaClientInfo, "name");
			_recentClients["client:" + (clientName ?? "unknown")] = DateTime.UtcNow;

			JsonObject result;
			try {
				switch(method) {
					case "server/discover":
						result = new JsonObject() {
							["supportedVersions"] = new JsonArray(SupportedVersions.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
							["capabilities"] = GetCapabilities(),
							["instructions"] = McpInstructions.Text,
							["ttlMs"] = 3600000,
							["cacheScope"] = "private"
						};
						break;

					case "tools/list":
						result = GetToolList();
						result["ttlMs"] = 3600000;
						result["cacheScope"] = "private";
						break;

					case "tools/call":
						result = await CallToolAsync(parameters, ct);
						break;

					default:
						return McpHttpResponse.Error(404, id, MethodNotFound, $"Method not found: {method}");
				}
			} catch(McpProtocolException ex) {
				return McpHttpResponse.Error(200, id, ex.Code, ex.Message, ex.ErrorData);
			}

			result["resultType"] = "complete";
			result["_meta"] = new JsonObject() { [MetaServerInfo] = GetServerInfo() };
			return McpHttpResponse.Result(id, result);
		}

		private async Task<McpHttpResponse> ProcessLegacyAsync(McpHttpRequest request, JsonNode? id, string method, JsonObject? parameters, bool isNotification, string? headerVersion, CancellationToken ct)
		{
			string? sessionId = request.GetHeader("Mcp-Session-Id");
			LegacySession? session = null;

			if(method == "initialize") {
				if(isNotification) {
					return McpHttpResponse.Error(400, null, InvalidRequest, "initialize must be a request.");
				}

				string? requested = GetPathString(parameters, "protocolVersion");
				string negotiated = requested != null && LegacyVersions.Contains(requested) ? requested : LegacyVersions[0];
				string? clientName = GetPathString(parameters, "clientInfo", "name");

				PurgeSessions();
				session = new LegacySession(negotiated, clientName ?? "unknown");
				string newSessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
				_sessions[newSessionId] = session;
				_recentClients["session:" + newSessionId] = DateTime.UtcNow;

				JsonObject initResult = new JsonObject() {
					["protocolVersion"] = negotiated,
					["capabilities"] = GetCapabilities(),
					["serverInfo"] = GetServerInfo(),
					["instructions"] = McpInstructions.Text
				};
				McpHttpResponse response = McpHttpResponse.Result(id, initResult);
				response.Headers["Mcp-Session-Id"] = newSessionId;
				return response;
			}

			if(sessionId != null) {
				if(!_sessions.TryGetValue(sessionId, out session)) {
					return McpHttpResponse.Error(404, id, InvalidRequest, "Session not found or expired, send a new initialize request.");
				}
				session.LastSeen = DateTime.UtcNow;
				_recentClients["session:" + sessionId] = DateTime.UtcNow;

				if(headerVersion != null && headerVersion != session.ProtocolVersion) {
					return McpHttpResponse.Error(400, id, InvalidRequest, $"MCP-Protocol-Version header '{headerVersion}' does not match the negotiated version '{session.ProtocolVersion}'.");
				}
			} else {
				_recentClients["legacy:anonymous"] = DateTime.UtcNow;
			}

			if(isNotification) {
				//notifications/initialized, notifications/cancelled, etc.
				return McpHttpResponse.Accepted();
			}

			JsonObject result;
			try {
				switch(method) {
					case "ping":
						result = new JsonObject();
						break;

					case "tools/list":
						result = GetToolList();
						break;

					case "tools/call":
						result = await CallToolAsync(parameters, ct);
						break;

					case "logging/setLevel":
						result = new JsonObject();
						break;

					default:
						return McpHttpResponse.Error(200, id, MethodNotFound, $"Method not found: {method}");
				}
			} catch(McpProtocolException ex) {
				return McpHttpResponse.Error(200, id, ex.Code, ex.Message, ex.ErrorData);
			}

			return McpHttpResponse.Result(id, result);
		}

		private JsonObject GetCapabilities()
		{
			return new JsonObject() {
				["tools"] = new JsonObject() { ["listChanged"] = false }
			};
		}

		private JsonObject GetServerInfo()
		{
			return new JsonObject() {
				["name"] = "mesen",
				["title"] = "Mesen debugger",
				["version"] = _serverVersion
			};
		}

		private JsonObject GetToolList()
		{
			JsonArray tools = new JsonArray();
			foreach(McpTool tool in _tools.Tools) {
				tools.Add((JsonNode)tool.ToJson());
			}
			return new JsonObject() { ["tools"] = tools };
		}

		private async Task<JsonObject> CallToolAsync(JsonObject? parameters, CancellationToken ct)
		{
			string? name = GetPathString(parameters, "name");
			if(name == null) {
				throw new McpProtocolException(InvalidParams, "Missing tool name.");
			}

			McpTool? tool = _tools.Get(name);
			if(tool == null) {
				throw new McpProtocolException(InvalidParams, $"Unknown tool: {name}");
			}

			JsonNode? argsNode = parameters?["arguments"];
			if(argsNode != null && argsNode is not JsonObject) {
				throw new McpProtocolException(InvalidParams, "'arguments' must be an object.");
			}

			JsonObject structured;
			try {
				McpArgs args = new McpArgs(argsNode as JsonObject);
				args.ValidateKnownArguments(tool.InputSchema);
				structured = await _tools.InvokeAsync(tool, args, ct);
			} catch(McpToolException ex) {
				return new JsonObject() {
					["content"] = new JsonArray(new JsonObject() { ["type"] = "text", ["text"] = ex.Message }),
					["isError"] = true
				};
			} catch(OperationCanceledException) {
				throw;
			} catch(Exception ex) {
				return new JsonObject() {
					["content"] = new JsonArray(new JsonObject() { ["type"] = "text", ["text"] = "Internal error: " + ex.Message }),
					["isError"] = true
				};
			}

			return new JsonObject() {
				["content"] = new JsonArray(new JsonObject() { ["type"] = "text", ["text"] = McpJson.Serialize(structured) }),
				["structuredContent"] = structured,
				["isError"] = false
			};
		}

		private void PurgeSessions()
		{
			DateTime now = DateTime.UtcNow;
			foreach(KeyValuePair<string, LegacySession> kvp in _sessions) {
				if(now - kvp.Value.LastSeen > SessionTimeout) {
					_sessions.TryRemove(kvp.Key, out _);
				}
			}

			while(_sessions.Count >= MaxSessions) {
				KeyValuePair<string, LegacySession> oldest = _sessions.OrderBy(kvp => kvp.Value.LastSeen).First();
				_sessions.TryRemove(oldest.Key, out _);
			}

			foreach(KeyValuePair<string, DateTime> kvp in _recentClients) {
				if(now - kvp.Value > SessionTimeout) {
					_recentClients.TryRemove(kvp.Key, out _);
				}
			}
		}

		private static string? GetMetaString(JsonObject? parameters, string key)
		{
			return GetPathString(parameters, "_meta", key);
		}

		/// <summary>Returns the string at the given property path, or null if any part of the path is missing or has the wrong type</summary>
		private static string? GetPathString(JsonNode? node, params string[] path)
		{
			foreach(string key in path) {
				if(node is not JsonObject obj || !obj.TryGetPropertyValue(key, out node)) {
					return null;
				}
			}
			return (node as JsonValue)?.TryGetValue(out string? value) == true ? value : null;
		}

		/// <summary>Decodes the "=?base64?...?=" sentinel encoding used for non-ASCII header values</summary>
		private static string? DecodeHeaderValue(string? value)
		{
			if(value != null && value.StartsWith("=?base64?") && value.EndsWith("?=") && value.Length >= 11) {
				try {
					return Encoding.UTF8.GetString(Convert.FromBase64String(value.Substring(9, value.Length - 11)));
				} catch(FormatException) {
					return null;
				}
			}
			return value;
		}

		private class LegacySession
		{
			public string ProtocolVersion { get; }
			public string ClientName { get; }
			public DateTime LastSeen { get; set; } = DateTime.UtcNow;

			public LegacySession(string protocolVersion, string clientName)
			{
				ProtocolVersion = protocolVersion;
				ClientName = clientName;
			}
		}
	}

	public class McpHttpRequest
	{
		public string Body { get; }
		private Func<string, string?> _getHeader;

		public McpHttpRequest(string body, Func<string, string?> getHeader)
		{
			Body = body;
			_getHeader = getHeader;
		}

		public string? GetHeader(string name)
		{
			return _getHeader(name);
		}
	}

	public class McpHttpResponse
	{
		public int StatusCode { get; }
		public JsonNode? Body { get; }
		public Dictionary<string, string> Headers { get; } = new();

		public McpHttpResponse(int statusCode, JsonNode? body = null)
		{
			StatusCode = statusCode;
			Body = body;
		}

		public static McpHttpResponse Accepted()
		{
			return new McpHttpResponse(202);
		}

		public static McpHttpResponse Result(JsonNode? id, JsonObject result)
		{
			return new McpHttpResponse(200, new JsonObject() {
				["jsonrpc"] = "2.0",
				["id"] = id,
				["result"] = result
			});
		}

		public static McpHttpResponse Error(int statusCode, JsonNode? id, int code, string message, JsonNode? data = null)
		{
			JsonObject error = new JsonObject() {
				["code"] = code,
				["message"] = message
			};
			if(data != null) {
				error["data"] = data;
			}

			JsonObject body = new JsonObject() { ["jsonrpc"] = "2.0" };
			if(id != null) {
				body["id"] = id;
			}
			body["error"] = error;
			return new McpHttpResponse(statusCode, body);
		}
	}
}
