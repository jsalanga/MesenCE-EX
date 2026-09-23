using Mesen.Config;
using Mesen.Debugger.Labels;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Mesen.Mcp.Tools
{
	public static class McpStatusTools
	{
		private static string _crcCacheKey = "";
		private static UInt32 _crcCacheValue;

		public static void Register(McpToolRegistry registry)
		{
			registry.Add(new McpTool(
				"get_status",
				"Returns the emulator status: loaded ROM (name, path, console, SHA1, PRG ROM CRC32, size), " +
				"decoded cartridge header (SNES: title, map mode, chipset, ROM/SRAM size, region, checksum, interrupt vectors; NES: iNES header), " +
				"CPUs present with their current PC, whether execution is paused (and why it last stopped), frame count, " +
				"every available memory type with its size in bytes, and whether write access is enabled. Call this first.",
				McpSchema.Create().Build(),
				GetStatus
			) { ReadOnly = true, RequiresRom = false });
		}

		private static async Task<JsonObject> GetStatus(McpArgs args, System.Threading.CancellationToken ct)
		{
			JsonObject result = new JsonObject() {
				["write_access_enabled"] = ConfigManager.Config.Debug.Integration.McpAllowWriteAccess
			};

			if(McpDebugSession.Instance.IsLoading) {
				result["rom_loaded"] = false;
				result["loading"] = true;
				return result;
			}

			if(!EmuApi.IsRunning()) {
				result["rom_loaded"] = false;
				return result;
			}

			await McpDebugSession.Instance.EnsureReadyAsync();

			RomInfo romInfo = EmuApi.GetRomInfo();
			CpuType mainCpu = romInfo.ConsoleType.GetMainCpuType();
			MemoryType prgType = mainCpu.GetPrgRomMemoryType();

			string sha1 = EmuApi.GetRomHash(HashType.Sha1);
			JsonObject rom = new JsonObject() {
				["name"] = romInfo.GetRomName(),
				["path"] = romInfo.RomPath,
				["format"] = romInfo.Format.ToString(),
				["console"] = romInfo.ConsoleType.ToString(),
				["sha1"] = sha1
			};
			if(!string.IsNullOrEmpty(romInfo.PatchPath)) {
				rom["patch_path"] = romInfo.PatchPath;
			}

			int prgSize = DebugApi.GetMemorySize(prgType);
			if(prgSize > 0) {
				rom["prg_rom_memory_type"] = prgType.ToString();
				rom["prg_rom_size"] = prgSize;
				rom["prg_rom_crc32"] = GetPrgCrc32(prgType, sha1).ToString("X8");
			}
			result["rom_loaded"] = true;
			result["rom"] = rom;

			if(romInfo.ConsoleType == ConsoleType.Snes && romInfo.Format == RomFormat.Sfc) {
				result["snes_header"] = GetSnesHeader();
			} else if(romInfo.ConsoleType == ConsoleType.Nes) {
				JsonObject? nesHeader = GetNesHeader();
				if(nesHeader != null) {
					result["nes_header"] = nesHeader;
				}
			}

			JsonArray cpus = new JsonArray();
			foreach(CpuType cpuType in romInfo.CpuTypes.OrderBy(c => c)) {
				cpus.Add((JsonNode)new JsonObject() {
					["cpu"] = cpuType.ToString(),
					["pc"] = McpHelpers.FormatCpuAddress(cpuType, DebugApi.GetProgramCounter(cpuType, true)),
					["memory_type"] = cpuType.ToMemoryType().ToString(),
					["is_main_cpu"] = cpuType == mainCpu
				});
			}
			result["main_cpu"] = mainCpu.ToString();
			result["cpus"] = cpus;

			bool paused = EmuApi.IsPaused();
			JsonObject execution = new JsonObject() {
				["paused"] = paused
			};
			BreakEvent? lastBreak = McpDebugSession.Instance.LastBreak;
			if(paused && lastBreak != null) {
				execution["last_break_source"] = lastBreak.Value.Source.ToString();
				execution["last_break_cpu"] = lastBreak.Value.SourceCpu.ToString();
			}
			TimingInfo timing = EmuApi.GetTimingInfo(mainCpu);
			execution["frame_count"] = timing.FrameCount;
			execution["fps"] = Math.Round(timing.Fps, 3);
			result["execution"] = execution;

			JsonArray memTypes = new JsonArray();
			foreach(MemoryType memType in McpHelpers.GetAvailableMemoryTypes()) {
				memTypes.Add((JsonNode)new JsonObject() {
					["name"] = memType.ToString(),
					["size"] = DebugApi.GetMemorySize(memType),
					["cpu_address_space"] = memType.IsRelativeMemory()
				});
			}
			result["memory_types"] = memTypes;
			result["label_count"] = LabelManager.GetAllLabels().Count;

			return result;
		}

		private static UInt32 GetPrgCrc32(MemoryType prgType, string sha1)
		{
			string key = prgType.ToString() + sha1;
			if(_crcCacheKey != key) {
				_crcCacheValue = McpHelpers.Crc32(DebugApi.GetMemoryState(prgType));
				_crcCacheKey = key;
			}
			return _crcCacheValue;
		}

		private static JsonObject GetSnesHeader()
		{
			//Read the header through the CPU's address space, which reflects the mapping selected by the emulator
			byte[] h = DebugApi.GetMemoryValues(MemoryType.SnesMemory, 0xFFB0, 0xFFFF);

			string title = new string(h.Skip(0x10).Take(21).Select(b => b >= 0x20 && b < 0x7F ? (char)b : ' ').ToArray()).TrimEnd();
			byte mapMode = h[0x25];
			byte romType = h[0x26];
			int checksumComplement = h[0x2C] | (h[0x2D] << 8);
			int checksum = h[0x2E] | (h[0x2F] << 8);

			AddressInfo headerAddr = DebugApi.GetAbsoluteAddress(new AddressInfo() { Address = 0xFFC0, Type = MemoryType.SnesMemory });

			JsonObject header = new JsonObject() {
				["title"] = title,
				["map_mode"] = McpHelpers.Hex(mapMode, 2),
				["mapping"] = GetSnesMapping(mapMode),
				["speed"] = (mapMode & 0x10) != 0 ? "FastROM" : "SlowROM",
				["rom_type"] = McpHelpers.Hex(romType, 2),
				["chipset"] = GetSnesChipset(romType),
				["rom_size_kb"] = h[0x27] < 16 ? (1 << h[0x27]) : 0,
				["sram_size_kb"] = h[0x28] > 0 && h[0x28] < 16 ? (1 << h[0x28]) : 0,
				["region"] = McpHelpers.Hex(h[0x29], 2),
				["developer_id"] = McpHelpers.Hex(h[0x2A], 2),
				["version"] = h[0x2B],
				["checksum"] = McpHelpers.Hex(checksum, 4),
				["checksum_complement"] = McpHelpers.Hex(checksumComplement, 4),
				["checksum_pair_valid"] = (checksum ^ checksumComplement) == 0xFFFF
			};

			if(headerAddr.Address >= 0) {
				header["header_location"] = McpHelpers.AddressInfoToJson(headerAddr);
			}

			if(h[0x2A] == 0x33) {
				//Extended header
				header["maker_code"] = Encoding.ASCII.GetString(h, 0x00, 2);
				header["game_code"] = Encoding.ASCII.GetString(h, 0x02, 4).TrimEnd();
			}

			Func<int, string> vector = (int offset) => McpHelpers.Hex(h[offset] | (h[offset + 1] << 8), 4);
			header["native_vectors"] = new JsonObject() {
				["cop"] = vector(0x34),
				["brk"] = vector(0x36),
				["abort"] = vector(0x38),
				["nmi"] = vector(0x3A),
				["irq"] = vector(0x3E)
			};
			header["emulation_vectors"] = new JsonObject() {
				["cop"] = vector(0x44),
				["abort"] = vector(0x48),
				["nmi"] = vector(0x4A),
				["reset"] = vector(0x4C),
				["irq_brk"] = vector(0x4E)
			};
			header["vectors_note"] = "Vectors are 16-bit addresses in bank $00.";

			return header;
		}

		private static string GetSnesMapping(byte mapMode)
		{
			return (mapMode & 0xEF) switch {
				0x20 => "LoROM",
				0x21 => "HiROM",
				0x22 => "LoROM (S-DD1/ExLoROM)",
				0x23 => "SA-1",
				0x25 => "ExHiROM",
				0x2A => "SPC7110",
				_ => "Unknown"
			};
		}

		private static string GetSnesChipset(byte romType)
		{
			string baseType = (romType & 0x0F) switch {
				0x00 => "ROM",
				0x01 => "ROM+RAM",
				0x02 => "ROM+RAM+Battery",
				0x03 => "ROM+Coprocessor",
				0x04 => "ROM+Coprocessor+RAM",
				0x05 => "ROM+Coprocessor+RAM+Battery",
				0x06 => "ROM+Coprocessor+Battery",
				_ => "Unknown"
			};

			if((romType & 0x0F) >= 0x03) {
				string coprocessor = (romType >> 4) switch {
					0x0 => "DSP",
					0x1 => "SuperFX (GSU)",
					0x2 => "OBC1",
					0x3 => "SA-1",
					0x4 => "S-DD1",
					0x5 => "S-RTC",
					0xE => "Other (Super Game Boy/Satellaview)",
					0xF => "Custom (SPC7110/ST010/ST011/ST018/Cx4)",
					_ => "Unknown"
				};
				return baseType.Replace("Coprocessor", coprocessor);
			}
			return baseType;
		}

		private static JsonObject? GetNesHeader()
		{
			byte[] h = DebugApi.GetRomHeader();
			if(h.Length < 16 || h[0] != 'N' || h[1] != 'E' || h[2] != 'S' || h[3] != 0x1A) {
				return null;
			}

			bool nes2 = (h[7] & 0x0C) == 0x08;
			int mapper = (h[6] >> 4) | (h[7] & 0xF0) | (nes2 ? ((h[8] & 0x0F) << 8) : 0);
			return new JsonObject() {
				["raw"] = McpHelpers.HexBytes(h, 0, 16),
				["format"] = nes2 ? "NES 2.0" : "iNES",
				["mapper"] = mapper,
				["submapper"] = nes2 ? (h[8] >> 4) : 0,
				["prg_rom_16kb_banks"] = h[4] | (nes2 ? ((h[9] & 0x0F) << 8) : 0),
				["chr_rom_8kb_banks"] = h[5] | (nes2 ? ((h[9] & 0xF0) << 4) : 0),
				["mirroring"] = (h[6] & 0x08) != 0 ? "FourScreen" : ((h[6] & 0x01) != 0 ? "Vertical" : "Horizontal"),
				["battery"] = (h[6] & 0x02) != 0,
				["trainer"] = (h[6] & 0x04) != 0
			};
		}
	}
}
