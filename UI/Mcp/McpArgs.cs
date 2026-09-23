using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mesen.Mcp
{
	/// <summary>Typed accessors for tool call arguments, producing readable errors for the model</summary>
	public class McpArgs
	{
		private JsonObject _args;

		public McpArgs(JsonObject? args)
		{
			_args = args ?? new JsonObject();
		}

		public bool Has(string name)
		{
			return _args.TryGetPropertyValue(name, out JsonNode? node) && node != null;
		}

		public string? GetString(string name)
		{
			if(!_args.TryGetPropertyValue(name, out JsonNode? node) || node == null) {
				return null;
			}
			if(node is JsonValue value && value.TryGetValue(out string? str)) {
				return str;
			}
			if(node is JsonValue numValue && numValue.GetValueKind() == JsonValueKind.Number) {
				return numValue.ToJsonString();
			}
			throw new McpToolException($"Argument '{name}' must be a string.");
		}

		public string GetRequiredString(string name)
		{
			string? value = GetString(name);
			if(value == null) {
				throw new McpToolException($"Missing required argument '{name}'.");
			}
			return value;
		}

		public long? GetInt(string name)
		{
			if(!_args.TryGetPropertyValue(name, out JsonNode? node) || node == null) {
				return null;
			}
			if(node is JsonValue value) {
				if(value.GetValueKind() == JsonValueKind.Number) {
					if(value.TryGetValue(out long l)) {
						return l;
					}
					if(value.TryGetValue(out double d) && d == Math.Floor(d)) {
						return (long)d;
					}
				} else if(value.TryGetValue(out string? str)) {
					if(TryParseNumber(str, out long parsed)) {
						return parsed;
					}
				}
			}
			throw new McpToolException($"Argument '{name}' must be an integer.");
		}

		public long GetInt(string name, long defaultValue, long min, long max)
		{
			long value = GetInt(name) ?? defaultValue;
			if(value < min || value > max) {
				throw new McpToolException($"Argument '{name}' must be between {min} and {max} (got {value}).");
			}
			return value;
		}

		public bool GetBool(string name, bool defaultValue)
		{
			if(!_args.TryGetPropertyValue(name, out JsonNode? node) || node == null) {
				return defaultValue;
			}
			if(node is JsonValue value) {
				if(value.TryGetValue(out bool b)) {
					return b;
				}
				if(value.TryGetValue(out string? str) && bool.TryParse(str, out b)) {
					return b;
				}
			}
			throw new McpToolException($"Argument '{name}' must be a boolean.");
		}

		public UInt32? GetAddress(string name)
		{
			if(!_args.TryGetPropertyValue(name, out JsonNode? node) || node == null) {
				return null;
			}

			if(node is JsonValue value) {
				if(value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out long l) && l >= 0 && l <= UInt32.MaxValue) {
					return (UInt32)l;
				} else if(value.TryGetValue(out string? str) && TryParseAddress(str, out UInt32 addr)) {
					return addr;
				}
			}
			throw new McpToolException($"Argument '{name}' is not a valid address. Use a JSON number (decimal) or a hex string such as \"$8000\", \"0x8000\", \"8000\" or \"$80:8000\".");
		}

		public UInt32 GetRequiredAddress(string name)
		{
			UInt32? value = GetAddress(name);
			if(value == null) {
				throw new McpToolException($"Missing required argument '{name}'.");
			}
			return value.Value;
		}

		public T? GetEnum<T>(string name) where T : struct, Enum
		{
			string? str = GetString(name);
			if(str == null) {
				return null;
			}
			return ParseEnum<T>(name, str);
		}

		public T GetRequiredEnum<T>(string name) where T : struct, Enum
		{
			T? value = GetEnum<T>(name);
			if(value == null) {
				throw new McpToolException($"Missing required argument '{name}'. Valid values: {string.Join(", ", Enum.GetNames<T>())}.");
			}
			return value.Value;
		}

		public static T ParseEnum<T>(string argName, string value) where T : struct, Enum
		{
			if(!int.TryParse(value, out _) && Enum.TryParse<T>(value.Trim(), true, out T result)) {
				return result;
			}
			throw new McpToolException($"Invalid value '{value}' for '{argName}'. Valid values: {string.Join(", ", Enum.GetNames<T>())}.");
		}

		public List<string> GetStringList(string name)
		{
			List<string> result = new();
			if(!_args.TryGetPropertyValue(name, out JsonNode? node) || node == null) {
				return result;
			}
			if(node is JsonArray array) {
				foreach(JsonNode? item in array) {
					if(item is JsonValue value && value.TryGetValue(out string? str)) {
						result.Add(str);
					} else {
						throw new McpToolException($"Argument '{name}' must be an array of strings.");
					}
				}
			} else if(node is JsonValue value && value.TryGetValue(out string? str)) {
				//Be lenient and accept a comma-separated string
				result.AddRange(str.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
			} else {
				throw new McpToolException($"Argument '{name}' must be an array of strings.");
			}
			return result;
		}

		public void ValidateKnownArguments(JsonObject schema)
		{
			if(schema["properties"] is not JsonObject props) {
				return;
			}
			foreach(KeyValuePair<string, JsonNode?> arg in _args) {
				if(!props.ContainsKey(arg.Key)) {
					throw new McpToolException($"Unknown argument '{arg.Key}'. Valid arguments: {string.Join(", ", props.Select(p => p.Key))}.");
				}
			}
		}

		/// <summary>Parses "$8000", "0x8000", "8000h", "8000", "$80:8000", "80:8000" (bank:address). Strings without a prefix are hex unless hexByDefault is false.</summary>
		public static bool TryParseAddress(string? str, out UInt32 address, bool hexByDefault = true)
		{
			address = 0;
			if(string.IsNullOrWhiteSpace(str)) {
				return false;
			}

			str = str.Trim().Replace("_", "");
			bool isHex = false;
			if(str.StartsWith("$")) {
				str = str.Substring(1);
				isHex = true;
			} else if(str.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
				str = str.Substring(2);
				isHex = true;
			} else if(str.EndsWith("h", StringComparison.OrdinalIgnoreCase)) {
				str = str.Substring(0, str.Length - 1);
				isHex = true;
			}

			int colon = str.IndexOf(':');
			if(colon >= 0) {
				//bank:address notation, always hex
				string bankStr = str.Substring(0, colon).TrimStart('$');
				string addrStr = str.Substring(colon + 1).TrimStart('$');
				if(UInt32.TryParse(bankStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out UInt32 bank) && UInt32.TryParse(addrStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out UInt32 addr) && bank <= 0xFF && addr <= 0xFFFF) {
					address = (bank << 16) | addr;
					return true;
				}
				return false;
			}

			if(isHex || hexByDefault) {
				return UInt32.TryParse(str, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
			}
			return UInt32.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out address);
		}

		private static bool TryParseNumber(string? str, out long value)
		{
			value = 0;
			if(TryParseAddress(str, out UInt32 addr, false)) {
				value = addr;
				return true;
			}
			return str != null && long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
		}
	}
}
