using Mesen.Config;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp
{
	public class McpToolRegistry
	{
		private List<McpTool> _tools = new();
		private Dictionary<string, McpTool> _toolsByName = new();

		public IReadOnlyList<McpTool> Tools { get { return _tools; } }

		public void Add(McpTool tool)
		{
			if(_toolsByName.ContainsKey(tool.Name)) {
				throw new InvalidOperationException("Duplicate MCP tool: " + tool.Name);
			}
			_tools.Add(tool);
			_toolsByName[tool.Name] = tool;
		}

		public McpTool? Get(string name)
		{
			return _toolsByName.TryGetValue(name, out McpTool? tool) ? tool : null;
		}

		public async Task<JsonObject> InvokeAsync(McpTool tool, McpArgs args, CancellationToken ct)
		{
			if(tool.RequiresWriteAccess && !ConfigManager.Config.Debug.Integration.McpAllowWriteAccess) {
				throw new McpToolException($"'{tool.Name}' modifies emulator state and is disabled. Ask the user to enable 'Allow write access' for the MCP server in Mesen (Debug > Debugger settings > Integration tab).");
			}

			if(tool.RequiresRom && !EmuApi.IsRunning()) {
				throw new McpToolException("No ROM is loaded. Load a ROM in Mesen first (or use load_rom if write access is enabled).");
			}

			return await tool.Handler(args, ct);
		}
	}
}
