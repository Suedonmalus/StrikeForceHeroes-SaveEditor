#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
mock_agent.py —— 「战火英雄修改器」的假注入代理（只为 GUI 自测，不碰游戏）。

严格按 docs/AGENT_PROTOCOL.md 实现服务端：
  * 命名管道  \\\\.\\pipe\\SFHTrainer（字节模式，双工）
  * UTF-8 编码，一行一条消息，以 \\n 结尾，字段用 \\t 分隔
  * 支持 PING / STATE / SETMONEY / SETRARITY / SETCAMO / SETMOD / SAVE / REFRESH / QUIT
  * 错误按协议返回 ERR <code> <message>

用法：
    F:\\1Software\\Python\\python.exe gui\\test\\mock_agent.py
    ... --money 5000 --loop --verbose

只用 ctypes 调 Win32，不依赖 pywin32 等第三方库。
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import sys
import time

# ---------------------------------------------------------------------------
# Win32
# ---------------------------------------------------------------------------
PIPE_ACCESS_DUPLEX = 0x00000003
PIPE_TYPE_BYTE = 0x00000000
PIPE_READMODE_BYTE = 0x00000000
PIPE_WAIT = 0x00000000
PIPE_UNLIMITED_INSTANCES = 255
BUFSIZE = 65536

ERROR_PIPE_CONNECTED = 535
ERROR_BROKEN_PIPE = 109
ERROR_PIPE_NOT_CONNECTED = 233
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.CreateNamedPipeW.restype = wt.HANDLE
k32.CreateNamedPipeW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, wt.DWORD,
                                 wt.DWORD, wt.DWORD, wt.DWORD, wt.LPVOID]
k32.ConnectNamedPipe.restype = wt.BOOL
k32.ConnectNamedPipe.argtypes = [wt.HANDLE, wt.LPVOID]
k32.DisconnectNamedPipe.restype = wt.BOOL
k32.DisconnectNamedPipe.argtypes = [wt.HANDLE]
k32.ReadFile.restype = wt.BOOL
k32.ReadFile.argtypes = [wt.HANDLE, wt.LPVOID, wt.DWORD, ctypes.POINTER(wt.DWORD), wt.LPVOID]
k32.WriteFile.restype = wt.BOOL
k32.WriteFile.argtypes = [wt.HANDLE, wt.LPVOID, wt.DWORD, ctypes.POINTER(wt.DWORD), wt.LPVOID]
k32.FlushFileBuffers.restype = wt.BOOL
k32.FlushFileBuffers.argtypes = [wt.HANDLE]
k32.CloseHandle.restype = wt.BOOL
k32.CloseHandle.argtypes = [wt.HANDLE]


def log(msg: str) -> None:
    print(f"[mock {time.strftime('%H:%M:%S')}] {msg}", file=sys.stderr, flush=True)


# ---------------------------------------------------------------------------
# 假数据（形状与协议一致，数值是随意的测试值）
# ---------------------------------------------------------------------------
RARITIES = [(0, "Normal", "×1"), (1, "Rare", "×1.5"), (2, "Epic", "×2"), (3, "Legendary", "×3")]

MODS = [
    (0,  "Damage",       "@% damage",                              "4",  "0x7FE", "0xF"),
    (1,  "Rate",         "@% fire rate",                            "5",  "0x7FE", "0xF"),
    (2,  "Reload",       "@% reload speed",                         "6",  "0x7FE", "0xE"),
    (3,  "Range",        "@% range",                                "5",  "0x7FE", "0xF"),
    (4,  "Aim",          "@% accuracy",                             "4",  "0x7FE", "0xF"),
    (5,  "HeadDamage",   "@% headshot damage",                      "3",  "0x7EE", "0xF"),
    (6,  "CritDamage",   "@% critical damage",                      "5",  "0x7FE", "0xF"),
    (7,  "CritChance",   "@% critical chance",                      "2",  "0x7FE", "0xF"),
    (8,  "Ammo",         "@% magazine size",                        "4",  "0x7CE", "0xF"),
    (9,  "AmmoSpare",    "@% spare ammo",                           "5",  "0x7CE", "0xF"),
    (10, "Multi",        "@% chance to fire a second projectile",   "3",  "0x7A6", "0x7"),
    (11, "Jam",          "@% jam resistance",                       "6",  "0x7CE", "0xF"),
    (12, "Explosive",    "@% chance to explode",                    "4",  "0x7A0", "0x7"),
    (13, "Dash",         "@% dash distance",                        "3",  "0x7F0", "0x7"),
    (14, "Vision",       "@% vision range",                         "5",  "0x7F0", "0xF"),
    (15, "Streak",       "@% damage per killstreak",                "4",  "0x7FE", "0xF"),
    (16, "Exp",          "@% experience gain",                      "3",  "0x7FF", "0xF"),
    (17, "DamageAir",    "@% damage while airborne",                "6",  "0x7FE", "0xF"),
    (18, "DamageStreak", "@% damage while on a streak",             "5",  "0x7FE", "0xF"),
    (19, "Block",        "@% block chance",                         "4",  "0x7F0", "0xF"),
]


