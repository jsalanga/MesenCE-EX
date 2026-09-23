#!/usr/bin/env python3
"""End-to-end test of Mesen's MCP server, using the official MCP Python SDK.

Usage:
    pip install -r requirements.txt
    python test_mcp.py --rom "C:/path/to/game.sfc"
    python test_mcp.py                # uses a small generated homebrew SNES test ROM

Requirements in Mesen (Debug > Debugger settings > Integration):
    - "Enable the MCP server" checked (default port 8765)
    - "Allow write access" checked, so the script can load the ROM
      (without it, load the ROM manually in Mesen and pass --no-load)
"""

import argparse
import asyncio
import json
import os
import sys
import tempfile

from mcp import ClientSession

try:
    from mcp.client.streamable_http import streamable_http_client as http_client
except ImportError:
    from mcp.client.streamable_http import streamablehttp_client as http_client

TEST_LABEL = "McpTestLabel"


def build_test_rom() -> bytes:
    """Builds a 32KB LoROM SNES program: reset handler, a subroutine called in a loop, an NMI handler."""
    rom = bytearray([0xFF] * 0x8000)
    code = bytes([
        0x78, 0x18, 0xFB,              # $8000: SEI, CLC, XCE (native mode)
        0xC2, 0x30,                    # $8003: REP #$30
        0xA2, 0xFF, 0x1F,              # $8005: LDX #$1FFF
        0x9A,                          # $8008: TXS
        0xE2, 0x20,                    # $8009: SEP #$20
        0xA9, 0x81,                    # $800B: LDA #$81
        0x8D, 0x00, 0x42,              # $800D: STA $4200 (enable NMI + auto joypad read)
        0x64, 0x10,                    # $8010: STZ $10
        0x20, 0x17, 0x80,              # $8012: JSR $8017
        0x80, 0xFB,                    # $8015: BRA $8012
        0xE6, 0x10,                    # $8017: INC $10
        0xAD, 0x18, 0x42,              # $8019: LDA $4218 (joypad)
        0x85, 0x12,                    # $801C: STA $12
        0xAD, 0x30, 0x80,              # $801E: LDA $8030 (data read)
        0x85, 0x14,                    # $8021: STA $14
        0x60,                          # $8023: RTS
        0xAD, 0x10, 0x42, 0x40,        # $8024: LDA $4210, RTI (NMI handler)
        0x40,                          # $8028: RTI (IRQ/BRK/COP handler)
    ])
    rom[0:len(code)] = code
    rom[0x30:0x35] = b"HELLO"
    rom[0x7FC0:0x7FD5] = b"MESEN MCP TEST".ljust(21)
    rom[0x7FD5:0x7FDC] = bytes([0x20, 0x00, 0x05, 0x00, 0x01, 0x00, 0x00])

    def word(offset: int, value: int) -> None:
        rom[offset] = value & 0xFF
        rom[offset + 1] = value >> 8

    for vector in (0x7FE4, 0x7FE6, 0x7FE8, 0x7FEE, 0x7FF4, 0x7FF8, 0x7FFE):
        word(vector, 0x8028)
    word(0x7FEA, 0x8024)
    word(0x7FFA, 0x8024)
    word(0x7FFC, 0x8000)
    word(0x7FDC, 0xFFFF)
    word(0x7FDE, 0x0000)
    checksum = sum(rom) & 0xFFFF
    word(0x7FDE, checksum)
    word(0x7FDC, checksum ^ 0xFFFF)
    return bytes(rom)


class Runner:
    def __init__(self, session: ClientSession):
        self.session = session
        self.failures = 0

    async def call(self, name: str, **arguments) -> dict:
        result = await self.session.call_tool(name, arguments)
        text = "".join(c.text for c in result.content if getattr(c, "type", "") == "text")
        # SDK 2.x uses snake_case field names, 1.x uses camelCase
        is_error = getattr(result, "is_error", None) if hasattr(result, "is_error") else getattr(result, "isError", False)
        structured = getattr(result, "structured_content", None) if hasattr(result, "structured_content") else getattr(result, "structuredContent", None)
        if is_error:
            raise RuntimeError(f"{name} failed: {text}")
        if structured is not None:
            return structured
        return json.loads(text)

    def check(self, condition: bool, description: str, detail: object = "") -> bool:
        print(f"[{'PASS' if condition else 'FAIL'}] {description}" + (f"  {detail}" if detail != "" else ""))
        if not condition:
            self.failures += 1
        return condition


def parse_hex(value: str) -> int:
    return int(value.replace("$", "").replace(":", ""), 16)


