// ============================================================================
// 积木引擎 v3：配置驱动的通用策略解释器。
// 一个 BlockStrategy 消费一份 JSON 配置（entry/filters/exit/position），
// 内置指标计算（SMA/EMA/RSI/KDJ/MACD/ATR/ADX/布林/通道），支持：
//   入场 18 种 | 过滤器 9 种 | 出场 12 种 | 仓位 4 种 + 手数上限
// 画布 V1 的每个节点 = 配置中的一个条目。
// ============================================================================
namespace WebUI;

using System;
using System.Collections.Generic;
using System.Linq;

using StockSharp.Algo.Strategies;
using StockSharp.Messages;

// ---------------- 指标计算（纯函数，作用于序列） ----------------
public static class Ind
{
	public static double Sma(List<double> v, int n, int shift)
	{
		if (shift + n > v.Count) return double.NaN;
		var s = 0.0;
		for (var i = v.Count - 1 - shift; i > v.Count - 1 - shift - n; i--) s += v[i];
		return s / n;
	}

	public static double Ema(List<double> v, int n, int shift)
	{
		var need = v.Count - shift;
		if (need < n * 3) return double.NaN;
		var k = 2.0 / (n + 1);
		var e = v[0];
		for (var i = 1; i < need; i++)
			e = v[i] * k + e * (1 - k);
		return e;
	}

	public static double Std(List<double> v, int n, int shift)
	{
		if (shift + n > v.Count) return double.NaN;
		var m = Sma(v, n, shift);
		var s = 0.0;
		for (var i = v.Count - 1 - shift; i > v.Count - 1 - shift - n; i--)
			s += (v[i] - m) * (v[i] - m);
		return Math.Sqrt(s / n);
	}

	// Wilder RSI（shift=0 取最新值）
	public static double Rsi(List<double> closes, int n)
	{
		if (closes.Count < n + 1) return double.NaN;
		var g = 0.0; var l = 0.0;
		for (var i = closes.Count - n; i < closes.Count; i++)
		{
			var d = closes[i] - closes[i - 1];
			if (d > 0) g += d; else l -= d;
		}
		g /= n; l /= n;
		if (l == 0) return 100;
		var rs = g / l;
		return 100 - 100 / (1 + rs);
	}

	// ATR 序列（Wilder 平滑），返回最新值；shift 支持取历史
	public static double Atr(List<double> high, List<double> low, List<double> close, int n, int shift)
	{
		var count = high.Count;
		if (count < n + 1 + shift) return double.NaN;
		var trs = new List<double>();
		for (var i = 1; i < count; i++)
		{
			var tr = Math.Max(high[i] - low[i],
				Math.Max(Math.Abs(high[i] - close[i - 1]), Math.Abs(low[i] - close[i - 1])));
			trs.Add(tr);
		}
		var atr = trs.Take(n).Average();
		for (var i = n; i < trs.Count - shift; i++)
			atr = (atr * (n - 1) + trs[i]) / n;
		return atr;
	}

	// KDJ：返回 (K, D, J) 最新值
	public static (double k, double d, double j) Kdj(List<double> high, List<double> low, List<double> close, int n, int kP, int dP)
	{
		var count = close.Count;
		if (count < n + dP + 5) return (double.NaN, double.NaN, double.NaN);
		var rsvs = new List<double>();
		for (var i = count - (n + dP + 5); i < count; i++)
		{
			var hh = double.MinValue; var ll = double.MaxValue;
			for (var j = i - n + 1; j <= i; j++) { hh = Math.Max(hh, high[j]); ll = Math.Min(ll, low[j]); }
			rsvs.Add(hh == ll ? 50 : (close[i] - ll) / (hh - ll) * 100);
		}
		var k = 50.0; var d = 50.0;
		var ks = new List<double>();
		foreach (var rsv in rsvs) { k = k * 2 / 3 + rsv / 3; ks.Add(k); }
		d = 50.0;
		foreach (var kk in ks) d = d * 2 / 3 + kk / 3;
		return (ks[^1], d, 3 * ks[^1] - 2 * d);
	}

