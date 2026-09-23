namespace Mesen.Mcp
{
	public static class McpInstructions
	{
		public const string Text =
			"This server controls the Mesen emulator's debugger (SNES, NES, Game Boy/GBC, GBA, PC Engine, SMS/Game Gear, WonderSwan). " +
			"Start with get_status to see the loaded ROM, its CPUs and memory types. " +
			"Addresses: JSON numbers are decimal; strings are always hex (\"$8000\", \"0x8000\", \"8000\", or bank:address \"$80:8000\"). " +
			"Memory types use Mesen's names: CPU address spaces (SnesMemory, SpcMemory, Sa1Memory, NesMemory, ...) are what the CPU sees; " +
			"physical regions (SnesPrgRom, SnesWorkRam, SnesSaveRam, SnesVideoRam, SnesCgRam, SnesSpriteRam, SpcRam, NesPrgRom, ...) are offsets into that memory. " +
			"Labels and comments are best attached to physical addresses (e.g. SnesPrgRom offsets for code, SnesWorkRam for variables) so they apply regardless of mapping. " +
			"Typical exploration loop: pause, disassemble around the PC, add_breakpoint, run_until (returns why execution stopped and the CPU state), " +
			"set_label/set_comment to record findings, and get_cdl/get_cdl_stats to see which bytes are known code or data. " +
			"Use set_input to press controller buttons for a number of frames to reach new code. " +
			"Labels, comments and breakpoints are saved in Mesen's debugger workspace, exactly as if edited in the debugger UI. " +
			"Large results are capped; when a result is truncated it says so.";
	}
}
