using Mesen.Debugger.Labels;
using Mesen.Interop;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Mesen.Mcp
{
	public static class McpHelpers
	{
		public static string Hex(long value, int digits)
		{
			return "$" + value.ToString("X" + digits);
		}

		public static string HexBytes(byte[] data, int start = 0, int length = -1)
		{
			if(length < 0) {
				length = data.Length - start;
			}
			StringBuilder sb = new StringBuilder(length * 3);
			for(int i = 0; i < length; i++) {
				if(i > 0) {
					sb.Append(' ');
				}
				sb.Append(data[start + i].ToString("X2"));
			}
			return sb.ToString();
		}

		public static string FormatCpuAddress(CpuType cpuType, long address)
		{
			return Hex(address, cpuType.GetAddressSize());
		}

		public static string FormatAddress(MemoryType memType, long address)
		{
			string format = memType.GetFormatString();
			return "$" + address.ToString(format);
		}

		public static CpuType GetMainCpu()
		{
			return EmuApi.GetRomInfo().ConsoleType.GetMainCpuType();
		}

		public static CpuType ResolveCpu(McpArgs args, string argName = "cpu")
		{
			RomInfo romInfo = EmuApi.GetRomInfo();
			CpuType? cpu = args.GetEnum<CpuType>(argName);
			if(cpu == null) {
				return romInfo.ConsoleType.GetMainCpuType();
			}
			if(!romInfo.CpuTypes.Contains(cpu.Value)) {
				throw new McpToolException($"CPU '{cpu.Value}' is not present in the loaded ROM. Available CPUs: {string.Join(", ", romInfo.CpuTypes)}.");
			}
			return cpu.Value;
		}

		public static List<MemoryType> GetAvailableMemoryTypes()
		{
			List<MemoryType> types = new();
			foreach(MemoryType type in Enum.GetValues<MemoryType>()) {
				if(type != MemoryType.None && DebugApi.GetMemorySize(type) > 0) {
					types.Add(type);
				}
			}
			return types;
		}

		public static MemoryType ResolveMemoryType(McpArgs args, string argName, MemoryType? defaultType)
		{
			MemoryType? memType = args.GetEnum<MemoryType>(argName) ?? defaultType;
			if(memType == null) {
				throw new McpToolException($"Missing required argument '{argName}'. Available memory types: {string.Join(", ", GetAvailableMemoryTypes())}.");
			}
			if(memType.Value == MemoryType.None || DebugApi.GetMemorySize(memType.Value) <= 0) {
				throw new McpToolException($"Memory type '{memType.Value}' is not available for the loaded ROM. Available memory types: {string.Join(", ", GetAvailableMemoryTypes())}.");
			}
			return memType.Value;
		}

		public static void ValidateRange(MemoryType memType, UInt32 address, UInt32 length)
		{
			int size = DebugApi.GetMemorySize(memType);
			if(address >= size) {
				throw new McpToolException($"Address {FormatAddress(memType, address)} is out of range for {memType} (size: {FormatAddress(memType, size)} bytes).");
			}
		}

		/// <summary>Returns the label name (or null) for a CPU-relative or absolute address</summary>
		public static CodeLabel? GetLabel(MemoryType memType, long address)
		{
			if(address < 0 || address > Int32.MaxValue) {
				return null;
			}
			return LabelManager.GetLabel(new AddressInfo() { Address = (int)address, Type = memType });
		}

		public static void AddLabelInfo(JsonObject obj, MemoryType memType, long address, string key = "label")
		{
			CodeLabel? label = GetLabel(memType, address);
			if(label != null && label.Label.Length > 0) {
				AddressInfo relAddr = new AddressInfo() { Address = (int)address, Type = memType };
				AddressInfo absAddr = memType.IsRelativeMemory() ? DebugApi.GetAbsoluteAddress(relAddr) : relAddr;
				int offset = absAddr.Type == label.MemoryType ? absAddr.Address - (int)label.Address : 0;
				obj[key] = offset > 0 ? label.Label + "+" + offset : label.Label;
			}
		}

		public static JsonObject AddressInfoToJson(AddressInfo addr)
		{
			return new JsonObject() {
				["memory_type"] = addr.Type.ToString(),
				["address"] = FormatAddress(addr.Type, addr.Address)
			};
		}

		public static JsonArray ToJsonArray(IEnumerable<string> values)
		{
			JsonArray array = new JsonArray();
			foreach(string value in values) {
				array.Add((JsonNode)value);
			}
			return array;
		}

		public static JsonArray ToJsonArray(IEnumerable<JsonNode> values)
		{
			JsonArray array = new JsonArray();
			foreach(JsonNode value in values) {
				array.Add(value);
			}
			return array;
		}

		private static UInt32[]? _crcTable;

		public static UInt32 Crc32(byte[] data)
		{
			if(_crcTable == null) {
				UInt32[] table = new UInt32[256];
				for(UInt32 i = 0; i < 256; i++) {
					UInt32 c = i;
					for(int j = 0; j < 8; j++) {
						c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1);
					}
					table[i] = c;
				}
				_crcTable = table;
			}

			UInt32 crc = 0xFFFFFFFF;
			foreach(byte b in data) {
				crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
			}
			return ~crc;
		}

		public static JsonObject GetCpuState(CpuType cpuType)
		{
			return cpuType switch {
				CpuType.Snes or CpuType.Sa1 => GetSnesCpuState(cpuType),
				CpuType.Spc => GetSpcState(),
				CpuType.NecDsp => StructToJson(DebugApi.GetCpuState<NecDspState>(cpuType)),
				CpuType.Gsu => StructToJson(DebugApi.GetCpuState<GsuState>(cpuType)),
				CpuType.Cx4 => StructToJson(DebugApi.GetCpuState<Cx4State>(cpuType)),
				CpuType.St018 => StructToJson(DebugApi.GetCpuState<ArmV3CpuState>(cpuType)),
				CpuType.Gameboy => StructToJson(DebugApi.GetCpuState<GbCpuState>(cpuType)),
				CpuType.Nes => StructToJson(DebugApi.GetCpuState<NesCpuState>(cpuType)),
				CpuType.Pce => StructToJson(DebugApi.GetCpuState<PceCpuState>(cpuType)),
				CpuType.Sms => StructToJson(DebugApi.GetCpuState<SmsCpuState>(cpuType)),
				CpuType.Gba => StructToJson(DebugApi.GetCpuState<GbaCpuState>(cpuType)),
				CpuType.Ws => StructToJson(DebugApi.GetCpuState<WsCpuState>(cpuType)),
				_ => throw new McpToolException("Unsupported CPU type: " + cpuType)
			};
		}

		private static JsonObject GetSnesCpuState(CpuType cpuType)
		{
			SnesCpuState s = DebugApi.GetCpuState<SnesCpuState>(cpuType);
			bool m8 = s.PS.HasFlag(SnesCpuFlags.MemoryMode8) || s.EmulationMode;
			bool x8 = s.PS.HasFlag(SnesCpuFlags.IndexMode8) || s.EmulationMode;
			return new JsonObject() {
				["pc"] = Hex((s.K << 16) | s.PC, 6),
				["a"] = Hex(s.A, 4),
				["x"] = Hex(s.X, 4),
				["y"] = Hex(s.Y, 4),
				["sp"] = Hex(s.SP, 4),
				["d"] = Hex(s.D, 4),
				["db"] = Hex(s.DBR, 2),
				["pb"] = Hex(s.K, 2),
				["p"] = Hex((byte)s.PS, 2),
				["flags"] = new JsonObject() {
					["n"] = s.PS.HasFlag(SnesCpuFlags.Negative),
					["v"] = s.PS.HasFlag(SnesCpuFlags.Overflow),
					["m"] = s.PS.HasFlag(SnesCpuFlags.MemoryMode8),
					["x"] = s.PS.HasFlag(SnesCpuFlags.IndexMode8),
					["d"] = s.PS.HasFlag(SnesCpuFlags.Decimal),
					["i"] = s.PS.HasFlag(SnesCpuFlags.IrqDisable),
					["z"] = s.PS.HasFlag(SnesCpuFlags.Zero),
					["c"] = s.PS.HasFlag(SnesCpuFlags.Carry),
					["e"] = s.EmulationMode
				},
				["accumulator_8bit"] = m8,
				["index_8bit"] = x8,
				["cycle_count"] = s.CycleCount,
				["stop_state"] = s.StopState.ToString(),
				["irq_lock"] = s.IrqLock,
				["need_nmi"] = s.NeedNmi
			};
		}

		private static JsonObject GetSpcState()
		{
			SpcState s = DebugApi.GetCpuState<SpcState>(CpuType.Spc);
			return new JsonObject() {
				["pc"] = Hex(s.PC, 4),
				["a"] = Hex(s.A, 2),
				["x"] = Hex(s.X, 2),
				["y"] = Hex(s.Y, 2),
				["sp"] = Hex(s.SP, 2),
				["psw"] = Hex((byte)s.PS, 2),
				["flags"] = new JsonObject() {
					["n"] = s.PS.HasFlag(SpcFlags.Negative),
					["v"] = s.PS.HasFlag(SpcFlags.Overflow),
					["p"] = s.PS.HasFlag(SpcFlags.DirectPage),
					["b"] = s.PS.HasFlag(SpcFlags.Break),
					["h"] = s.PS.HasFlag(SpcFlags.HalfCarry),
					["i"] = s.PS.HasFlag(SpcFlags.IrqEnable),
					["z"] = s.PS.HasFlag(SpcFlags.Zero),
					["c"] = s.PS.HasFlag(SpcFlags.Carry)
				},
				["cycle_count"] = s.Cycle,
				["stop_state"] = s.StopState.ToString(),
				["cpu_regs"] = HexBytes(s.CpuRegs),
				["output_regs"] = HexBytes(s.OutputReg)
			};
		}

		/// <summary>
		/// Generic conversion of a CPU state struct to JSON (used for CPUs without a dedicated formatter).
		/// Registers (byte/UInt16/UInt32) are shown as hex strings, counters (UInt64/signed) as numbers.
		/// </summary>
		public static JsonObject StructToJson<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] T>(T state) where T : struct
		{
			return StructToJson(state, typeof(T), 0);
		}

		[UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "State structs are in the Mesen assembly, which is rooted (TrimmerRootAssembly)")]
		public static JsonObject BoxedStructToJson(BaseState state)
		{
			return StructToJson(state, state.GetType(), 0);
		}

		[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "State structs are in the Mesen assembly, which is rooted (TrimmerRootAssembly)")]
		[UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "State structs are in the Mesen assembly, which is rooted (TrimmerRootAssembly)")]
		private static JsonObject StructToJson(object state, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type type, int depth)
		{
			JsonObject result = new JsonObject();
			foreach(FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
				JsonNode? value = ValueToJson(field.GetValue(state), field.FieldType, depth);
				if(value != null) {
					result[ToSnakeCase(field.Name)] = value;
				}
			}
			return result;
		}

		[UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "State structs are in the Mesen assembly, which is rooted (TrimmerRootAssembly)")]
		private static JsonNode? ValueToJson(object? value, Type type, int depth)
		{
			switch(value) {
				case null: return null;
				case bool b: return b;
				case Enum e: return e.ToString();
				case byte b: return Hex(b, 2);
				case UInt16 u16: return Hex(u16, 4);
				case UInt32 u32: return Hex(u32, 8);
				case UInt64 u64: return u64;
				case sbyte i8: return i8;
				case Int16 i16: return i16;
				case Int32 i32: return i32;
				case Int64 i64: return i64;
				case double d: return d;
				case float f: return f;
				case byte[] bytes: return HexBytes(bytes);
				case Array array: {
					if(depth > 2) {
						return null;
					}
					JsonArray result = new JsonArray();
					Type elementType = type.GetElementType() ?? typeof(object);
					foreach(object? item in (IEnumerable)array) {
						result.Add(ValueToJson(item, item?.GetType() ?? elementType, depth + 1));
					}
					return result;
				}
				default:
					if(type.IsValueType && depth < 2) {
						return StructToJson(value, value.GetType(), depth + 1);
					}
					return null;
			}
		}

		private static string ToSnakeCase(string name)
		{
			StringBuilder sb = new StringBuilder(name.Length + 8);
			for(int i = 0; i < name.Length; i++) {
				char c = name[i];
				if(char.IsUpper(c)) {
					if(i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])))) {
						sb.Append('_');
					}
					sb.Append(char.ToLowerInvariant(c));
				} else {
					sb.Append(c);
				}
			}
			return sb.ToString();
		}
	}
}