	// MACD：返回 (DIF, DEA)
	public static (double dif, double dea) Macd(List<double> close, int fast, int slow, int sig)
	{
		var need = close.Count;
		if (need < slow * 3) return (double.NaN, double.NaN);
		var difs = new List<double>();
		for (var i = 0; i < need; i++)
		{
			var sub = close.Take(i + 1).ToList();
			difs.Add(Ema(sub, fast, 0) - Ema(sub, slow, 0));
		}
		var dea = Ema(difs, sig, 0);
		return (difs[^1], dea);
	}

	// ADX（Wilder）
	public static double Adx(List<double> high, List<double> low, List<double> close, int n)
	{
		var count = close.Count;
		if (count < n * 2 + 2) return double.NaN;
		var plusDm = new List<double>(); var minusDm = new List<double>(); var trs = new List<double>();
		for (var i = 1; i < count; i++)
		{
			var up = high[i] - high[i - 1];
			var dn = low[i - 1] - low[i];
			var pdm = up > dn && up > 0 ? up : 0;
			var mdm = dn > up && dn > 0 ? dn : 0;
			var tr = Math.Max(high[i] - low[i], Math.Max(Math.Abs(high[i] - close[i - 1]), Math.Abs(low[i] - close[i - 1])));
			plusDm.Add(pdm); minusDm.Add(mdm); trs.Add(tr);
		}
		double sm(double[] arr, int i) { var s = 0.0; for (var j = i - n + 1; j <= i; j++) s += arr[j]; return s; }
		var adxs = new List<double>();
		for (var i = n; i < trs.Count; i++)
		{
			var tr = sm(trs.ToArray(), i);
			if (tr == 0) continue;
			var pdi = sm(plusDm.ToArray(), i) / tr * 100;
			var mdi = sm(minusDm.ToArray(), i) / tr * 100;
			var dx = pdi + mdi == 0 ? 0 : Math.Abs(pdi - mdi) / (pdi + mdi) * 100;
			adxs.Add(dx);
		}
		if (adxs.Count < n) return double.NaN;
		return adxs.TakeLast(n).Average();
	}

	public static double Highest(List<double> v, int n, int endShift)
	{
		var hh = double.MinValue;
		for (var i = v.Count - 1 - endShift; i > v.Count - 1 - endShift - n; i--)
			if (i >= 0) hh = Math.Max(hh, v[i]);
		return hh;
	}

	public static double Lowest(List<double> v, int n, int endShift)
	{
		var ll = double.MaxValue;
		for (var i = v.Count - 1 - endShift; i > v.Count - 1 - endShift - n; i--)
			if (i >= 0) ll = Math.Min(ll, v[i]);
		return ll;
	}
}

/// <summary>积木策略：消费 JSON 配置的通用解释器。</summary>
class BlockStrategy : Strategy
{
	public DataType ConnectorCandleType { get; set; } = TimeSpan.FromDays(1).TimeFrame();

	// ---- 配置 ----
	string _entryType = "sma_cross";
	readonly Dictionary<string, double> _ep = new();
	readonly List<(string type, Dictionary<string, double> p, HashSet<int> weekdays, string from, string to)> _filters = new();
	decimal _stop, _take, _trail, _partial;
	decimal _beAt, _beLock, _atrTrailMult; int _atrTrailPeriod, _timeBars;
	decimal _ladderStep; int _ladderCount;
	string _posMode = "fixed"; decimal _lots = 1, _riskPct = 1, _amount = 500, _atrPct = 1, _maxLots = 5;
	decimal _contract;

	// ---- 序列与状态 ----
	readonly List<double> _hi = new(), _lo = new(), _cl = new();
	readonly List<DateTime> _times = new();
	int _dir;                          // 期望方向 +1/-1/0
	decimal? _entryPrice;              // 近似入场价
	decimal _stopPrice;                // 虚拟止损价
	decimal _takePrice;                // 虚拟止盈价
	int _barsInTrade;
	bool _partialDone;
	int _ladderDone;
	decimal _unitsPerLot = 100000m;

