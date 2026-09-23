using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp
{
	public delegate Task<JsonObject> McpToolHandler(McpArgs args, CancellationToken ct);

	public class McpTool
	{
		public string Name { get; }
		public string Description { get; }
		public JsonObject InputSchema { get; }
		public McpToolHandler Handler { get; }

		/// <summary>Tool changes emulated state (memory, registers, ROM, save states) and requires the "allow write access" setting</summary>
		public bool RequiresWriteAccess { get; init; }

		/// <summary>Tool only reads state (used for the readOnlyHint annotation)</summary>
		public bool ReadOnly { get; init; }

		/// <summary>Tool can destroy state that cannot be recovered (used for the destructiveHint annotation)</summary>
		public bool Destructive { get; init; }

		/// <summary>Tool needs a ROM to be loaded (and the debugger to be attached)</summary>
		public bool RequiresRom { get; init; } = true;

		public McpTool(string name, string description, JsonObject inputSchema, McpToolHandler handler)
		{
			Name = name;
			Description = description;
			InputSchema = inputSchema;
			Handler = handler;
		}

		public JsonObject ToJson()
		{
			return new JsonObject() {
				["name"] = Name,
				["description"] = Description + (RequiresWriteAccess ? " Requires 'Allow write access' to be enabled in Mesen's debugger settings (Integration tab)." : ""),
				["inputSchema"] = InputSchema.DeepClone(),
				["annotations"] = new JsonObject() {
					["readOnlyHint"] = ReadOnly,
					["destructiveHint"] = Destructive,
					["openWorldHint"] = false
				}
			};
		}
	}

	/// <summary>Error returned to the client as a tool result with isError=true (the model can read it and recover)</summary>
	public class McpToolException : Exception
	{
		public McpToolException(string message) : base(message) { }
	}

	/// <summary>Error returned to the client as a JSON-RPC error</summary>
	public class McpProtocolException : Exception
	{
		public int Code { get; }
		public JsonNode? ErrorData { get; }

		public McpProtocolException(int code, string message, JsonNode? data = null) : base(message)
		{
			Code = code;
			ErrorData = data;
		}
	}

	/// <summary>Small helper to build JSON schemas for tool inputs</summary>
	public class McpSchema
	{
		private JsonObject _properties = new JsonObject();
		private JsonArray _required = new JsonArray();

		public static McpSchema Create()
		{
			return new McpSchema();
		}

		public McpSchema String(string name, string description, bool required = false, IEnumerable<string>? values = null)
		{
			JsonObject prop = new JsonObject() { ["type"] = "string", ["description"] = description };
			if(values != null) {
				JsonArray enumValues = new JsonArray();
				foreach(string value in values) {
					enumValues.Add((JsonNode)value);
				}
				prop["enum"] = enumValues;
			}
			return Add(name, prop, required);
		}

		public McpSchema Integer(string name, string description, bool required = false, long? min = null, long? max = null)
		{
			JsonObject prop = new JsonObject() { ["type"] = "integer", ["description"] = description };
			if(min != null) {
				prop["minimum"] = min.Value;
			}
			if(max != null) {
				prop["maximum"] = max.Value;
			}
			return Add(name, prop, required);
		}

		public McpSchema Boolean(string name, string description, bool required = false)
		{
			return Add(name, new JsonObject() { ["type"] = "boolean", ["description"] = description }, required);
		}

		/// <summary>Addresses are accepted as a number or as a hex string ("$8000", "0x8000", "8000h", "$00:8000")</summary>
		public McpSchema Address(string name, string description, bool required = false)
		{
			JsonObject prop = new JsonObject() {
				["type"] = new JsonArray("string", "integer"),
				["description"] = description + " JSON number (decimal), or string (always hex): \"$8000\", \"0x8000\", \"8000\" or bank:address \"$80:8000\"."
			};
			return Add(name, prop, required);
		}

		public McpSchema StringArray(string name, string description, bool required = false, IEnumerable<string>? values = null)
		{
			JsonObject items = new JsonObject() { ["type"] = "string" };
			if(values != null) {
				JsonArray enumValues = new JsonArray();
				foreach(string value in values) {
					enumValues.Add((JsonNode)value);
				}
				items["enum"] = enumValues;
			}
			return Add(name, new JsonObject() { ["type"] = "array", ["description"] = description, ["items"] = items }, required);
		}

		private McpSchema Add(string name, JsonObject prop, bool required)
		{
			_properties[name] = prop;
			if(required) {
				_required.Add((JsonNode)name);
			}
			return this;
		}

		public JsonObject Build()
		{
			JsonObject schema = new JsonObject() {
				["type"] = "object",
				["properties"] = _properties,
				["additionalProperties"] = false
			};
			if(_required.Count > 0) {
				schema["required"] = _required;
			}
			return schema;
		}
	}
}
