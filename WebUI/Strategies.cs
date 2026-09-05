// ============================================================================
// 策略库 v2（回退版）：ExitManagedStrategy 公共出场基类
// + SMA 均线交叉 / N日突破 两个模板策略。
// 出场积木：固定止损/止盈、移动止损、分批落袋（回测端支持）。
// ============================================================================
namespace WebUI;

using System;

using StockSharp.Algo.Indicators;
using StockSharp.Algo.Strategies;
using StockSharp.Messages;

/// <summary>
/// 公共出场管理基类：止盈/止损/移动止损/分批落袋。
/// </summary>
abstract class ExitManagedStrategy : Strategy
{
	public DataType ConnectorCandleType { get; set; } = TimeSpan.FromDays(1).TimeFrame();
	public decimal StopPercent { get; set; }
	public decimal TakePercent { get; set; }
	public decimal TrailPercent { get; set; }
	public decimal PartialAt { get; set; }

	private decimal? _entryPrice;
	private bool _partialDone;

	protected void SetupProtection()
	{
		var hasTake = TakePercent > 0;
		var hasStop = StopPercent > 0;

		if (!hasTake && !hasStop)
			return;

		StartProtection(
			takeProfit: hasTake ? new Unit(TakePercent, UnitTypes.Percent) : null,
			stopLoss: hasStop ? new Unit(StopPercent, UnitTypes.Percent) : null);
	}

	protected void ManagePartial(ICandleMessage candle)
	{
		if (PartialAt <= 0)
		{
			_entryPrice = null;
			_partialDone = false;
			return;
		}

		if (Position == 0)
		{
			_entryPrice = null;
			_partialDone = false;
			return;
		}

		_entryPrice ??= candle.ClosePrice;

		if (_partialDone)
			return;

		var dir = Position > 0 ? 1m : -1m;
		var profitPct = (candle.ClosePrice - _entryPrice.Value) / _entryPrice.Value * dir * 100m;

		if (profitPct >= PartialAt)
		{
			var half = Math.Abs(Position) / 2m;
			if (half > 0)
			{
				var side = Position > 0 ? Sides.Sell : Sides.Buy;
				RegisterOrder(CreateOrder(side, Math.Round(candle.ClosePrice, 5), half));
				LogInfo($"分批落袋：浮盈 {profitPct:F2}% >= {PartialAt}%，平掉一半 {half} 手落袋");
			}
			_partialDone = true;
		}
	}
}

/// <summary>SMA 均线交叉：快线上穿慢线做多，下穿做空。</summary>
class SmaCrossStrategy : ExitManagedStrategy
{
	private readonly SMA _fast;
	private readonly SMA _slow;
	private bool? _fastBelowSlow;

	public int FastLength => _fast.Length;
	public int SlowLength => _slow.Length;

	public SmaCrossStrategy(int fastLength, int slowLength, decimal volume)
	{
		_fast = new SMA { Length = fastLength };
		_slow = new SMA { Length = slowLength };
		Volume = volume;
	}

	protected override void OnStarted2(DateTime time)
	{
		base.OnStarted2(time);
		SetupProtection();

		SubscribeCandles(ConnectorCandleType)
			.Bind(_fast, _slow, OnProcess)
			.Start();
	}

	private void OnProcess(ICandleMessage candle, decimal fastValue, decimal slowValue)
	{
		if (candle.State != CandleStates.Finished)
			return;

		if (!IsFormedAndOnlineAndAllowTrading())
			return;

		var fastBelow = fastValue < slowValue;

		if (_fastBelowSlow is null)
		{
			_fastBelowSlow = fastBelow;
			return;
		}

		if (_fastBelowSlow == fastBelow)
			return;

		_fastBelowSlow = fastBelow;

		CancelActiveOrders();

		// 目标仓位法：金叉目标 +Volume，死叉目标 -Volume，只补差额
		var target = fastBelow ? -Volume : Volume;
		var diff = target - Position;

		if (diff == 0)
			return;

		var direction = diff > 0 ? Sides.Buy : Sides.Sell;
		var price = Math.Round(slowValue, 5);
		RegisterOrder(CreateOrder(direction, price, Math.Abs(diff)));
	}
}

/// <summary>突破策略：收盘价突破 N 日最高做多，跌破 N 日最低做空。</summary>
class BreakoutStrategy : ExitManagedStrategy
{
	private readonly Highest _highest;
	private readonly Lowest _lowest;
	private decimal? _prevHigh;
	private decimal? _prevLow;

	public int BreakoutLength { get; }

	public BreakoutStrategy(int breakoutLength, decimal volume)
	{
		BreakoutLength = breakoutLength;
		Volume = volume;
		_highest = new Highest { Length = breakoutLength };
		_lowest = new Lowest { Length = breakoutLength };
	}

	protected override void OnStarted2(DateTime time)
	{
		base.OnStarted2(time);
		SetupProtection();

		SubscribeCandles(ConnectorCandleType)
			.Bind(_highest, _lowest, OnProcess)
			.Start();
	}

	private void OnProcess(ICandleMessage candle, decimal highestValue, decimal lowestValue)
	{
		if (candle.State != CandleStates.Finished)
			return;

		if (!(_highest.IsFormed && _lowest.IsFormed && _prevHigh is not null && _prevLow is not null
			&& IsFormedAndOnlineAndAllowTrading()))
		{
			_prevHigh = highestValue;
			_prevLow = lowestValue;
			return;
		}

		decimal? target = candle.ClosePrice > _prevHigh
			? Volume
			: candle.ClosePrice < _prevLow
				? -Volume
				: null;

		if (target is not null)
		{
			CancelActiveOrders();

			var diff = target.Value - Position;

			if (diff != 0)
			{
				var direction = diff > 0 ? Sides.Buy : Sides.Sell;
				var price = Math.Round(candle.ClosePrice, 5);
				RegisterOrder(CreateOrder(direction, price, Math.Abs(diff)));
			}
		}

		_prevHigh = highestValue;
		_prevLow = lowestValue;
	}
}
