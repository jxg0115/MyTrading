# -*- coding: utf-8 -*-
"""
实时策略引擎：连接本地 MT5 终端，轮询行情、计算信号、可选自动下单（模拟账户）。
由 WebUI 后端启动/停止，状态写入 data/live_status.json 供网页轮询。

用法:
    python live_worker.py <品种> <周期> <策略sma|breakout> <快线> <慢线> <突破天数> <手数> <止损%> <自动下单0|1>
"""
import json
import os
import sys
import time
from datetime import datetime
from pathlib import Path

import MetaTrader5 as mt5

TERMINAL_PATH = r"D:\MT4BC\terminal64.exe"
STATE_FILE = Path(r"D:\StockSharp\MyTrading\data\live_status.json")
MAGIC = 666001

TF_MAP = {
    "M1": mt5.TIMEFRAME_M1, "M5": mt5.TIMEFRAME_M5, "M15": mt5.TIMEFRAME_M15,
    "M30": mt5.TIMEFRAME_M30, "H1": mt5.TIMEFRAME_H1, "H4": mt5.TIMEFRAME_H4,
    "D1": mt5.TIMEFRAME_D1, "W1": mt5.TIMEFRAME_W1,
}
TF_SECONDS = {"M1": 60, "M5": 300, "M15": 900, "M30": 1800,
              "H1": 3600, "H4": 14400, "D1": 86400, "W1": 604800}