# 皮肤目录：index, 名称, 描述, 稀有度
CAMOS = [
    (0, "Arctic",   "雪地迷彩", 0),
    (1, "Digital",  "数码迷彩", 1),
    (2, "Gold",     "黄金皮肤", 3),
]

# id, name, type, level, rarity, value, equippedMask, camoIndex, [ (modIndex, modRarity), ... ]
ITEMS = [
    (101, "katana",        4,  3, 2, 2400.00, 1, [(0, 3), (3, 1), (5, 2), (19, 0)]),
    (102, "G36",           1,  5, 1, 1800.00, 2, [(1, 2), (8, 1), (9, 0), (11, 0)]),
    (103, ".44 Automag",   3,  2, 3, 5200.00, 0, [(6, 3), (7, 2), (0, 1), (4, 0)]),
    (104, "MP5",           2,  4, 0, 900.00,  0, []),
    (105, "RPG-7",         6,  6, 2, 6100.00, 0, [(12, 2), (3, 1), (0, 0), (2, 0)]),
    (106, "Spas-12",       9,  4, 1, 2100.00, 0, [(10, 1), (8, 2), (11, 1), (1, 0)]),
    (107, "AWP",           10, 7, 3, 8800.00, 0, [(5, 3), (6, 3), (4, 2), (3, 1)]),
    (108, "M9",            7,  1, 0, 300.00,  0, []),
    (109, "Riot Shield",   8,  5, 1, 1500.00, 4, [(19, 2), (13, 1), (14, 0), (9, 0)]),
    (110, "Combat Helmet", -1, 3, 1, 700.00,  4, []),
    (111, "Kevlar Vest",   -1, 4, 2, 1400.00, 4, []),
    (112, "Ammo Pouch",    -1, 1, 0, 250.00,  0, []),
    (113, "Boots",         -1, 2, 0, 200.00,  0, []),
    (114, "Frag Grenade",  5,  2, 1, 400.00,  0, []),
    # 协议 v1.1：游戏内克隆对象的 name 可能为空，GUI 要回退成「未命名武器 #id」
    (115, "",              4,  1, 0, 100.00,  0, []),
]


class MockState:
    def __init__(self, money: int = 12345, curclass: str = "Rifleman", ready_after: float = 0.0):
        self.money = money
        self.curclass = curclass
        # 协议 v1.1：READY=0 表示存档还在加载，所有写命令返回 ERR 1。
        # ready_after > 0 时先返回 READY=0，过这么多秒后变成 READY=1。
        self.ready_after = ready_after
        self._t0 = time.monotonic()

        # 深拷贝 item 列表，避免多次运行互相污染
        self.items = [
            {"id": i[0], "name": i[1], "type": i[2], "level": i[3], "rarity": i[4],
             "value": i[5], "equipped": i[6], "camo": i[7], "mods": [list(p) for p in i[8]]}
            for i in ITEMS
        ]
        self.saves = 0

    @property
    def ready(self) -> bool:
        return self.ready_after <= 0 or (time.monotonic() - self._t0) >= self.ready_after

    def item(self, item_id: int):
        for it in self.items:
            if it["id"] == item_id:
                return it
        return None

    # ---- STATE 块 -------------------------------------------------------
    def state_lines(self) -> list[str]:
        out = ["STATE", f"MONEY\t{self.money}", f"READY\t{1 if self.ready else 0}",
               f"CURCLASS\t{self.curclass}"]
        for idx, name, mult in RARITIES:
            out.append(f"RARITY\t{idx}\t{name}\t{mult}")
        out.append(f"MODCOUNT\t{len(MODS)}")
        for idx, name, desc, base, wmask, rmask in MODS:
            out.append(f"MOD\t{idx}\t{name}\t{desc}\t{base}\t{wmask}\t{rmask}")
        out.append(f"CAMOCOUNT\t{len(CAMOS)}")
        for idx, name, desc, rarity in CAMOS:
            out.append(f"CAMO\t{idx}\t{name}\t{desc}\t{rarity}")
        out.append(f"ITEMCOUNT\t{len(self.items)}")
        for it in self.items:
            parts = [str(it["id"]), it["name"], str(it["type"]), str(it["level"]),
                     str(it["rarity"]), f"{it['value']:.2f}", str(it["equipped"]),
                     str(it["camo"]), str(len(it["mods"]))]
            for mod_index, mod_rarity in it["mods"]:
                parts.append(str(mod_index))
                parts.append(str(mod_rarity))
            out.append("ITEM\t" + "\t".join(parts))
        out.append("END")
        return out