async def run(args: argparse.Namespace) -> int:
    rom_path = args.rom
    if rom_path is None and not args.no_load:
        rom_path = os.path.join(tempfile.gettempdir(), "mesen_mcp_test.sfc")
        with open(rom_path, "wb") as f:
            f.write(build_test_rom())
        print(f"Generated test ROM: {rom_path}")

    async with http_client(args.url) as streams:
        read_stream, write_stream = streams[0], streams[1]
        async with ClientSession(read_stream, write_stream) as session:
            await session.initialize()
            t = Runner(session)

            # 1. List tools
            tools = {tool.name for tool in (await session.list_tools()).tools}
            expected = {"get_status", "pause", "resume", "step", "run_until", "read_memory", "disassemble",
                        "add_breakpoint", "remove_breakpoint", "set_label", "get_labels", "export_labels"}
            t.check(expected <= tools, f"tools/list returns {len(tools)} tools", sorted(expected - tools) or "")

            # 2. Load the ROM (paused on the first instruction)
            if not args.no_load:
                loaded = await t.call("load_rom", path=os.path.abspath(rom_path), pause=True)
                t.check(loaded.get("paused") is True, "load_rom loads the ROM and pauses", loaded.get("instruction", {}).get("address"))

            status = await t.call("get_status")
            if not t.check(status.get("rom_loaded") is True, "get_status reports a loaded ROM", status.get("rom", {}).get("name", "")):
                return 1
            main_cpu = status["main_cpu"]
            cpu_memory = next(c["memory_type"] for c in status["cpus"] if c["cpu"] == main_cpu)

            # 3. Pause
            paused = await t.call("pause")
            t.check(paused.get("paused") is True, "pause", paused.get("stop_reason"))

            # 4. Read memory
            ram_type = next((m["name"] for m in status["memory_types"] if m["name"].endswith("WorkRam") or m["name"] == "NesInternalRam"), cpu_memory)
            mem = await t.call("read_memory", memory_type=ram_type, address=0, length=16)
            t.check(len(mem.get("hex", "").split()) == 16, f"read_memory {ram_type} (16 bytes)", mem.get("hex"))

            # 5. Disassemble the reset vector
            header = status.get("snes_header")
            reset_vector = parse_hex(header["emulation_vectors"]["reset"]) if header else None
            if reset_vector is None:
                reset_vector = parse_hex(status["cpus"][0]["pc"])
            dis = await t.call("disassemble", address=reset_vector, count=10)
            rows = dis.get("rows", [])
            t.check(len(rows) > 0 and parse_hex(rows[0]["address"]) == reset_vector,
                    f"disassemble reset vector ${reset_vector:04X}", " | ".join(r["text"] for r in rows[:5]))

            # 6. Set a breakpoint and hit it with run_until (SNES: NMI handler, which runs every frame)
            if header:
                target = parse_hex(header["native_vectors"]["nmi"])
            else:
                target = parse_hex((await t.call("get_cpu_state"))["pc"])
            bp = await t.call("add_breakpoint", address=target, type="exec")
            t.check("id" in bp, f"add_breakpoint exec ${target:06X}", f"id={bp.get('id')}")
            stop = await t.call("run_until", timeout_frames=300, timeout_ms=15000)
            hit_address = parse_hex(stop.get("instruction", {}).get("address", "$FFFFFFFF"))
            t.check(stop.get("stop_reason") == "breakpoint" and hit_address == target,
                    "run_until stops on the breakpoint", f"{stop.get('stop_reason')} at {stop.get('instruction', {}).get('address')}")
            await t.call("remove_breakpoint", id=bp["id"])

            # 7. Set a label and check it in get_labels and in an exported .mlb file
            label = await t.call("set_label", address=target, label=TEST_LABEL, comment="Set by test_mcp.py")
            t.check(label.get("label") == TEST_LABEL, "set_label", f"{label.get('memory_type')} {label.get('address')}")
            labels = await t.call("get_labels", search=TEST_LABEL)
            t.check(any(lbl.get("label") == TEST_LABEL for lbl in labels.get("labels", [])), "label appears in get_labels")

            mlb_path = os.path.join(tempfile.gettempdir(), "mesen_mcp_test.mlb")
            await t.call("export_labels", path=mlb_path, overwrite=True)
            with open(mlb_path, encoding="utf-8-sig") as f:
                mlb_lines = [line for line in f.read().splitlines() if f":{TEST_LABEL}" in line]
            t.check(len(mlb_lines) == 1, "label appears in the exported .mlb", mlb_lines[0] if mlb_lines else mlb_path)

            # Cleanup
            await t.call("delete_label", label=TEST_LABEL)
            await t.call("resume")

            print(f"\n{'All checks passed' if t.failures == 0 else f'{t.failures} check(s) failed'}")
            return 0 if t.failures == 0 else 1


def main() -> None:
    parser = argparse.ArgumentParser(description="End-to-end test of Mesen's MCP server")
    parser.add_argument("--url", default="http://127.0.0.1:8765/mcp", help="MCP endpoint URL")
    parser.add_argument("--rom", help="ROM to load (default: a generated homebrew SNES test ROM)")
    parser.add_argument("--no-load", action="store_true", help="Don't load a ROM, use the one already loaded in Mesen")
    sys.exit(asyncio.run(run(parser.parse_args())))


if __name__ == "__main__":
    main()
