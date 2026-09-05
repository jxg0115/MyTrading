# -*- coding: utf-8 -*-
"""
从本地 MetaTrader 5 终端拉取真实历史数据，导出 CSV 供 FxBacktest 使用。
用法:
    python fetch_mt5.py EURUSD D1
    python fetch_mt5.py XAUUSD H1 2023-01-01 2025-12-31
"""
import sys
from datetime import datetime
from pathlib import Path

import MetaTrader5 as mt5

TERMINAL_PATH = r"D:\MT4BC\terminal64.exe"
OUT_DIR = Path(r"D:\StockSharp\MyTrading\data")


def main():
    symbol = sys.argv[1].upper() if len(sys.argv) > 1 else "EURUSD"
    tf_name = sys.argv[2].upper() if len(sys.argv) > 2 else "D1"
    begin = datetime.strptime(sys.argv[3], "%Y-%m-%d") if len(sys.argv) > 3 else datetime(2015, 1, 1)
    end = datetime.strptime(sys.argv[4], "%Y-%m-%d") if len(sys.argv) > 4 else datetime.now()

    tf_map = {
        "M1": mt5.TIMEFRAME_M1, "M5": mt5.TIMEFRAME_M5, "M15": mt5.TIMEFRAME_M15,
        "M30": mt5.TIMEFRAME_M30, "H1": mt5.TIMEFRAME_H1, "H4": mt5.TIMEFRAME_H4,
        "D1": mt5.TIMEFRAME_D1, "W1": mt5.TIMEFRAME_W1,
    }
    if tf_name not in tf_map:
        print(f"未知周期 {tf_name}，可选: {list(tf_map)}")
        return 1

    if not mt5.initialize(TERMINAL_PATH):
        print(f"MT5 连接失败: {mt5.last_error()}")
        print("请确认 MT5 终端已启动并登录过账户（首次需手动登录一次）")
        return 1

    account = mt5.account_info()
    if account:
        print(f"已连接账户: {account.login} @ {account.server} ({account.name})")
    else:
        print("警告: 未登录账户，历史数据可能不可用")

    if not mt5.symbol_select(symbol, True):
        print(f"无法访问品种 {symbol}: {mt5.last_error()}")
        mt5.shutdown()
        return 1

    rates = mt5.copy_rates_range(symbol, tf_map[tf_name], begin, end)
    mt5.shutdown()

    if rates is None or len(rates) == 0:
        print(f"没有取到 {symbol} {tf_name} 数据: {mt5.last_error()}")
        print("提示: 请在 MT5 里打开该品种图表并翻到最早的历史，让终端把数据下载完整")
        return 1

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    out_file = OUT_DIR / f"{symbol}_{tf_name}.csv"

    lines = ["time,open,high,low,close,volume"]
    for r in rates:
        t = datetime.fromtimestamp(r["time"]).strftime("%Y-%m-%d %H:%M:%S")
        lines.append(f"{t},{r['open']:.5f},{r['high']:.5f},{r['low']:.5f},{r['close']:.5f},{int(r['tick_volume'])}")

    out_file.write_text("\n".join(lines), encoding="utf-8")
    print(f"导出 {len(rates)} 根 {symbol} {tf_name} K线 ({lines[1].split(',')[0]} .. {lines[-1].split(',')[0]})")
    print(f"已保存: {out_file}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