class Server:
    def __init__(self, state: MockState, verbose: bool = False):
        self.state = state
        self.verbose = verbose
        self.quit = False

    # ---- 单条命令处理 ---------------------------------------------------
    def handle(self, line: str) -> list[str]:
        line = line.rstrip("\r")
        if not line.strip():
            return []
        parts = line.split("\t")
        if len(parts) == 1:
            parts = line.split(" ")
            parts = [p for p in parts if p != ""]
        cmd = parts[0].upper()
        args = parts[1:]
        log(f"<< {line}")

        if cmd == "PING":
            return ["PONG"]

        if cmd == "STATE":
            lines = self.state.state_lines()
            if self.verbose:
                log(">> STATE 块 " + f"{len(lines)} 行")
            return lines

        # 协议 v1.1：READY=0 时一律返回 ERR 1（not-ready）
        if cmd in ("SETMONEY", "SETRARITY", "SETCAMO", "SETMOD", "SAVE") and not self.state.ready:
            log("   拒绝：游戏未就绪（READY=0）")
            return ["ERR 1 not-ready"]

        if cmd == "SETMONEY":
            if len(args) != 1:
                return ["ERR 2 bad-args"]
            try:
                value = int(args[0])
            except ValueError:
                return ["ERR 2 bad-args"]
            value = max(0, min(999999, value))
            self.state.money = value
            return [f"OK {value}"]

        if cmd == "SETRARITY":
            if len(args) != 2:
                return ["ERR 2 bad-args"]
            item_id, rarity = (int(x) for x in args)
            it = self.state.item(item_id)
            if it is None:
                return ["ERR 3 item-not-found"]
            if it["type"] < 0:
                return ["ERR 4 item-has-no-weapon-data"]
            if rarity < 0 or rarity > 6:
                return ["ERR 2 rarity"]
            it["rarity"] = rarity
            return ["OK"]

        if cmd == "SETCAMO":
            if len(args) != 2:
                return ["ERR 2 bad-args"]
            item_id, camo_index = (int(x) for x in args)
            it = self.state.item(item_id)
            if it is None:
                return ["ERR 3 item-not-found"]
            if it["type"] < 0:
                return ["ERR 4 item-has-no-weapon-data"]
            if camo_index < -1 or camo_index >= len(CAMOS):
                return ["ERR 5 camo-index"]
            it["camo"] = camo_index
            return ["OK"]

        if cmd == "SETMOD":
            if len(args) != 4:
                return ["ERR 2 bad-args"]
            try:
                item_id, slot, mod_index, rarity = (int(x) for x in args)
            except ValueError:
                return ["ERR 2 bad-args"]
            it = self.state.item(item_id)
            if it is None:
                return ["ERR 3 item-not-found"]
            if it["type"] < 0:
                return ["ERR 4 item-has-no-weapon-data"]
            if slot < 0 or slot > 3:
                return ["ERR 2 bad-args"]
            if mod_index < 0 or mod_index >= len(MODS):
                return ["ERR 5 mod-index-invalid"]
            if rarity < 0 or rarity > 3:
                return ["ERR 2 bad-args"]
            while len(it["mods"]) < 4:
                it["mods"].append([0, 0])
            it["mods"][slot] = [mod_index, rarity]
            return ["OK"]

        if cmd == "CLEARMODS":
            if len(args) != 1:
                return ["ERR 2 bad-args"]
            try:
                item_id = int(args[0])
            except ValueError:
                return ["ERR 2 bad-args"]
            it = self.state.item(item_id)
            if it is None:
                return ["ERR 3 item-not-found"]
            if it["type"] < 0:
                return ["ERR 4 item-has-no-weapon-data"]
            it["mods"] = []
            return ["OK"]

        if cmd == "SAVE":
            self.state.saves += 1
            log(f"   SAVE #{self.state.saves}：money={self.state.money}，"
                f"武器带词条={sum(1 for i in self.state.items if i['mods'])}")
            return ["OK"]

        if cmd == "REFRESH":
            return ["OK"]

        if cmd == "QUIT":
            self.quit = True
            return ["OK"]

        return ["ERR 2 bad-args"]

    # ---- 连接生命周期 ---------------------------------------------------
    def serve_client(self, handle) -> None:
        pending = b""
        buf = ctypes.create_string_buffer(BUFSIZE)
        nread = wt.DWORD(0)
        while not self.quit:
            ok = k32.ReadFile(handle, buf, BUFSIZE, ctypes.byref(nread), None)
            if not ok:
                err = ctypes.get_last_error()
                if err in (ERROR_BROKEN_PIPE, ERROR_PIPE_NOT_CONNECTED):
                    log("客户端断开。")
                else:
                    log(f"ReadFile 失败，Win32 错误 {err}")
                return
            if nread.value == 0:
                log("客户端关闭了管道。")
                return
            pending += buf.raw[:nread.value]
            while b"\n" in pending:
                raw, pending = pending.split(b"\n", 1)
                line = raw.decode("utf-8", errors="replace")
                for reply in self.handle(line):
                    self.write(handle, reply)

    @staticmethod
    def write(handle, text: str) -> None:
        data = (text + "\n").encode("utf-8")
        cbuf = ctypes.create_string_buffer(data, len(data))
        written = wt.DWORD(0)
        ok = k32.WriteFile(handle, cbuf, len(data), ctypes.byref(written), None)
        if not ok:
            raise OSError(f"WriteFile 失败，Win32 错误 {ctypes.get_last_error()}")
        k32.FlushFileBuffers(handle)