def main():
    symbol = sys.argv[1].upper()
    tf_name = sys.argv[2].upper()
    strategy = sys.argv[3].lower()
    fast_len = int(sys.argv[4])
    slow_len = int(sys.argv[5])
    brk_len = int(sys.argv[6])
    lots = float(sys.argv[7])
    stop_pct = float(sys.argv[8])
    autotrade = sys.argv[9] == "1"
    take_pct = float(sys.argv[10]) if len(sys.argv) > 10 else 0
    trail_pct = float(sys.argv[11]) if len(sys.argv) > 11 else 0
    partial_pct = float(sys.argv[12]) if len(sys.argv) > 12 else 0

    signals = []          # 事件日志（最新在前，最多50条）
    last_state = None     # 上一次指标状态，用于边沿触发
    pos_partial_done = {} # 已分批落袋过的持仓单号

    def write_state(**kw):
        try:
            STATE_FILE.parent.mkdir(parents=True, exist_ok=True)
            kw["pid"] = os.getpid()
            kw["updated_epoch"] = int(time.time())
            STATE_FILE.write_text(json.dumps(kw, ensure_ascii=False), encoding="utf-8")
        except Exception:
            pass

    def log(text, price=None):
        signals.insert(0, {
            "time": datetime.now().strftime("%H:%M:%S"),
            "text": text,
            "price": price,
        })
        del signals[50:]

    if not mt5.initialize(TERMINAL_PATH):
        write_state(running=False, error=f"MT5 连接失败: {mt5.last_error()}", signals=[])
        return

    if not mt5.symbol_select(symbol, True):
        write_state(running=False, error=f"品种 {symbol} 不可用（MT5 市场报价里没有）", signals=[])
        mt5.shutdown()
        return

    tf = TF_MAP[tf_name]
    poll_sec = max(5, min(30, TF_SECONDS[tf_name] // 6))
    digits = mt5.symbol_info(symbol).digits

    write_state(running=True, symbol=symbol, tf=tf_name, strategy=strategy,
                autotrade=autotrade, signals=[], error=None, started=datetime.now().strftime("%H:%M:%S"))

    try:
        while True:
            acct = mt5.account_info()
            tick = mt5.symbol_info_tick(symbol)
            rates = mt5.copy_rates_from_pos(symbol, tf, 0, 300)

            state = {
                "running": True,
                "symbol": symbol, "tf": tf_name, "strategy": strategy, "autotrade": autotrade,
                "signals": signals,
                "error": None,
                "last_update": datetime.now().strftime("%H:%M:%S"),
                "account": None, "position": None,
                "bid": tick.bid if tick else None,
                "ask": tick.ask if tick else None,
                "candles": [],
            }

            if acct:
                state["account"] = {
                    "login": acct.login, "server": acct.server,
                    "balance": round(acct.balance, 2), "equity": round(acct.equity, 2),
                }

            # 当前持仓（只认本引擎下的单）
            pos = None
            for p in (mt5.positions_get(symbol=symbol) or []):
                if p.magic == MAGIC:
                    pos = p
                    break
            if pos:
                state["position"] = {
                    "side": "buy" if pos.type == mt5.POSITION_TYPE_BUY else "sell",
                    "volume": pos.volume,
                    "entry": pos.price_open,
                    "profit": round(pos.profit, 2),
                    "sl": pos.sl,
                }

                # ---- 移动止损（回撤止盈）：只朝有利方向移动 ----
                if trail_pct > 0 and tick:
                    is_long = pos.type == mt5.POSITION_TYPE_BUY
                    ref = tick.bid if is_long else tick.ask
                    new_sl = round(ref - ref * trail_pct / 100 if is_long
                                   else ref + ref * trail_pct / 100, digits)
                    better = (pos.sl == 0) or (is_long and new_sl > pos.sl) or \
                             ((not is_long) and new_sl < pos.sl)
                    if better and abs(new_sl - pos.sl) > 10 ** (-digits - 1):
                        mod = {
                            "action": mt5.TRADE_ACTION_SLTP,
                            "position": pos.ticket,
                            "sl": new_sl,
                            "tp": pos.tp,
                        }
                        r = mt5.order_send(mod)
                        if r and r.retcode == mt5.TRADE_RETCODE_DONE:
                            log(f"移动止损调整 -> {new_sl}", new_sl)

                # ---- 分批落袋：浮盈达到 partial_pct 平一半 ----
                if partial_pct > 0 and tick and not pos_partial_done.get(pos.ticket):
                    is_long = pos.type == mt5.POSITION_TYPE_BUY
                    ref = tick.bid if is_long else tick.ask
                    entry = pos.price_open
                    profit_pct = (ref - entry) / entry * (100 if is_long else -100)
                    if profit_pct >= partial_pct and pos.volume / 2 >= 0.01:
                        half = round(pos.volume / 2, 2)
                        close_req = {
                            "action": mt5.TRADE_ACTION_DEAL,
                            "position": pos.ticket,
                            "symbol": symbol,
                            "volume": half,
                            "type": mt5.ORDER_TYPE_SELL if is_long else mt5.ORDER_TYPE_BUY,
                            "price": tick.bid if is_long else tick.ask,
                            "deviation": 20,
                            "magic": MAGIC,
                            "comment": "partial-take",
                            "type_filling": mt5.ORDER_FILLING_FOK,
                        }
                        r = mt5.order_send(close_req)
                        ok = r and r.retcode == mt5.TRADE_RETCODE_DONE
                        log(f"分批落袋：浮盈 {profit_pct:.2f}% >= {partial_pct}%，"
                            f"{'平掉一半 ' + str(half) + ' 手' if ok else '平半仓失败 retcode ' + str(getattr(r, 'retcode', 'N/A'))}",
                            ref)
                        if ok:
                            pos_partial_done[pos.ticket] = True

            if rates is not None and len(rates) > slow_len + 2:
                closed = rates[:-1]                       # 去掉未走完的当前bar
                closes = [r["close"] for r in closed]
                state["candles"] = [
                    [int(r["time"]), r["open"], r["high"], r["low"], r["close"], int(r["tick_volume"])]
                    for r in closed[-200:]
                ]

                sig = None

                # ---------- SMA 交叉 ----------
                if strategy == "sma":
                    def sma(arr, n):
                        return sum(arr[-n:]) / n
                    fast_now, slow_now = sma(closes, fast_len), sma(closes, slow_len)
                    fast_prev = sum(closes[-fast_len - 1:-1]) / fast_len
                    slow_prev = sum(closes[-slow_len - 1:-1]) / slow_len
                    now_below = fast_now < slow_now
                    prev_below = fast_prev < slow_prev
                    if prev_below != now_below:
                        sig = "buy" if not now_below else "sell"

                # ---------- N日突破 ----------
                elif strategy == "breakout":
                    close = closes[-1]
                    prev_high = max(r["high"] for r in closed[-brk_len - 1:-1])
                    prev_low = min(r["low"] for r in closed[-brk_len - 1:-1])
                    if close > prev_high:
                        sig = "buy"
                    elif close < prev_low:
                        sig = "sell"

                # 边沿触发：同类信号只响应一次
                if sig is not None and sig != last_state:
                    last_state = sig
                    px = tick.last or tick.bid
                    log(f"{'▲ 买入信号' if sig == 'buy' else '▼ 卖出信号'} @ {px:.{digits}f}", px)

                    if autotrade:
                        execute(symbol, sig, lots, stop_pct, digits, tick, log)
                        time.sleep(1)   # 给成交回报留点时间
                    else:
                        log("（仅信号模式，未下单）", px)

            write_state(**state)
            time.sleep(poll_sec)
    finally:
        mt5.shutdown()


def execute(symbol, sig, lots, stop_pct, digits, tick, log):
    """目标仓位法：先平反向仓，再开新仓（带止损）。"""
    # 平掉本引擎的反向持仓
    for p in (mt5.positions_get(symbol=symbol) or []):
        if p.magic != MAGIC:
            continue
        is_buy_pos = p.type == mt5.POSITION_TYPE_BUY
        if (sig == "buy" and not is_buy_pos) or (sig == "sell" and is_buy_pos):
            close_req = {
                "action": mt5.TRADE_ACTION_DEAL,
                "position": p.ticket,
                "symbol": symbol,
                "volume": p.volume,
                "type": mt5.ORDER_TYPE_SELL if is_buy_pos else mt5.ORDER_TYPE_BUY,
                "price": tick.bid if is_buy_pos else tick.ask,
                "deviation": 20,
                "magic": MAGIC,
                "comment": "close",
                "type_filling": mt5.ORDER_FILLING_IOC,
            }
            r = mt5.order_send(close_req)
            if r is None or r.retcode != mt5.TRADE_RETCODE_DONE:
                # 部分券商不支持 IOC，退回 FOK
                close_req["type_filling"] = mt5.ORDER_FILLING_FOK
                r = mt5.order_send(close_req)
            log(f"平仓 {'多' if is_buy_pos else '空'} {p.volume} 手 -> retcode {getattr(r, 'retcode', 'N/A')}", tick.bid)

    # 开新仓
    tick = mt5.symbol_info_tick(symbol)
    is_buy = sig == "buy"
    price = tick.ask if is_buy else tick.bid
    sl_dist = price * stop_pct / 100.0 if stop_pct > 0 else 0
    tp_dist = price * take_pct / 100.0 if take_pct > 0 else 0
    req = {
        "action": mt5.TRADE_ACTION_DEAL,
        "symbol": symbol,
        "volume": lots,
        "type": mt5.ORDER_TYPE_BUY if is_buy else mt5.ORDER_TYPE_SELL,
        "price": price,
        "sl": round(price - sl_dist if is_buy else price + sl_dist, digits),
        "tp": round(price + tp_dist if is_buy else price - tp_dist, digits) if tp_dist else 0.0,
        "deviation": 20,
        "magic": MAGIC,
        "comment": "webui-live",
        "type_filling": mt5.ORDER_FILLING_IOC,
    }
    r = mt5.order_send(req)
    if r is None or r.retcode != mt5.TRADE_RETCODE_DONE:
        req["type_filling"] = mt5.ORDER_FILLING_FOK
        r = mt5.order_send(req)

    if r is not None and r.retcode == mt5.TRADE_RETCODE_DONE:
        log(f"✓ 已开{'多' if is_buy else '空'} {lots} 手 @ {price:.{digits}f} (SL {req['sl']})", price)
    else:
        log(f"✗ 下单失败 retcode={getattr(r, 'retcode', 'N/A')} {getattr(r, 'comment', '')}", price)


if __name__ == "__main__":
    main()
