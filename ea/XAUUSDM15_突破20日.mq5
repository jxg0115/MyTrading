//+------------------------------------------------------------------+
//| XAUUSDM15 突破 20日
//| 由 MyTrading 工作台自动生成 · 模板: N日突破(20)
//| 出场: 止损2% 止盈4% 移动止损0% 分批落袋2% · 手数1lot
//+------------------------------------------------------------------+
#property copyright "MyTrading Workbench"
#property version   "1.00"

#include <Trade\Trade.mqh>

input long   InpMagic     = 666001; // 魔术号
input int    InpLength    = 20;    // 突破回看天数(K线数)
input double InpLots      = 1;    // 每笔手数
input double InpStopPct   = 2;    // 固定止损 %
input double InpTakePct   = 4;    // 固定止盈 % (0=关闭)
input double InpTrailPct  = 0;    // 移动止损 % (0=关闭)
input double InpPartialPct= 2;    // 分批落袋 % (0=关闭)

CTrade trade;

datetime lastBar = 0;
ulong partialDoneTicket = 0;

int OnInit()
{
	trade.SetExpertMagicNumber(InpMagic);
	return INIT_SUCCEEDED;
}

bool NewBar()
{
	datetime t = iTime(_Symbol, _Period, 0);
	if (t != lastBar) { lastBar = t; return true; }
	return false;
}

// 找到本EA的持仓
bool MyPosition(ulong &ticket, long &type, double &volume, double &entry)
{
	for (int i = PositionsTotal() - 1; i >= 0; i--)
	{
		ulong tk = PositionGetTicket(i);
		if (tk == 0) continue;
		if (PositionGetString(POSITION_SYMBOL) != _Symbol) continue;
		if (PositionGetInteger(POSITION_MAGIC) != InpMagic) continue;
		ticket = tk;
		type   = PositionGetInteger(POSITION_TYPE);
		volume = PositionGetDouble(POSITION_VOLUME);
		entry  = PositionGetDouble(POSITION_PRICE_OPEN);
		return true;
	}
	return false;
}

void CloseAllMine()
{
	for (int i = PositionsTotal() - 1; i >= 0; i--)
	{
		ulong tk = PositionGetTicket(i);
		if (tk == 0) continue;
		if (PositionGetString(POSITION_SYMBOL) != _Symbol) continue;
		if (PositionGetInteger(POSITION_MAGIC) != InpMagic) continue;
		trade.PositionClose(tk);
	}
}

double NormVol(double v)
{
	double step = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_STEP);
	double minv = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
	double maxv = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MAX);
	v = MathFloor(v / step) * step;
	if (v < minv) v = 0;
	if (v > maxv) v = maxv;
	return v;
}

void OpenTrade(bool isBuy)
{
	double ask = SymbolInfoDouble(_Symbol, SYMBOL_ASK);
	double bid = SymbolInfoDouble(_Symbol, SYMBOL_BID);
	double price = isBuy ? ask : bid;
	double sl = 0, tp = 0;
	if (InpStopPct > 0) sl = isBuy ? price*(1-InpStopPct/100.0) : price*(1+InpStopPct/100.0);
	if (InpTakePct > 0) tp = isBuy ? price*(1+InpTakePct/100.0) : price*(1-InpTakePct/100.0);
	sl = NormalizeDouble(sl, _Digits);
	tp = NormalizeDouble(tp, _Digits);
	if (isBuy) trade.Buy (InpLots, _Symbol, 0, sl, tp, "MyTrading");
	else       trade.Sell(InpLots, _Symbol, 0, sl, tp, "MyTrading");
}

void OnTick()
{
	// ---- 移动止损：只朝有利方向移动 ----
	if (InpTrailPct > 0)
	{
		ulong tk; long type; double vol, entry;
		if (MyPosition(tk, type, vol, entry))
		{
			bool isBuy = (type == POSITION_TYPE_BUY);
			double refPrice = isBuy ? SymbolInfoDouble(_Symbol, SYMBOL_BID) : SymbolInfoDouble(_Symbol, SYMBOL_ASK);
			double newSl = NormalizeDouble(isBuy ? refPrice*(1-InpTrailPct/100.0) : refPrice*(1+InpTrailPct/100.0), _Digits);
			double curSl = PositionGetDouble(POSITION_SL);
			bool better = (curSl == 0) || (isBuy && newSl > curSl) || (!isBuy && newSl < curSl);
			if (better) trade.PositionModify(tk, newSl, PositionGetDouble(POSITION_TP));
		}
	}

	// ---- 分批落袋：浮盈达到阈值平一半 ----
	if (InpPartialPct > 0)
	{
		ulong tk; long type; double vol, entry;
		if (MyPosition(tk, type, vol, entry) && tk != partialDoneTicket)
		{
			bool isBuy = (type == POSITION_TYPE_BUY);
			double refPrice = isBuy ? SymbolInfoDouble(_Symbol, SYMBOL_BID) : SymbolInfoDouble(_Symbol, SYMBOL_ASK);
			double profitPct = (refPrice - entry)/entry * (isBuy ? 100.0 : -100.0);
			if (profitPct >= InpPartialPct)
			{
				double half = NormVol(vol/2.0);
				if (half > 0) { trade.PositionClosePartial(tk, half); partialDoneTicket = tk; }
			}
		}
	}

	// ---- 信号：只在收盘K线（新bar）判断 ----
	if (!NewBar()) return;

	bool crossUp = false, crossDn = false;
	string signalText = "";

	int  hiIdx = iHighest(_Symbol, _Period, MODE_HIGH, InpLength, 1);
	int  loIdx = iLowest (_Symbol, _Period, MODE_LOW,  InpLength, 1);
	double prevHigh = iHigh(_Symbol, _Period, hiIdx);
	double prevLow  = iLow (_Symbol, _Period, loIdx);
	double close1   = iClose(_Symbol, _Period, 1);
	crossUp = close1 > prevHigh;
	crossDn = close1 < prevLow;
	signalText = StringFormat("突破 %g>高%g / 跌%g", close1, prevHigh, prevLow);

	ulong tk; long type; double vol, entry;
	bool hasPos = MyPosition(tk, type, vol, entry);

	if (crossUp && (!hasPos || type == POSITION_TYPE_SELL))
	{
		CloseAllMine();
		OpenTrade(true);
		partialDoneTicket = 0;
	}
	else if (crossDn && (!hasPos || type == POSITION_TYPE_BUY))
	{
		CloseAllMine();
		OpenTrade(false);
		partialDoneTicket = 0;
	}
}
