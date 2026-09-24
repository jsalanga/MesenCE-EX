using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Mesen.Debugger.ViewModels;
using Mesen.Debugger.Windows;
using Mesen.Utilities;
using System;

namespace Mesen.Mcp
{
	public class McpServerWindow : MesenWindow
	{
		private McpServerWindowViewModel _model;
		private DispatcherTimer _timer;

		public McpServerWindow()
		{
			InitializeComponent();
			_model = new McpServerWindowViewModel();
			DataContext = _model;
			_timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (s, e) => _model.Refresh());
		}

		private void InitializeComponent()
		{
			AvaloniaXamlLoader.Load(this);
		}

		protected override void OnOpened(EventArgs e)
		{
			base.OnOpened(e);
			if(Design.IsDesignMode) {
				return;
			}
			McpServer.Instance.StatusChanged += Server_StatusChanged;
			_timer.Start();
		}

		protected override void OnClosing(WindowClosingEventArgs e)
		{
			base.OnClosing(e);
			if(Design.IsDesignMode) {
				return;
			}
			_timer.Stop();
			McpServer.Instance.StatusChanged -= Server_StatusChanged;
		}

		private void Server_StatusChanged(object? sender, EventArgs e)
		{
			Dispatcher.UIThread.Post(() => _model.Refresh());
		}

		private void CopyUrl_OnClick(object? sender, RoutedEventArgs e)
		{
			Clipboard?.SetTextAsync(_model.Url);
		}

		private void CopyCommand_OnClick(object? sender, RoutedEventArgs e)
		{
			Clipboard?.SetTextAsync(_model.ClaudeCodeCommand);
		}

		private void Settings_OnClick(object? sender, RoutedEventArgs e)
		{
			DebuggerConfigWindow.Open(DebugConfigWindowTab.Integration, this);
		}

		private void Close_OnClick(object? sender, RoutedEventArgs e)
		{
			Close();
		}
	}
}
