using Mesen.Mcp.Tools;

namespace Mesen.Mcp
{
	public static class McpTools
	{
		public static McpToolRegistry CreateRegistry()
		{
			//Tools are listed in a deterministic order (helps clients cache the list)
			McpToolRegistry registry = new McpToolRegistry();
			McpStatusTools.Register(registry);
			McpStateTools.Register(registry);
			McpAnalysisTools.Register(registry);
			McpExecutionTools.Register(registry);
			McpLabelTools.Register(registry);
			McpBreakpointTools.Register(registry);
			McpWriteTools.Register(registry);
			return registry;
		}
	}
}
