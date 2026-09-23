using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mesen.Mcp
{
	public static class McpJson
	{
		//Relaxed escaping keeps quotes/apostrophes readable in tool output (responses are never embedded in HTML)
		public static readonly JsonSerializerOptions Options = new JsonSerializerOptions() {
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			WriteIndented = false
		};

		public static string Serialize(JsonNode node)
		{
			return node.ToJsonString(Options);
		}
	}
}
