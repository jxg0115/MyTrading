// ============================================================================
// EA生成器：把策略库里的策略（模板+参数）转成 MQL5 源码。
// 生成逻辑与工作台回测一一对应：同信号、目标仓位法、止损/止盈/移动止损/分批落袋。
// ============================================================================
namespace WebUI;

using System.Text;

public static class EaGenerator
{
	public static (string code, string fileName) Generate(StrategyRecord s)
	{
		var p = System.Text.Json.JsonDocument.Parse(
			string.IsNullOrWhiteSpace(s.ParamsJson) ? "{}" : s.ParamsJson).RootElement;

		double Get(string name, double def) =>
			p.TryGetProperty(name, out var el) && el.TryGetDouble(out var v) ? v : def;

		var lots = Get("volume", 1);
		var stop = Get("stop", 2);
		var take = Get("take", 0);
		var trail = Get("trail", 0);
		var partial = Get("partial", 0);

		string signalBlock;
		string inputs;
		string desc;

		if (s.Template == "breakout")
		{
			var length = (int)Get("length", 20);
			inputs = $"input int    InpLength    = {length};    // 突破回看天数(K线数)";
			desc = $"N日突破({length})";
			signalBlock = """
				int  hiIdx = iHighest(_Symbol, _Period, MODE_HIGH, InpLength, 1);
				int  loIdx = iLowest (_Symbol, _Period, MODE_LOW,  InpLength, 1);
				double prevHigh = iHigh(_Symbol, _Period, hiIdx);
				double prevLow  = iLow (_Symbol, _Period, loIdx);
				double close1   = iClose(_Symbol, _Period, 1);
				crossUp = close1 > prevHigh;
				crossDn = close1 < prevLow;
				signalText = StringFormat("突破 %g>高%g / 跌%g", close1, prevHigh, prevLow);
				""";
		}
		else
		{
			var fast = (int)Get("fast", 10);
			var slow = (int)Get("slow", 30);
			inputs = $"input int    InpFast      = {fast};     // 快线周期\ninput int    InpSlow      = {slow};     // 慢线周期";
			desc = $"SMA均线交叉({fast}/{slow})";
			signalBlock = """
				double f1 = Buf(hFast, 1), f2 = Buf(hFast, 2);
				double s1 = Buf(hSlow, 1), s2 = Buf(hSlow, 2);
				if (f1 == EMPTY_VALUE || s1 == EMPTY_VALUE || f2 == EMPTY_VALUE || s2 == EMPTY_VALUE) return;
				crossUp = (f2 <= s2) && (f1 > s1);
				crossDn = (f2 >= s2) && (f1 < s1);
				signalText = StringFormat("均线 %g/%g", f1, s1);
				""";
		}

		var handles = s.Template == "breakout"
			? ""
			: "\thFast = iMA(_Symbol, PERIOD_CURRENT, InpFast, 0, MODE_SMA, PRICE_CLOSE);\n\thSlow = iMA(_Symbol, PERIOD_CURRENT, InpSlow, 0, MODE_SMA, PRICE_CLOSE);\n\tif (hFast == INVALID_HANDLE || hSlow == INVALID_HANDLE) return INIT_FAILED;\n";
		var handlesDecl = s.Template == "breakout"
			? ""
			: "int hFast = INVALID_HANDLE, hSlow = INVALID_HANDLE;\n";
		var hasIndicators = s.Template != "breakout";

		var safeName = s.Name.Replace("\\", "_").Replace("/", "_").Replace(":", "_").Replace("\"", "_");

		var code = new StringBuilder();
		code.AppendLine("//+------------------------------------------------------------------+");
		code.AppendLine($"//| {safeName}");
		code.AppendLine($"//| 由 MyTrading 工作台自动生成 · 模板: {desc}");
		code.AppendLine($"//| 出场: 止损{stop}% 止盈{take}% 移动止损{trail}% 分批落袋{partial}% · 手数{lots}lot");
		code.AppendLine("//+------------------------------------------------------------------+");
		code.AppendLine("#property copyright \"MyTrading Workbench\"");
		code.AppendLine("#property version   \"1.00\"");
		code.AppendLine("");
		code.AppendLine("#include <Trade\\Trade.mqh>");
		code.AppendLine("");
		code.AppendLine("input long   InpMagic     = 666001; // 魔术号");
		code.AppendLine(inputs);
		code.AppendLine("input double InpLots      = " + lots.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";    // 每笔手数");
		code.AppendLine("input double InpStopPct   = " + stop.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";    // 固定止损 %");
		code.AppendLine("input double InpTakePct   = " + take.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";    // 固定止盈 % (0=关闭)");
		code.AppendLine("input double InpTrailPct  = " + trail.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";    // 移动止损 % (0=关闭)");
		code.AppendLine("input double InpPartialPct= " + partial.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";    // 分批落袋 % (0=关闭)");
		code.AppendLine("");
		code.AppendLine("CTrade trade;");
		code.AppendLine(handlesDecl);
		code.AppendLine("datetime lastBar = 0;");
		code.AppendLine("ulong partialDoneTicket = 0;");
		code.AppendLine("");
		code.AppendLine("int OnInit()");
		code.AppendLine("{");
		code.AppendLine("\ttrade.SetExpertMagicNumber(InpMagic);");
		if (handles != "") code.AppendLine(handles);
		code.AppendLine("\treturn INIT_SUCCEEDED;");
		code.AppendLine("}");
		code.AppendLine("");
		code.AppendLine("bool NewBar()");
		code.AppendLine("{");
		code.AppendLine("\tdatetime t = iTime(_Symbol, _Period, 0);");
		code.AppendLine("\tif (t != lastBar) { lastBar = t; return true; }");
		code.AppendLine("\treturn false;");
		code.AppendLine("}");
		code.AppendLine("");
		if (hasIndicators)
		{
			code.AppendLine("double Buf(int handle, int shift)");
			code.AppendLine("{");
			code.AppendLine("\tdouble b[1];");
			code.AppendLine("\tif (CopyBuffer(handle, 0, shift, 1, b) != 1) return EMPTY_VALUE;");
			code.AppendLine("\treturn b[0];");
			code.AppendLine("}");
			code.AppendLine("");
		}
		code.AppendLine("// 找到本EA的持仓");
		code.AppendLine("bool MyPosition(ulong &ticket, long &type, double &volume, double &entry)");
		code.AppendLine("{");
		code.AppendLine("\tfor (int i = PositionsTotal() - 1; i >= 0; i--)");
		code.AppendLine("\t{");
		code.AppendLine("\t\tulong tk = PositionGetTicket(i);");
		code.AppendLine("\t\tif (tk == 0) continue;");
		code.AppendLine("\t\tif (PositionGetString(POSITION_SYMBOL) != _Symbol) continue;");
		code.AppendLine("\t\tif (PositionGetInteger(POSITION_MAGIC) != InpMagic) continue;");
		code.AppendLine("\t\tticket = tk;");
		code.AppendLine("\t\ttype   = PositionGetInteger(POSITION_TYPE);");
		code.AppendLine("\t\tvolume = PositionGetDouble(POSITION_VOLUME);");
		code.AppendLine("\t\tentry  = PositionGetDouble(POSITION_PRICE_OPEN);");
		code.AppendLine("\t\treturn true;");
		code.AppendLine("\t}");
		code.AppendLine("\treturn false;");
		code.AppendLine("}");
		code.AppendLine("");
		code.AppendLine("void CloseAllMine()");
		code.AppendLine("{");
		code.AppendLine("\tfor (int i = PositionsTotal() - 1; i >= 0; i--)");
		code.AppendLine("\t{");
		code.AppendLine("\t\tulong tk = PositionGetTicket(i);");
		code.AppendLine("\t\tif (tk == 0) continue;");
		code.AppendLine("\t\tif (PositionGetString(POSITION_SYMBOL) != _Symbol) continue;");
		code.AppendLine("\t\tif (PositionGetInteger(POSITION_MAGIC) != InpMagic) continue;");
		code.AppendLine("\t\ttrade.PositionClose(tk);");
		code.AppendLine("\t}");
		code.AppendLine("}");
		code.AppendLine("");
		code.AppendLine("double NormVol(double v)");
		code.AppendLine("{");
		code.AppendLine("\tdouble step = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_STEP);");
		code.AppendLine("\tdouble minv = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);");
		code.AppendLine("\tdouble maxv = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MAX);");
		code.AppendLine("\tv = MathFloor(v / step) * step;");
		code.AppendLine("\tif (v < minv) v = 0;");
		code.AppendLine("\tif (v > maxv) v = maxv;");
		code.AppendLine("\treturn v;");
		code.AppendLine("}");
		code.AppendLine("");
		code.AppendLine("void OpenTrade(bool isBuy)");
		code.AppendLine("{");
		code.AppendLine("\tdouble ask = SymbolInfoDouble(_Symbol, SYMBOL_ASK);");
		code.AppendLine("\tdouble bid = SymbolInfoDouble(_Symbol, SYMBOL_BID);");
		code.AppendLine("\tdouble price = isBuy ? ask : bid;");
		code.AppendLine("\tdouble sl = 0, tp = 0;");
		code.AppendLine("\tif (InpStopPct > 0) sl = isBuy ? price*(1-InpStopPct/100.0) : price*(1+InpStopPct/100.0);");
		code.AppendLine("\tif (InpTakePct > 0) tp = isBuy ? price*(1+InpTakePct/100.0) : price*(1-InpTakePct/100.0);");
		code.AppendLine("\tsl = NormalizeDouble(sl, _Digits);");
		code.AppendLine("\ttp = NormalizeDouble(tp, _Digits);");
		code.AppendLine("\tif (isBuy) trade.Buy (InpLots, _Symbol, 0, sl, tp, \"MyTrading\");");
		code.AppendLine("\telse       trade.Sell(InpLots, _Symbol, 0, sl, tp, \"MyTrading\");");
		code.AppendLine("}");
		code.AppendLine("");
		code.AppendLine("void OnTick()");
		code.AppendLine("{");
		code.AppendLine("\t// ---- 移动止损：只朝有利方向移动 ----");
		code.AppendLine("\tif (InpTrailPct > 0)");
		code.AppendLine("\t{");
		code.AppendLine("\t\tulong tk; long type; double vol, entry;");
		code.AppendLine("\t\tif (MyPosition(tk, type, vol, entry))");
		code.AppendLine("\t\t{");
		code.AppendLine("\t\t\tbool isBuy = (type == POSITION_TYPE_BUY);");
		code.AppendLine("\t\t\tdouble refPrice = isBuy ? SymbolInfoDouble(_Symbol, SYMBOL_BID) : SymbolInfoDouble(_Symbol, SYMBOL_ASK);");
		code.AppendLine("\t\t\tdouble newSl = NormalizeDouble(isBuy ? refPrice*(1-InpTrailPct/100.0) : refPrice*(1+InpTrailPct/100.0), _Digits);");
		code.AppendLine("\t\t\tdouble curSl = PositionGetDouble(POSITION_SL);");
		code.AppendLine("\t\t\tbool better = (curSl == 0) || (isBuy && newSl > curSl) || (!isBuy && newSl < curSl);");
		code.AppendLine("\t\t\tif (better) trade.PositionModify(tk, newSl, PositionGetDouble(POSITION_TP));");
		code.AppendLine("\t\t}");
		code.AppendLine("\t}");
		code.AppendLine("");
		code.AppendLine("\t// ---- 分批落袋：浮盈达到阈值平一半 ----");
		code.AppendLine("\tif (InpPartialPct > 0)");
		code.AppendLine("\t{");
		code.AppendLine("\t\tulong tk; long type; double vol, entry;");
		code.AppendLine("\t\tif (MyPosition(tk, type, vol, entry) && tk != partialDoneTicket)");
		code.AppendLine("\t\t{");
		code.AppendLine("\t\t\tbool isBuy = (type == POSITION_TYPE_BUY);");
		code.AppendLine("\t\t\tdouble refPrice = isBuy ? SymbolInfoDouble(_Symbol, SYMBOL_BID) : SymbolInfoDouble(_Symbol, SYMBOL_ASK);");
		code.AppendLine("\t\t\tdouble profitPct = (refPrice - entry)/entry * (isBuy ? 100.0 : -100.0);");
		code.AppendLine("\t\t\tif (profitPct >= InpPartialPct)");
		code.AppendLine("\t\t\t{");
		code.AppendLine("\t\t\t\tdouble half = NormVol(vol/2.0);");
		code.AppendLine("\t\t\t\tif (half > 0) { trade.PositionClosePartial(tk, half); partialDoneTicket = tk; }");
		code.AppendLine("\t\t\t}");
		code.AppendLine("\t\t}");
		code.AppendLine("\t}");
		code.AppendLine("");
		code.AppendLine("\t// ---- 信号：只在收盘K线（新bar）判断 ----");
		code.AppendLine("\tif (!NewBar()) return;");
		code.AppendLine("");
		code.AppendLine("\tbool crossUp = false, crossDn = false;");
		code.AppendLine("\tstring signalText = \"\";");
		code.AppendLine("");
		code.AppendLine("\t" + signalBlock.Replace("\n", "\n\t"));
		code.AppendLine("");
		code.AppendLine("\tulong tk; long type; double vol, entry;");
		code.AppendLine("\tbool hasPos = MyPosition(tk, type, vol, entry);");
		code.AppendLine("");
		code.AppendLine("\tif (crossUp && (!hasPos || type == POSITION_TYPE_SELL))");
		code.AppendLine("\t{");
		code.AppendLine("\t\tCloseAllMine();");
		code.AppendLine("\t\tOpenTrade(true);");
		code.AppendLine("\t\tpartialDoneTicket = 0;");
		code.AppendLine("\t}");
		code.AppendLine("\telse if (crossDn && (!hasPos || type == POSITION_TYPE_BUY))");
		code.AppendLine("\t{");
		code.AppendLine("\t\tCloseAllMine();");
		code.AppendLine("\t\tOpenTrade(false);");
		code.AppendLine("\t\tpartialDoneTicket = 0;");
		code.AppendLine("\t}");
		code.AppendLine("}");

		var fileName = MakeFileName(safeName);
		return (code.ToString(), fileName + ".mq5");
	}

	private static string MakeFileName(string name)
	{
		var sb = new StringBuilder();
		foreach (var c in name)
		{
			if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
			else sb.Append('_');
		}
		return sb.Length == 0 ? "MyStrategy" : sb.ToString();
	}
}