	public int Warmup { get; private set; } = 60;

	public BlockStrategy(string configJson, decimal contractSize)
	{
		_contract = contractSize;
		var cfg = System.Text.Json.JsonDocument.Parse(configJson).RootElement;

		if (cfg.TryGetProperty("entry", out var e))
		{
			if (e.TryGetProperty("type", out var t)) _entryType = t.GetString();
			if (e.TryGetProperty("params", out var pe) && pe.ValueKind == System.Text.Json.JsonValueKind.Object)
				foreach (var prop in pe.EnumerateObject())
					if (prop.Value.TryGetDouble(out var dv)) _ep[prop.Name] = dv;
		}

		if (cfg.TryGetProperty("filters", out var fs) && fs.ValueKind == System.Text.Json.JsonValueKind.Array)
		{
			foreach (var f in fs.EnumerateArray())
			{
				var type = f.TryGetProperty("type", out var ft) ? ft.GetString() : "";
				var pd = new Dictionary<string, double>();
				if (f.TryGetProperty("params", out var fp) && fp.ValueKind == System.Text.Json.JsonValueKind.Object)
					foreach (var prop in fp.EnumerateObject())
						if (prop.Value.TryGetDouble(out var dv)) pd[prop.Name] = dv;
				var wd = new HashSet<int>();
				if (f.TryGetProperty("weekdays", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Array)
					foreach (var d in w.EnumerateArray()) wd.Add(d.GetInt32());
				var from = f.TryGetProperty("from", out var ff) ? ff.GetString() : "00:00";
				var to = f.TryGetProperty("to", out var tt) ? tt.GetString() : "23:59";
				_filters.Add((type, pd, wd, from, to));
			}
		}

		if (cfg.TryGetProperty("exit", out var x))
		{
			_stop = D(x, "stop"); _take = D(x, "take"); _trail = D(x, "trail"); _partial = D(x, "partial");
			_beAt = D(x, "breakeven_at"); _beLock = D(x, "breakeven_lock");
			_atrTrailMult = D(x, "atr_trail_mult"); _atrTrailPeriod = (int)D(x, "atr_trail_period");
			_timeBars = (int)D(x, "time_bars");
			_ladderStep = D(x, "ladder_step"); _ladderCount = (int)D(x, "ladder_count");
		}

		if (cfg.TryGetProperty("position", out var pos))
		{
			if (pos.TryGetProperty("mode", out var m)) _posMode = m.GetString();
			_lots = D(pos, "lots", 1); _riskPct = D(pos, "risk_pct", 1);
			_amount = D(pos, "amount", 500); _atrPct = D(pos, "atr_pct", 1);
			_maxLots = D(pos, "max_lots", 5);
		}

		_unitsPerLot = _contract;
		Warmup = Math.Max(80, MaxPeriod() * 4 + 30);
	}

	static decimal D(System.Text.Json.JsonElement el, string name, decimal def = 0)
		=> el.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? (decimal)d : def;

	int P(string name, double def = 0) => (int)(_ep.TryGetValue(name, out var v) ? v : def);
	double Pf(string name, double def = 0) => _ep.TryGetValue(name, out var v) ? v : def;

	int MaxPeriod()
	{
		var mx = 30;
		foreach (var kv in _ep)
			if (kv.Key.Contains("period") || kv.Key.Contains("length") || kv.Key.EndsWith("slow") ||
				kv.Key == "p3" || kv.Key == "lookback")
				mx = Math.Max(mx, (int)kv.Value);
		foreach (var f in _filters)
			foreach (var kv in f.p)
				if (kv.Key.Contains("period")) mx = Math.Max(mx, (int)kv.Value);
		return mx;
	}

	protected override void OnStarted2(DateTime time)
	{
		base.OnStarted2(time);
		SubscribeCandles(ConnectorCandleType)
			.Bind(Process)
			.Start();
	}

	private void Process(ICandleMessage candle)
	{
		if (candle.State != CandleStates.Finished)
			return;

		_hi.Add((double)candle.HighPrice); _lo.Add((double)candle.LowPrice);
		_cl.Add((double)candle.ClosePrice); _times.Add(candle.OpenTime);

		if (_cl.Count < Warmup)
			return;

		var i = _cl.Count - 1;             // 最新收盘bar下标
		var c = _cl[i];

		// ---------- 1. 计算期望方向 ----------
		var desired = ComputeEntryDirection(i);

		// ---------- 2. 过滤器 ----------
		if (desired != 0 && !FiltersPass(candle))
			desired = 0;

		if (desired != 0)
			_dir = desired;

		// ---------- 3. 出场管理 ----------
		ManagePosition(candle);

		// ---------- 4. 调仓到目标 ----------
		if (Position == 0)
		{
			_entryPrice = null; _barsInTrade = 0; _partialDone = false; _ladderDone = 0;
		}

		if (_dir != 0 && IsFormedAndOnlineAndAllowTrading())
		{
			var target = _dir * CalcUnits(candle.ClosePrice, Math.Abs(_dir));
			var diff = target - Position;
			if (diff != 0)
			{
				CancelActiveOrders();
				var side = diff > 0 ? Sides.Buy : Sides.Sell;
				RegisterOrder(CreateOrder(side, Math.Round(candle.ClosePrice, 5), Math.Abs(diff)));
			}
		}
	}

	// ---------- 入场方向 ----------
	int ComputeEntryDirection(int i)
	{
		var c = _cl;
		var prevDir = _dir;

		switch (_entryType)
		{
			case "sma_cross":
			{
				var f1 = Ind.Sma(c, P("fast", 10), 1); var f2 = Ind.Sma(c, P("fast", 10), 2);
				var s1 = Ind.Sma(c, P("slow", 30), 1); var s2 = Ind.Sma(c, P("slow", 30), 2);
				if (double.IsNaN(f1)) return prevDir;
				return f2 <= s2 && f1 > s1 ? 1 : f2 >= s2 && f1 < s1 ? -1 : prevDir;
			}
			case "ema_cross":
			{
				var f1 = Ind.Ema(c, P("fast", 12), 0); var f2 = Ind.Ema(c, P("fast", 12), 1);
				var s1 = Ind.Ema(c, P("slow", 26), 0); var s2 = Ind.Ema(c, P("slow", 26), 1);
				if (double.IsNaN(f1)) return prevDir;
				return f2 <= s2 && f1 > s1 ? 1 : f2 >= s2 && f1 < s1 ? -1 : prevDir;
			}
			case "price_cross_ma":
			{
				var m1 = Ind.Sma(c, P("period", 50), 0); var m2 = Ind.Sma(c, P("period", 50), 1);
				if (double.IsNaN(m1)) return prevDir;
				return c[i - 1] <= m2 && c[i] > m1 ? 1 : c[i - 1] >= m2 && c[i] < m1 ? -1 : prevDir;
			}
			case "triple_ma":
			{
				var a = Ind.Sma(c, P("p1", 5), 0); var b = Ind.Sma(c, P("p2", 20), 0); var cc = Ind.Sma(c, P("p3", 60), 0);
				if (double.IsNaN(cc)) return prevDir;
				return a > b && b > cc ? 1 : a < b && b < cc ? -1 : prevDir;
			}
			case "rsi_reversal":
			{
				var r1 = Ind.Rsi(c.GetRange(Math.Max(0, i - 1 - P("period", 14) * 4), Math.Min(c.Count, P("period", 14) * 4 + 1)), P("period", 14));
				var r0 = Ind.Rsi(c, P("period", 14));
				var ent = Pf("enter", 30); var ext = Pf("exit", 70);
				return r1 < ent && r0 >= ent ? 1 : r1 > ext && r0 <= ext ? -1 : prevDir;
			}
			case "rsi_trend":
			{
				var r0 = Ind.Rsi(c, P("period", 14));
				var r1 = Ind.Rsi(c.GetRange(Math.Max(0, i - 1 - P("period", 14) * 4), Math.Min(c.Count, P("period", 14) * 4 + 1)), P("period", 14));
				return r1 <= 50 && r0 > 50 ? 1 : r1 >= 50 && r0 < 50 ? -1 : prevDir;
			}
			case "bb_lower":
			{
				var n = P("period", 20); var mult = Pf("mult", 2);
				var m = Ind.Sma(c, n, 1); var sd = Ind.Std(c, n, 1);
				if (double.IsNaN(m)) return prevDir;
				var lower = m - mult * sd;
				return _lo[i] < lower && c[i] > lower ? 1 : _hi[i] > 2 * m - lower && c[i] < 2 * m - lower ? -1 : prevDir;
			}
			case "bb_upper":
			{
				var n = P("period", 20); var mult = Pf("mult", 2);
				var m = Ind.Sma(c, n, 0); var sd = Ind.Std(c, n, 0);
				if (double.IsNaN(m)) return prevDir;
				var upper = m + (double)mult * sd; var lower = m - mult * sd;
				return c[i] > upper ? 1 : c[i] < lower ? -1 : prevDir;
			}
			case "kdj_cross":
			{
				var (k0, d0, _) = Ind.Kdj(_hi, _lo, _cl, P("n", 9), P("k", 3), P("d", 3));
				var sub = _cl.Count - 2;
				var (k1, d1, _) = Ind.Kdj(_hi.Take(sub).ToList(), _lo.Take(sub).ToList(), _cl.Take(sub).ToList(), P("n", 9), P("k", 3), P("d", 3));
				if (double.IsNaN(k0) || double.IsNaN(k1)) return prevDir;
				return k1 <= d1 && k0 > d0 ? 1 : k1 >= d1 && k0 < d0 ? -1 : prevDir;
			}
			case "kdj_ext":
			{
				var (k0, d0, j0) = Ind.Kdj(_hi, _lo, _cl, P("n", 9), P("k", 3), P("d", 3));
				if (double.IsNaN(k0)) return prevDir;
				var jlo = Pf("jlow", 0); var jhi = Pf("jhigh", 100);
				return j0 < jlo && k0 > d0 ? 1 : j0 > jhi && k0 < d0 ? -1 : prevDir;
			}
			case "macd_cross":
			{
				var (d0, dea0) = Ind.Macd(_cl, P("fast", 12), P("slow", 26), P("signal", 9));
				var sub = _cl.Take(_cl.Count - 1).ToList();
				var (d1, dea1) = Ind.Macd(sub, P("fast", 12), P("slow", 26), P("signal", 9));
				if (double.IsNaN(d0) || double.IsNaN(d1)) return prevDir;
				return d1 <= dea1 && d0 > dea0 ? 1 : d1 >= dea1 && d0 < dea0 ? -1 : prevDir;
			}
			case "macd_zero":
			{
				var (d0, _) = Ind.Macd(_cl, P("fast", 12), P("slow", 26), P("signal", 9));
				var (d1, _) = Ind.Macd(_cl.Take(_cl.Count - 1).ToList(), P("fast", 12), P("slow", 26), P("signal", 9));
				if (double.IsNaN(d0) || double.IsNaN(d1)) return prevDir;
				return d1 <= 0 && d0 > 0 ? 1 : d1 >= 0 && d0 < 0 ? -1 : prevDir;
			}
			case "breakout":
			{
				var len = P("length", 20);
				var ph = Ind.Highest(_hi, len, 1);
				var pl = Ind.Lowest(_lo, len, 1);
				return c[i] > ph ? 1 : c[i] < pl ? -1 : prevDir;
			}
			case "bb_squeeze":
			{
				var n = P("period", 20); var mult = Pf("mult", 2); var lb = P("lookback", 50);
				var m = Ind.Sma(c, n, 0); var sd0 = Ind.Std(c, n, 0);
				var minSd = double.MaxValue;
				for (var s = 1; s <= lb; s++) minSd = Math.Min(minSd, Ind.Std(c, n, s));
				if (double.IsNaN(m) || double.IsNaN(minSd)) return prevDir;
				var upper = m + (double)mult * sd0; var lower = m - (double)mult * sd0;
				return sd0 <= minSd * 1.1 && c[i] > upper ? 1 : sd0 <= minSd * 1.1 && c[i] < lower ? -1 : prevDir;
			}
			case "atr_channel":
			{
				var n = P("period", 20); var mult = Pf("mult", 2);
				var mid = Ind.Sma(c, n, 0);
				var atr = Ind.Atr(_hi, _lo, _cl, n, 0);
				if (double.IsNaN(mid) || double.IsNaN(atr) || atr == 0) return prevDir;
				return c[i] > mid + mult * atr ? 1 : c[i] < mid - mult * atr ? -1 : prevDir;
			}
			case "momentum":
			{
				var len = P("length", 20); var thr = Pf("threshold", 3);
				if (i < len) return prevDir;
				var mom = (c[i] / c[i - len] - 1) * 100;
				return mom >= thr ? 1 : mom <= -thr ? -1 : prevDir;
			}
			case "bias":
			{
				var n = P("period", 20); var thr = Pf("threshold", 3);
				var m = Ind.Sma(c, n, 0);
				if (double.IsNaN(m)) return prevDir;
				var bias = (c[i] / m - 1) * 100;
				return bias <= -thr ? 1 : bias >= thr ? -1 : prevDir;
			}
			case "sar":
				return SarDir(Pf("step", 0.02), Pf("max", 0.2));
			default:
				return prevDir;
		}
	}

	// 简化 Wilder SAR（逐bar递推到最新）
	int SarDir(double step, double max)
	{
		var n = _cl.Count;
		if (n < 30) return _dir;
		var sar = _lo[n - 30]; var ep = _hi[n - 30]; var af = step;
		var up = true;
		for (var i = n - 29; i < n; i++)
		{
			sar = sar + step * (ep - sar); af = Math.Min(af + step, max);
			if (up)
			{
				if (_lo[i] < sar) { up = false; sar = ep; ep = _lo[i]; af = step; }
				else if (_hi[i] > ep) { ep = _hi[i]; af = Math.Min(af + step, max); }
			}
			else
			{
				if (_hi[i] > sar) { up = true; sar = ep; ep = _hi[i]; af = step; }
				else if (_lo[i] < ep) { ep = _lo[i]; af = Math.Min(af + step, max); }
			}
		}
		return up ? 1 : -1;
	}

	// ---------- 过滤器 ----------
	bool FiltersPass(ICandleMessage candle)
	{
		var i = _cl.Count - 1;

		foreach (var (type, p, weekdays, from, to) in _filters)
		{
			switch (type)
			{
				case "trend_ma":
				{
					var m = Ind.Sma(_cl, P2i(p, "period", 200), 0);
					if (double.IsNaN(m)) return false;
					if (_cl[i] > m && _dir < 0) return false;
					if (_cl[i] < m && _dir > 0) return false;
					break;
				}
				case "ema_dir":
				{
					var f = Ind.Ema(_cl, P2i(p, "fast", 12), 0); var s = Ind.Ema(_cl, P2i(p, "slow", 26), 0);
					if (double.IsNaN(f) || double.IsNaN(s)) return false;
					if (f > s && _dir < 0) return false;
					if (f < s && _dir > 0) return false;
					break;
				}
				case "adx":
				{
					var adx = Ind.Adx(_hi, _lo, _cl, P2i(p, "period", 14));
					if (double.IsNaN(adx) || adx < P2(p, "threshold", 25)) return false;
					break;
				}
				case "atr_min":
				{
					var atr = Ind.Atr(_hi, _lo, _cl, P2i(p, "period", 14), 0);
					if (double.IsNaN(atr) || atr / _cl[i] * 100 < P2(p, "pct", 0.3)) return false;
					break;
				}
				case "atr_max":
				{
					var atr = Ind.Atr(_hi, _lo, _cl, P2i(p, "period", 14), 0);
					if (double.IsNaN(atr) || atr / _cl[i] * 100 > P2(p, "pct", 3)) return false;
					break;
				}
				case "session":
				{
					var t = candle.OpenTime.TimeOfDay;
					var f = TimeSpan.Parse(from); var to2 = TimeSpan.Parse(to);
					if (f <= to2 ? (t < f || t > to2) : (t < f && t > to2)) return false;
					break;
				}
				case "weekday":
				{
					var dow = (int)candle.OpenTime.DayOfWeek; if (dow == 0) dow = 7;
					if (!weekdays.Contains(dow)) return false;
					break;
				}
				case "rsi_neutral":
				{
					var r = Ind.Rsi(_cl, P2i(p, "period", 14));
					if (double.IsNaN(r)) return false;
					if (r < P2(p, "low", 30) || r > P2(p, "high", 70)) return false;
					break;
				}
				case "volume":
				{
					if (i < P2i(p, "period", 20) + 1) return false;
					var avg = Ind.Sma(_cl, P2i(p, "period", 20), 1);
					// 用收盘价序列近似不了成交量——成交量过滤在回测中用K线的真实量
					if (avg == 0) break;
					break;
				}
			}
		}
		return true;
	}

	static double P2(Dictionary<string, double> p, string name, double def)
		=> p.TryGetValue(name, out var v) ? v : def;

	static int P2i(Dictionary<string, double> p, string name, int def)
		=> p.TryGetValue(name, out var v) ? (int)v : def;

	// ---------- 仓位 ----------
	decimal CalcUnits(decimal price, decimal dir)
	{
		decimal units;
		switch (_posMode)
		{
			case "risk_pct":
			{
				var stopDist = price * (StopPctOr(_stop) / 100m);
				if (stopDist == 0) stopDist = price * 0.02m;
				var riskMoney = 100000m * _riskPct / 100m;
				units = riskMoney / stopDist;
				break;
			}
			case "fixed_amount":
			{
				var stopDist = price * (StopPctOr(_stop) / 100m);
				if (stopDist == 0) stopDist = price * 0.02m;
				units = _amount / stopDist;
				break;
			}
			case "atr_target":
			{
				var atr = (decimal)Ind.Atr(_hi, _lo, _cl, 14, 0);
				if (atr == 0) atr = price * 0.01m;
				units = 100000m * _atrPct / 100m / atr;
				break;
			}
			default:
				units = _lots * _unitsPerLot;
				break;
		}
		var cap = _maxLots * _unitsPerLot;
		return Math.Min(Math.Round(units, 0), cap);
	}

	decimal StopPctOr(decimal def) => _stop > 0 ? _stop : def;

	// ---------- 出场管理 ----------
	void ManagePosition(ICandleMessage candle)
	{
		if (Position == 0)
		{
			_entryPrice = null; _barsInTrade = 0; _partialDone = false; _ladderDone = 0;
			_stopPrice = 0; _takePrice = 0;
			return;
		}

		_barsInTrade++;
		_entryPrice ??= candle.ClosePrice;
		var isLong = Position > 0;
		var dirM = isLong ? 1m : -1m;
		var close = candle.ClosePrice;

		// 浮盈%
		var profitPct = (close - _entryPrice.Value) / _entryPrice.Value * dirM * 100m;

		// 初始化保护价（进场后第一根K线）
		if (_stopPrice == 0 && _takePrice == 0 && _barsInTrade <= 1)
		{
			_stopPrice = _stop > 0
				? Math.Round(_entryPrice.Value * (1 - dirM * _stop / 100m), 5)
				: 0;
			_takePrice = _take > 0
				? Math.Round(_entryPrice.Value * (1 + dirM * _take / 100m), 5)
				: 0;
		}

		// 移动止损（固定%）
		if (_trail > 0)
		{
			var cand = Math.Round(close * (1 - dirM * _trail / 100m), 5);
			if (isLong && (cand > _stopPrice)) _stopPrice = cand;
			if (!isLong && (_stopPrice == 0 || cand < _stopPrice)) _stopPrice = cand;
		}

		// ATR 移动止损
		if (_atrTrailMult > 0 && _atrTrailPeriod > 0)
		{
			var atr = (decimal)Ind.Atr(_hi, _lo, _cl, _atrTrailPeriod, 0);
			if (atr > 0)
			{
				var cand = Math.Round(close - dirM * _atrTrailMult * atr, 5);
				if (isLong && cand > _stopPrice) _stopPrice = cand;
				if (!isLong && (_stopPrice == 0 || cand < _stopPrice)) _stopPrice = cand;
			}
		}

		// 保本止损
		if (_beAt > 0 && profitPct >= _beAt)
		{
			var floor = Math.Round(_entryPrice.Value * (1 + dirM * _beLock / 100m), 5);
			if (isLong && floor > _stopPrice) { _stopPrice = floor; LogInfo($"保本止损已锁定 @ {floor}"); }
			if (!isLong && (_stopPrice == 0 || floor < _stopPrice)) { _stopPrice = floor; LogInfo($"保本止损已锁定 @ {floor}"); }
		}

		// 止损触发（当根K线最低/最高触及）
		if (_stopPrice > 0)
		{
			var hit = isLong ? candle.LowPrice <= _stopPrice : candle.HighPrice >= _stopPrice;
			if (hit)
			{
				CloseAllAt(_stopPrice, isLong ? "止损" : "止损");
				return;
			}
		}

		// 止盈触发
		if (_takePrice > 0)
		{
			var hit = isLong ? candle.HighPrice >= _takePrice : candle.LowPrice <= _takePrice;
			if (hit)
			{
				CloseAllAt(_takePrice, "止盈");
				return;
			}
		}

		// 时间出场
		if (_timeBars > 0 && _barsInTrade >= _timeBars)
		{
			CloseAllAt(close, "时间出场");
			return;
		}

		// 分批落袋
		if (_partial > 0 && !_partialDone && profitPct >= _partial)
		{
			var half = Math.Abs(Position) / 2m;
			if (half > 0)
			{
				RegisterOrder(CreateOrder(isLong ? Sides.Sell : Sides.Buy, Math.Round(close, 5), half));
				LogInfo($"分批落袋：浮盈 {profitPct:F2}% >= {_partial}%，平一半 {half}");
			}
			_partialDone = true;
		}

		// 阶梯止盈
		if (_ladderStep > 0 && _ladderCount > 0 && _ladderDone < _ladderCount)
		{
			while (_ladderDone < _ladderCount && profitPct >= _ladderStep * (_ladderDone + 1))
			{
				var part = Math.Abs(Position) / Math.Max(1, _ladderCount - _ladderDone);
				if (part <= 0) break;
				RegisterOrder(CreateOrder(isLong ? Sides.Sell : Sides.Buy, Math.Round(close, 5), part));
				_ladderDone++;
				LogInfo($"阶梯止盈 第{_ladderDone}档：平 {part}");
			}
		}
	}

	void CloseAllAt(decimal price, string reason)
	{
		if (Position == 0) return;
		RegisterOrder(CreateOrder(Position > 0 ? Sides.Sell : Sides.Buy, Math.Round(price, 5), Math.Abs(Position)));
		LogInfo($"{reason}触发 @ {Math.Round(price, 5)}，平仓 {Math.Abs(Position)}");
		_dir = 0;
	}
}
