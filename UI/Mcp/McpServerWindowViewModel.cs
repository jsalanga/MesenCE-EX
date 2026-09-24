using CommunityToolkit.Mvvm.ComponentModel;
using Mesen.Config;
using Mesen.Localization;
using Mesen.ViewModels;
using System;

namespace Mesen.Mcp
{
	public partial class McpServerWindowViewModel : ViewModelBase
	{
		[ObservableProperty] public partial string Status { get; set; } = "";
		[ObservableProperty] public partial bool IsListening { get; set; }
		[ObservableProperty] public partial string Url { get; set; } = "";
		[ObservableProperty] public partial string ClaudeCodeCommand { get; set; } = "";
		[ObservableProperty] public partial string WriteAccess { get; set; } = "";
		[ObservableProperty] public partial string Activity { get; set; } = "";

		public McpServerWindowViewModel()
		{
			Refresh();
		}

		public void Refresh()
		{
			McpServer server = McpServer.Instance;
			IntegrationConfig cfg = ConfigManager.Config.Debug.Integration;

			IsListening = server.State == McpServerState.Listening;
			Status = server.State switch {
				McpServerState.Listening => ResourceHelper.GetMessage("McpServerListening"),
				McpServerState.Error => ResourceHelper.GetMessage("McpServerError", server.LastError ?? ""),
				_ => ResourceHelper.GetMessage(cfg.McpServerEnabled ? "McpServerStarting" : "McpServerDisabled")
			};

			Url = server.Url;
			ClaudeCodeCommand = "claude mcp add --transport http mesen " + server.Url;
			WriteAccess = ResourceHelper.GetMessage(cfg.McpAllowWriteAccess ? "McpWriteAccessEnabled" : "McpWriteAccessDisabled");

			string lastRequest = server.LastRequestTime?.ToString("T") ?? "-";
			Activity = ResourceHelper.GetMessage("McpServerActivity", server.ActiveClientCount, server.RequestCount, lastRequest);
		}
	}
}
