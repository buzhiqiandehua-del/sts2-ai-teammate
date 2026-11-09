#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
CombatSolver 二进制补丁全量归档脚本 (22 处 IL 补丁)
======================================================
目标 DLL: CombatSolver-0.50.1-Quark/CombatSolver.dll
原始备份: CombatSolver.dll.original.bak
终版目标 SHA-256: C1A2C5DF901942A3CB2FA9ED5429B0435342BF09DFDA009A71D9056F33FEEEFC

用法:
  python apply_combatsolver_patches.py           # 默认模式: 仅在内存中打补丁并校验 SHA-256，不覆写磁盘文件
  python apply_combatsolver_patches.py --apply   # 应用模式: 校验无误后覆写目标 CombatSolver.dll
"""

import os
import sys
import argparse
import hashlib

EXPECTED_OUTPUT_SHA256 = "C1A2C5DF901942A3CB2FA9ED5429B0435342BF09DFDA009A71D9056F33FEEEFC"

def resolve_paths(bak_arg, out_arg):
    candidates_bak = [
        bak_arg,
        r"F:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\CombatSolver-0.50.1-Quark\CombatSolver.dll.original.bak",
        r"mods\CombatSolver-0.50.1-Quark\CombatSolver.dll.original.bak",
        r"CombatSolver.dll.original.bak"
    ]
    candidates_out = [
        out_arg,
        r"F:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\CombatSolver-0.50.1-Quark\CombatSolver.dll",
        r"mods\CombatSolver-0.50.1-Quark\CombatSolver.dll",
        r"CombatSolver.dll"
    ]

    bak_path = None
    for p in candidates_bak:
        if p and os.path.exists(p):
            bak_path = p
            break

    out_path = out_arg or candidates_out[1] if os.path.exists(os.path.dirname(candidates_out[1])) else candidates_out[2]
    return bak_path, out_path

def apply_all_patches(data):
    def patch(offset, expected_hex, new_hex, desc):
        exp = bytes.fromhex(expected_hex)
        new = bytes.fromhex(new_hex)
        assert len(exp) == len(new), f"{desc}: length mismatch {len(exp)} != {len(new)}"
        actual = bytes(data[offset:offset+len(exp)])
        assert actual == exp, f"{desc} at 0x{offset:x}: expected {exp.hex()}, got {actual.hex()}"
        data[offset:offset+len(new)] = new
        print(f"  [OK] 0x{offset:06x}: {desc}")

    print("\n--- 正在应用全部 22 处 IL 补丁 ---")

    # 1. get_IsMultiplayerSession (RVA 0x69c9b -> file 0x67e9b)
    patch(0x67e9b, "282e06000a", "162a000000", "Patch 1: get_IsMultiplayerSession -> ldc.i4.0; ret; nop*3")

    # 2. SolverController.BeginCombat (RVA 0x6ada8 -> file 0x68fa8)
    patch(0x69007, "330c288e1400066fde130006", "320c17000000000000000000", "Patch 2: BeginCombat -> allow >=1 player and bypass LocalContext.NetId check")

    # 3. SolverController.ActivateTurnSetupResult (RVA 0x6af5c -> file 0x6915c)
    patch(0x691c7, "3367", "3267", "Patch 3: ActivateTurnSetupResult -> allow >=1 player across rounds")

    # 4. SolverController.MonitorCombatPresence (RVA 0x6ea2c -> file 0x6cc2c)
    patch(0x6cc2a, "4098000000", "3f98000000", "Patch 4: MonitorCombatPresence -> allow >=1 player")

    # 5. SolverController.CanSolve (RVA 0x71418 -> file 0x6f618)
    patch(0x6f627, "2e09", "2f09", "Patch 5: CanSolve -> allow >=1 player")

    # 6. SolverController.StartFullAutoDeployment (RVA 0x6ff00 -> file 0x6e100)
    patch(0x6e194, "7e05080004", "1600000000", "Patch 6a: StartFullAutoDeployment -> disable StopFullAutoOnWorseResult")
    patch(0x6e2a9, "7e03080004", "1600000000", "Patch 6b: StartFullAutoDeployment -> disable StopFullAutoOnCombatEnd")
    patch(0x6e322, "7e04080004", "1600000000", "Patch 6c: StartFullAutoDeployment -> disable StopFullAutoOnDeathTurn")

    # 7. SolverController.ToggleVisibilityFromShortcut (RVA 0x170818 -> file 0x16ea18)
    patch(0x16ea21, "3307", "3207", "Patch 7: ToggleVisibilityFromShortcut -> allow >=1 player")

    # 8. TurnActionSolver.SolveCore (RVA 0xa791c -> file 0xa5b1c)
    patch(0xa5b27, "2e0b", "2f0b", "Patch 8: TurnActionSolver.SolveCore -> allow >=1 player")

    # 9. TurnActionSolver.HandleMultiplayerScaling (RVA 0x1a3d90 -> file 0x1a1f90)
    patch(0x1a1fcc, "2e37", "2f37", "Patch 9: HandleMultiplayerScaling -> allow >=1 player")

    # 10. CombatPredictionHistory.GetCounters (RVA 0x1889dc -> file 0x186bdc)
    patch(0x186bff, "03027bf51600042e0b", "16162e100000000000", "Patch 10: CombatPredictionHistory.GetCounters -> allow single/any")

    # 11. CombatShowcaseCollector.IsEligibleOpening (RVA 0x5289c -> file 0x50a9c)
    patch(0x50ab1, "2e0c", "2f0c", "Patch 11: CombatShowcaseCollector.IsEligibleOpening -> allow >=1 player")

    # 12. CombatReplayRecording.Start (RVA 0x50de0 -> file 0x4efe0)
    patch(0x4efe5, "2e03", "2f03", "Patch 12: CombatReplayRecording.Start -> allow >=1 player")

    # 13. MethodSpec 0x2b0002e2 metadata token -> Enumerable.Last<Player>
    patch(0x509b7a, "4517ac74", "8b15ac74", "Patch 13: MethodSpec 0x2b0002e2 -> Enumerable.Last<Player>")

    # 14. BattleDamageTracker.GetSinglePlayer (MethodDef 0x060009EF, RVA 0x47505 -> file 0x45705)
    patch(0x363fc2, "9100", "9600", "Patch 14a: BattleDamageTracker.GetSinglePlayer -> make public static")
    patch(0x45715, "2e02", "2f02", "Patch 14b: GetSinglePlayer -> allow >=1 player")
    patch(0x45719, "026fc008000a166fc908000a2a", "026fc008000a28e202002b002a", "Patch 14c: GetSinglePlayer -> return state.Players.Last() (Player 2)")

    # 15. Replace all 57 calls to LocalContext.GetMe(CombatState) (28 87 0a 00 0a) with BattleDamageTracker.GetSinglePlayer(CombatState) (28 ef 09 00 06)
    needle = bytes.fromhex("28870a000a")
    replacement = bytes.fromhex("28ef090006")
    count = 0
    pos = 0
    while True:
        idx = data.find(needle, pos)
        if idx == -1:
            break
        data[idx:idx+5] = replacement
        count += 1
        pos = idx + 5
    assert count == 57, f"Expected 57 calls to LocalContext.GetMe(CombatState), found {count}"
    print(f"  [OK] Patch 15: Replaced {count}/57 calls to LocalContext.GetMe(CombatState) -> GetSinglePlayer (P2)")

    # 16. CombatPredictionSimulator..ctor (RVA 0x18ddec -> file 0x18bfec)
    patch(0x18c06d, "2e03", "2f03", "Patch 16a: CombatPredictionSimulator..ctor -> allow >=1 player")
    patch(0x18c073, "6fc008000a166fc908000a", "6fc008000a28e202002b00", "Patch 16b: CombatPredictionSimulator..ctor -> state.Players.Last() (Player 2)")

    # 17. PreCombatLiveStateSnapshot.Capture (RVA 0x1b6394 -> file 0x1b4594)
    patch(0x1b45e2, "2e0b", "2f0b", "Patch 17a: PreCombatLiveStateSnapshot.Capture -> allow >=1 player")
    patch(0x1b461a, "6fbf0d000a166fc908000a", "6fbf0d000a28e202002b00", "Patch 17b: PreCombatLiveStateSnapshot.Capture -> runState.Players.Last() (Player 2)")

    # 18. ForkMap.Register & ForkMap.RequireRemap (multi-threading PredictedCard mapping conflict neutralization)
    patch(0x1ab6ac, "72fc230770038c8503001b6f3706000a6fc805000a722224077028fe05000a731b06000a7a", "2a" + "00"*36, "Patch 18a: ForkMap.Register -> return instead of throwing on conflicting target")
    patch(0x1ab84d, "72fc230770038c8503001b6f3706000a6fc805000a724824077028fe05000a731b06000a7a", "032a" + "00"*35, "Patch 18b: ForkMap.RequireRemap -> return self (ldarg.1; ret) instead of throwing")

    # 19. SolverController.ApplyPerformancePreset (RVA 0x7c46c -> file 0x7a66c)
    patch(0x7a678, "031a33090228931400060b2b45", "0228931400060b2b4900000000", "Patch 19: ApplyPerformancePreset -> always enforce BuildCustomPerformance")

    # 20. CombatRootSnapshot.Capture
    patch(0x4ffcf, "288508000a", "2626261700", "Patch 20a: CombatRootSnapshot.Capture -> bypass projection mismatch")
    patch(0x50022, "288508000a", "2626261700", "Patch 20b: CombatRootSnapshot.Capture -> bypass capture state change mismatch")

    # 21. SolverController.CompleteSearchCore
    patch(0x6d948, "28090d0006", "2626160000", "Patch 21a: CompleteSearchCore -> eliminate SEARCH_STALE result discard")
    patch(0x6d8b0, "166f43120006", "260000000000", "Patch 21b: CompleteSearchCore -> preserve FullAutoEnabled on worse result")

    # 22. Directive #5.16: SolverController.RequestSearch search-stall early ret bypass (file 0x69a27)
    patch(0x69a27, "28ef0c0006120f1f331728d105000a120f724c29027028d205000a120f0728fd03002b120f28d605000a6fb60c00062a", "2b2e" + "00"*46, "Patch 22: SolverController.RequestSearch -> bypass search reentrancy stall early ret (#5.16)")

def main():
    parser = argparse.ArgumentParser(description="CombatSolver 全量 22 处 IL 补丁归档校验与应用脚本")
    parser.add_argument("--bak", default=None, help="原始备份文件路径 (CombatSolver.dll.original.bak)")
    parser.add_argument("--out", default=None, help="输出目标文件路径 (CombatSolver.dll)")
    parser.add_argument("--apply", action="store_true", help="指定此参数时覆写目标文件；默认仅在内存中校验")
    args = parser.parse_args()

    bak_path, out_path = resolve_paths(args.bak, args.out)

    if not bak_path or not os.path.exists(bak_path):
        print(f"[ERROR] 找不到原始备份文件: {bak_path}")
        sys.exit(1)

    print(f"=== CombatSolver 22 处 IL 补丁应用器 ===")
    print(f"  原始备份文件: {bak_path} ({os.path.getsize(bak_path)} 字节)")
    print(f"  目标输出文件: {out_path}")
    print(f"  执行模式: {'[WRITE] 真实写入磁盘' if args.apply else '[DRY-RUN] 纯内存校验'}")

    raw_data = bytearray(open(bak_path, "rb").read())
    apply_all_patches(raw_data)

    calc_sha256 = hashlib.sha256(raw_data).hexdigest().upper()
    print(f"\n--- SHA-256 完整性校验 ---")
    print(f"  内存产物 SHA-256: {calc_sha256}")
    print(f"  终版标准 SHA-256: {EXPECTED_OUTPUT_SHA256}")

    if calc_sha256 != EXPECTED_OUTPUT_SHA256:
        print("[FAIL] SHA-256 校验失败，产物不匹配！")
        sys.exit(1)

    print("[PASS] SHA-256 校验 100% 匹配！")

    if args.apply:
        mode = "r+b" if os.path.exists(out_path) else "wb"
        with open(out_path, mode) as f:
            f.seek(0)
            f.write(raw_data)
            f.truncate()
        print(f"[SUCCESS] 成功写入补丁文件: {out_path} ({len(raw_data)} 字节)")
    else:
        print("[INFO] 当前为安全 Dry-Run 模式，未修改磁盘文件。若需覆写请传入 --apply 参数。")

if __name__ == "__main__":
    main()
