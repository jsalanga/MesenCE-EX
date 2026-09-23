using Avalonia.Threading;
using Mesen.Debugger.Labels;
using Mesen.Debugger.Utilities;
using Mesen.Interop;
using Mesen.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mesen.Mcp.Tools
{
	public static class McpLabelTools
	{
		public const int MaxLabels = 2000;
		private static readonly string[] ImportExtensions = { "mlb", "dbg", "sym", "cdb", "elf", "fns" };

		public static void Register(McpToolRegistry registry)
		{
			const string addressingNote = "The address can be given in a CPU address space (e.g. SnesMemory $80:8000): it's converted to the underlying " +
				"physical location (e.g. SnesPrgRom offset $0000 or SnesWorkRam offset) so the label applies wherever that memory is mapped. ";

			registry.Add(new McpTool(
				"get_labels",
				$"Lists labels and comments (sorted by memory type and address), optionally filtered. Each entry has the memory type, address, " +
				$"end address (for multi-byte labels), label, comment and, for code/ROM labels, the main CPU address where it's currently mapped. Paged: max {MaxLabels} per call.",
				McpSchema.Create()
					.String("memory_type", "Only return labels in this memory type (e.g. SnesPrgRom, SnesWorkRam, SnesRegister).")
					.Address("start", "Only return labels at or after this address (in memory_type).")
					.Address("end", "Only return labels at or before this address (in memory_type).")
					.String("search", "Only return labels whose name or comment contains this text (case-insensitive).")
					.Integer("offset", "Index of the first label to return (default 0).", false, 0)
					.Integer("limit", $"Maximum number of labels (1-{MaxLabels}, default 200).", false, 1, MaxLabels)
					.Build(),
				GetLabels
			) { ReadOnly = true });

			registry.Add(new McpTool(
				"set_label",
				"Creates or updates a label (and optional comment) at an address, exactly like the debugger's label editor, and saves it in Mesen's workspace. " +
				addressingNote +
				"Label names must match [@_a-zA-Z][@_a-zA-Z0-9]* and be unique. If a label already starts at this address it's replaced " +
				"(omitted label/comment arguments keep their current value); a label overlapping the range at another address is an error. " +
				"Use length > 1 for multi-byte data (tables, variables); references inside it show as label+offset.",
				McpSchema.Create()
					.Address("address", "Address of the label.", true)
					.String("memory_type", "Memory type of the address (default: the main CPU's address space, e.g. SnesMemory).")
					.String("label", "Label name (empty string to only have a comment).")
					.String("comment", "Comment (use \\n for multiple lines). Omit to keep the existing comment.")
					.Integer("length", "Number of bytes covered by the label (default 1, max 65536).", false, 1, LabelManager.MaxLength)
					.Build(),
				SetLabel
			));

			registry.Add(new McpTool(
				"set_comment",
				"Sets (or clears, with an empty string) the comment at an address, keeping any label there. Comments are shown in the disassembly. " +
				"A comment line containing assert(condition) creates an assert breakpoint, as in the debugger. " + addressingNote,
				McpSchema.Create()
					.Address("address", "Address of the comment.", true)
					.String("memory_type", "Memory type of the address (default: the main CPU's address space).")
					.String("comment", "Comment text (use \\n for multiple lines, empty string to remove).", true)
					.Build(),
				SetComment
			));

			registry.Add(new McpTool(
				"delete_label",
				"Deletes the label (and its comment) by name, or the label at an address.",
				McpSchema.Create()
					.String("label", "Name of the label to delete.")
					.Address("address", "Address of the label to delete (alternative to 'label').")
					.String("memory_type", "Memory type of the address (default: the main CPU's address space).")
					.Build(),
				DeleteLabel
			));

			registry.Add(new McpTool(
				"import_labels",
				"Imports labels/symbols from a file on disk, using the same importers as the debugger's File > Import menu: " +
				".mlb (Mesen labels), .dbg (ca65), .sym (WLA-DX, RGBDS, bass, PCEAS), .cdb (SDCC), .elf, .fns (NESASM). " +
				"By default this follows the user's 'Reset labels on import' setting (enabled by default: existing labels are replaced). " +
				"For .mlb files, merge=true keeps the existing labels and adds/overwrites the imported ones.",
				McpSchema.Create()
					.String("path", "Absolute path of the file to import.", true)
					.Boolean("merge", "Only for .mlb files: keep existing labels (default false: follow the user's setting).")
					.Build(),
				ImportLabels
			));

			registry.Add(new McpTool(
				"export_labels",
				"Exports all labels and comments to a Mesen label file (.mlb), the same format as the debugger's File > Export labels. " +
				"Format: one label per line, MemoryType:Address[-EndAddress]:Label[:Comment] (hex addresses). Refuses to overwrite an existing file unless overwrite=true.",
				McpSchema.Create()
					.String("path", "Absolute path of the .mlb file to write.", true)
					.Boolean("overwrite", "Overwrite the file if it already exists (default false).")
					.Build(),
				ExportLabels
			));
		}

		/// <summary>Converts an address to the location labels are stored at (physical memory when possible)</summary>
		private static AddressInfo ResolveLabelAddress(McpArgs args)
		{
			UInt32 address = args.GetRequiredAddress("address");
			MemoryType memType = McpHelpers.ResolveMemoryType(args, "memory_type", McpHelpers.GetMainCpu().ToMemoryType());
			AddressInfo addr = new AddressInfo() { Address = (int)address, Type = memType };

			if(memType.IsRelativeMemory()) {
				AddressInfo absAddr = DebugApi.GetAbsoluteAddress(addr);
				if(absAddr.Address >= 0 && absAddr.Type.SupportsLabels()) {
					return absAddr;
				}
			}

			if(!memType.SupportsLabels()) {
				throw new McpToolException($"Labels aren't supported on {memType}.");
			}
			McpHelpers.ValidateRange(memType, address, 1);
			return addr;
		}

		private static JsonObject ToJson(CodeLabel label, CpuType mainCpu)
		{
			JsonObject result = new JsonObject() {
				["memory_type"] = label.MemoryType.ToString(),
				["address"] = McpHelpers.FormatAddress(label.MemoryType, label.Address)
			};
			if(label.Length > 1) {
				result["end"] = McpHelpers.FormatAddress(label.MemoryType, label.Address + label.Length - 1);
			}
			if(label.Label.Length > 0) {
				result["label"] = label.Label;
			}
			if(label.Comment.Length > 0) {
				result["comment"] = label.Comment;
			}
			if(!label.MemoryType.IsRelativeMemory() && mainCpu.CanAccessMemoryType(label.MemoryType)) {
				AddressInfo rel = label.GetRelativeAddress(mainCpu);
				if(rel.Address >= 0) {
					result["cpu_address"] = McpHelpers.FormatCpuAddress(mainCpu, rel.Address);
				}
			}
			return result;
		}

		private static async Task<JsonObject> GetLabels(McpArgs args, CancellationToken ct)
		{
			MemoryType? memType = args.Has("memory_type") ? McpHelpers.ResolveMemoryType(args, "memory_type", null) : null;
			UInt32 start = args.GetAddress("start") ?? 0;
			UInt32 end = args.GetAddress("end") ?? UInt32.MaxValue;
			string? search = args.GetString("search");
			int offset = (int)args.GetInt("offset", 0, 0, int.MaxValue);
			int limit = (int)args.GetInt("limit", 200, 1, MaxLabels);
			CpuType mainCpu = McpHelpers.GetMainCpu();

			List<CodeLabel> labels = await Dispatcher.UIThread.InvokeAsync(() => LabelManager.GetAllLabels());
			IEnumerable<CodeLabel> filtered = labels
				.Where(l => memType == null || l.MemoryType == memType)
				.Where(l => l.Address + l.Length - 1 >= start && l.Address <= end)
				.Where(l => search == null || l.Label.Contains(search, StringComparison.OrdinalIgnoreCase) || l.Comment.Contains(search, StringComparison.OrdinalIgnoreCase))
				.OrderBy(l => l.MemoryType).ThenBy(l => l.Address);

			List<CodeLabel> matching = filtered.ToList();
			JsonArray result = new JsonArray();
			foreach(CodeLabel label in matching.Skip(offset).Take(limit)) {
				result.Add((JsonNode)ToJson(label, mainCpu));
			}

			JsonObject response = new JsonObject() {
				["total"] = matching.Count,
				["offset"] = offset,
				["labels"] = result
			};
			if(offset + result.Count < matching.Count) {
				response["truncated"] = true;
				response["next_offset"] = offset + result.Count;
			}
			return response;
		}

		private static async Task<JsonObject> SetLabel(McpArgs args, CancellationToken ct)
		{
			AddressInfo addr = ResolveLabelAddress(args);
			string? name = args.GetString("label")?.Trim();
			string? comment = args.GetString("comment");
			UInt32 length = (UInt32)args.GetInt("length", 1, 1, LabelManager.MaxLength);
			return await ApplyLabel(addr, name, comment, length, args.Has("length"));
		}

		private static async Task<JsonObject> SetComment(McpArgs args, CancellationToken ct)
		{
			AddressInfo addr = ResolveLabelAddress(args);
			string comment = args.GetRequiredString("comment");
			return await ApplyLabel(addr, null, comment, 1, false);
		}

		private static async Task<JsonObject> ApplyLabel(AddressInfo addr, string? name, string? comment, UInt32 length, bool lengthSpecified)
		{
			if(name != null && name.Length > 0 && !LabelManager.LabelRegex.IsMatch(name)) {
				throw new McpToolException($"Invalid label name '{name}'. Names must start with a letter, '_' or '@', followed by letters, digits, '_' or '@'.");
			}
			if(comment != null) {
				comment = comment.Replace("\r\n", "\n").Replace("\\n", "\n");
				if(comment.Contains('\x1')) {
					throw new McpToolException("Comments can't contain the \\x1 character.");
				}
			}

			CpuType mainCpu = McpHelpers.GetMainCpu();
			JsonObject result = await Dispatcher.UIThread.InvokeAsync(() => {
				CodeLabel? existing = LabelManager.GetLabel((UInt32)addr.Address, addr.Type);
				if(existing != null && existing.Address != addr.Address) {
					//The address is inside a multi-byte label that starts elsewhere
					throw new McpToolException($"The address is inside the label '{existing.Label}' ({McpHelpers.FormatAddress(existing.MemoryType, existing.Address)}-{McpHelpers.FormatAddress(existing.MemoryType, existing.Address + existing.Length - 1)}). Delete or resize it first.");
				}

				CodeLabel label = new CodeLabel() {
					Address = (UInt32)addr.Address,
					MemoryType = addr.Type,
					Label = name ?? existing?.Label ?? "",
					Comment = comment ?? existing?.Comment ?? "",
					Length = lengthSpecified || existing == null ? length : existing.Length,
					Flags = existing?.Flags ?? CodeLabelFlags.None
				};

				int maxAddress = DebugApi.GetMemorySize(label.MemoryType) - 1;
				if(label.Address + label.Length - 1 > maxAddress) {
					throw new McpToolException($"The label goes past the end of {label.MemoryType} (max address {McpHelpers.FormatAddress(label.MemoryType, maxAddress)}).");
				}

				for(UInt32 i = 1; i < label.Length; i++) {
					CodeLabel? other = LabelManager.GetLabel(label.Address + i, label.MemoryType);
					if(other != null && other != existing) {
						throw new McpToolException($"The range overlaps the existing label '{(other.Label.Length > 0 ? other.Label : other.Comment)}' at {McpHelpers.FormatAddress(other.MemoryType, other.Address)}.");
					}
				}

				if(label.Label.Length > 0) {
					CodeLabel? sameName = LabelManager.GetLabel(label.Label);
					if(sameName != null && sameName != existing) {
						throw new McpToolException($"The label name '{label.Label}' is already used at {sameName.MemoryType} {McpHelpers.FormatAddress(sameName.MemoryType, sameName.Address)}.");
					}
				}

				JsonObject response;
				if(label.Label.Length == 0 && label.Comment.Length == 0) {
					//Nothing left, remove the label
					if(existing != null) {
						LabelManager.DeleteLabel(existing, true);
					}
					response = new JsonObject() { ["deleted"] = existing != null };
				} else {
					if(existing != null) {
						LabelManager.DeleteLabel(existing, false);
					}
					LabelManager.SetLabel(label, true);
					response = ToJson(label, mainCpu);
					response["replaced_existing"] = existing != null;
				}

				DebugWorkspaceManager.AutoSave();
				return response;
			});

			McpDebugSession.Instance.ScheduleWorkspaceSave();
			return result;
		}

		private static async Task<JsonObject> DeleteLabel(McpArgs args, CancellationToken ct)
		{
			string? name = args.GetString("label");
			AddressInfo? addr = args.Has("address") ? ResolveLabelAddress(args) : null;
			if(name == null && addr == null) {
				throw new McpToolException("Either 'label' or 'address' is required.");
			}

			CpuType mainCpu = McpHelpers.GetMainCpu();
			JsonObject result = await Dispatcher.UIThread.InvokeAsync(() => {
				CodeLabel? label = name != null ? LabelManager.GetLabel(name) : LabelManager.GetLabel((UInt32)addr!.Value.Address, addr.Value.Type);
				if(label == null) {
					throw new McpToolException(name != null ? $"No label named '{name}'." : "No label at this address.");
				}
				JsonObject deleted = ToJson(label, mainCpu);
				LabelManager.DeleteLabel(label, true);
				DebugWorkspaceManager.AutoSave();
				return new JsonObject() { ["deleted"] = deleted };
			});

			McpDebugSession.Instance.ScheduleWorkspaceSave();
			return result;
		}

		private static async Task<JsonObject> ImportLabels(McpArgs args, CancellationToken ct)
		{
			string path = args.GetRequiredString("path");
			bool merge = args.GetBool("merge", false);
			if(!Path.IsPathFullyQualified(path)) {
				throw new McpToolException("'path' must be an absolute path.");
			}
			if(!File.Exists(path)) {
				throw new McpToolException($"File not found: {path}");
			}

			string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
			if(!ImportExtensions.Contains(ext)) {
				throw new McpToolException($"Unsupported file type '.{ext}'. Supported: {string.Join(", ", ImportExtensions.Select(e => "." + e))}.");
			}
			if(merge && ext != FileDialogHelper.MesenLabelExt) {
				throw new McpToolException("merge=true is only supported for .mlb files.");
			}

			JsonObject result = await Dispatcher.UIThread.InvokeAsync(() => {
				int before = LabelManager.GetAllLabels().Count;
				if(merge) {
					MesenLabelFile.Import(path, false);
				} else {
					DebugWorkspaceManager.LoadSupportedFile(path, false);
				}
				DebugWorkspaceManager.AutoSave();
				int after = LabelManager.GetAllLabels().Count;
				return new JsonObject() {
					["path"] = path,
					["labels_before"] = before,
					["labels_after"] = after
				};
			});

			McpDebugSession.Instance.ScheduleWorkspaceSave();
			return result;
		}

		private static async Task<JsonObject> ExportLabels(McpArgs args, CancellationToken ct)
		{
			string path = args.GetRequiredString("path");
			bool overwrite = args.GetBool("overwrite", false);
			if(!Path.IsPathFullyQualified(path)) {
				throw new McpToolException("'path' must be an absolute path.");
			}
			if(!path.EndsWith("." + FileDialogHelper.MesenLabelExt, StringComparison.OrdinalIgnoreCase)) {
				throw new McpToolException("The file name must end with .mlb.");
			}
			string? folder = Path.GetDirectoryName(path);
			if(folder == null || !Directory.Exists(folder)) {
				throw new McpToolException($"The folder doesn't exist: {folder}");
			}
			if(File.Exists(path) && !overwrite) {
				throw new McpToolException($"The file already exists: {path}. Set overwrite=true to replace it.");
			}

			int count = await Dispatcher.UIThread.InvokeAsync(() => {
				MesenLabelFile.Export(path);
				return LabelManager.GetAllLabels().Count;
			});

			return new JsonObject() {
				["path"] = path,
				["label_count"] = count
			};
		}
	}
}