def main() -> int:
    ap = argparse.ArgumentParser(description="SFHTrainer 假代理（命名管道 mock 服务端）")
    ap.add_argument("--pipe-name", default="SFHTrainer", help="管道名，默认 SFHTrainer")
    ap.add_argument("--money", type=int, default=12345, help="初始金钱")
    ap.add_argument("--curclass", default="Rifleman", help="CURCLASS 字段")
    ap.add_argument("--ready-after", type=float, default=0.0,
                    help="模拟存档加载：先返回 READY=0，N 秒后才变 READY=1（默认 0=立刻就绪）")
    ap.add_argument("--loop", action="store_true", help="客户端断开后继续等待下一个连接")
    ap.add_argument("--verbose", action="store_true", help="打印 STATE 块信息")
    args = ap.parse_args()

    pipe_path = r"\\.\pipe" + "\\" + args.pipe_name
    state = MockState(money=args.money, curclass=args.curclass, ready_after=args.ready_after)
    server = Server(state, verbose=args.verbose)

    h = k32.CreateNamedPipeW(
        pipe_path,
        PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
        PIPE_UNLIMITED_INSTANCES,
        BUFSIZE, BUFSIZE, 0, None)
    if h == INVALID_HANDLE_VALUE or not h:
        log(f"CreateNamedPipeW 失败，Win32 错误 {ctypes.get_last_error()}")
        return 2

    log(f"监听 {pipe_path}（money={state.money}，物品 {len(state.items)} 件，词条 {len(MODS)} 条，"
        f"READY={1 if state.ready else 0}"
        + (f"，{args.ready_after:g} 秒后就绪" if args.ready_after > 0 else "") + "）")

    try:
        while True:
            ok = k32.ConnectNamedPipe(h, None)
            if not ok:
                err = ctypes.get_last_error()
                if err != ERROR_PIPE_CONNECTED:
                    log(f"ConnectNamedPipe 失败，Win32 错误 {err}")
                    return 3
            log("客户端已连接。")
            try:
                server.serve_client(h)
            finally:
                k32.DisconnectNamedPipe(h)
                k32.FlushFileBuffers(h)

            if server.quit:
                log("收到 QUIT，退出。")
                return 0
            if not args.loop:
                log("客户端已断开且未收到 QUIT，退出。")
                return 0
            log("等待下一个连接…")
    except KeyboardInterrupt:
        log("被用户中断。")
        return 0
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    sys.exit(main())
