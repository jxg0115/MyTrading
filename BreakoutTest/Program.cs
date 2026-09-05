// ============================================================================
//  教学项目：20日突破策略（Breakout Test）
//
//  策略逻辑（一句话）：
//    价格突破最近20天最高价 -> 做多（趋势开始，追进去）
//    价格跌破最近20天最低价 -> 做空
//    固定 2% 止损保护每一笔仓位
//
//  这个思路来自经典的海龟交易法则，是趋势跟踪的入门策略。
//
//  整个文件按策略的四大件组织：
//    [1] 看什么  -> 订阅K线
//    [2] 算什么  -> Highest / Lowest 指标
//    [3] 什么时候动手 -> OnProcess 里的条件判断 + 下单
//    [4] 怎么保命 -> StartProtection 止损
// ============================================================================

namespace BreakoutTest;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Ecng.Logging;

using StockSharp.Algo;
using StockSharp.Algo.Indicators;
using StockSharp.Algo.Storages;
using StockSharp.Algo.Strategies;
using StockSharp.Algo.Testing;
using StockSharp.BusinessEntities;
using StockSharp.Configuration;
using StockSharp.Messages;

class Program
{
	private const string SecId = "EURUSD@FX";
	private static readonly TimeSpan TF = TimeSpan.FromDays(1);   // 用日线：1天1根K线

	static async Task<int> Main(string[] args)
	{
		var token = CancellationToken.None;

		// 和 FxBacktest 一样：优先读真实数据 CSV（MT5 导出的）
		var dataPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "storage"));
		var csvPath = args.Length > 0
			? args[0]
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "EURUSD_D1.csv"));

		Directory.CreateDirectory(dataPath);

		// ---------- [0] 准备数据：读 CSV，没有就报错退出（真实数据是研究的前提） ----------
		if (!File.Exists(csvPath))
		{
			Console.WriteLine($"找不到数据文件: {csvPath}");
			Console.WriteLine("先运行 python ../Mt5Data/fetch_mt5.py EURUSD D1 拉取真实数据");
			return 1;
		}

		var candles = LoadCsv(csvPath);
		Console.WriteLine($"Loaded {candles.Count} REAL daily candles ({candles[0].OpenTime:yyyy-MM-dd} .. {candles[^1].OpenTime:yyyy-MM-dd})");

		// ---------- [0.5] 数据存入 StockSharp 本地存储（回测引擎从这里读） ----------
		var exchangeInfoProvider = new InMemoryExchangeInfoProvider();
		var board = exchangeInfoProvider.GetOrCreateBoard("FX");

		var security = new Security
		{
			Id = SecId,
			Code = "EURUSD",
			Board = board,
			PriceStep = 0.00001m,
			Decimals = 5,
		};

		var secId = security.ToSecurityId();
		var storageRegistry = new StorageRegistry
		{
			DefaultDrive = new LocalMarketDataDrive(dataPath),
		};

		var candleType = DataType.TimeFrame(TF);
		var storage = storageRegistry.GetCandleMessageStorage(secId, candleType, storageRegistry.DefaultDrive, StorageFormats.Binary);
		await storage.SaveAsync(candles, token);

		// ---------- 回测环境搭建（照抄即可，不是策略的重点） ----------
		var logManager = new LogManager();
		logManager.Listeners.Add(new ConsoleLogListener());

		var level1Info = new Level1ChangeMessage
		{
			SecurityId = secId,
			ServerTime = candles[0].OpenTime,
		}
		.TryAdd(Level1Fields.MinPrice, 0.0001m)
		.TryAdd(Level1Fields.MaxPrice, 10m);

		var secProvider = (ISecurityProvider)new CollectionSecurityProvider([security]);
		var pf = Portfolio.CreateSimulator();
		pf.CurrentValue = 100000;

		var connector = new HistoryEmulationConnector(secProvider, [pf])
		{
			HistoryMessageAdapter = { StorageRegistry = storageRegistry },
		};
		logManager.Sources.Add(connector);

		// ---------- 创建策略实例：这里是你要改参数的地方 ----------
		var strategy = new BreakoutStrategy(breakoutLength: 20, volume: 10000)
		{
			Portfolio = connector.Portfolios.First(),
			Security = security,
			Connector = connector,
			ConnectorCandleType = candleType,
			LogLevel = LogLevels.Info,
			StopLossPercent = 2m,     // 每笔仓位最大亏 2% 就认赔
		};

		logManager.Sources.Add(strategy);

		connector.HistoryMessageAdapter.StartDate = candles[0].OpenTime;
		connector.HistoryMessageAdapter.StopDate = candles[^1].CloseTime;

		var finishedEvent = new ManualResetEvent(false);

		connector.SecurityReceived += (sub, s) =>
		{
			if (s != security)
				return;
			_ = connector.EmulationAdapter.SendInMessageAsync(level1Info, default);
		};

		connector.StateChanged2 += async state =>
		{
			if (state != ChannelStates.Stopped)
				return;

			await strategy.StopAsync();

			Console.WriteLine();
			Console.WriteLine("=== Backtest completed ===");
			foreach (var p in strategy.StatisticManager.Parameters)
				Console.WriteLine($"  {p.Name}: {p.Value}");

			finishedEvent.Set();
		};

		Console.WriteLine($"\nBacktesting breakout({strategy.BreakoutLength}) on {SecId} daily, stop={strategy.StopLossPercent}%\n");

		await strategy.StartAsync();
		connector.Connect();
		await connector.StartAsync();

		finishedEvent.WaitOne();
		logManager.Dispose();
		return 0;
	}

	// ---------- 从 MT5 导出的 CSV 读取K线 ----------
	private static List<TimeFrameCandleMessage> LoadCsv(string path)
	{
		var secId = new SecurityId { SecurityCode = "EURUSD", BoardCode = "FX" };
		var candles = new List<TimeFrameCandleMessage>();

		foreach (var line in File.ReadLines(path).Skip(1))   // 跳过表头行
		{
			if (string.IsNullOrWhiteSpace(line))
				continue;

			var p = line.Split(',');
			candles.Add(new TimeFrameCandleMessage
			{
				SecurityId = secId,
				TypedArg = TF,
				OpenTime = DateTime.Parse(p[0], CultureInfo.InvariantCulture),
				CloseTime = DateTime.Parse(p[0], CultureInfo.InvariantCulture) + TF,
				OpenPrice = decimal.Parse(p[1], CultureInfo.InvariantCulture),
				HighPrice = decimal.Parse(p[2], CultureInfo.InvariantCulture),
				LowPrice = decimal.Parse(p[3], CultureInfo.InvariantCulture),
				ClosePrice = decimal.Parse(p[4], CultureInfo.InvariantCulture),
				TotalVolume = decimal.Parse(p[5], CultureInfo.InvariantCulture),
				State = CandleStates.Finished,
			});
		}

		return candles;
	}
}

// ============================================================================
//  策略本体：突破最近 N 天最高/最低价就追涨/追跌
//  —— 换任何策略，改的都是这一个类
// ============================================================================
class BreakoutStrategy : Strategy
{
	// [2] 算什么：引擎自带"最高价/最低价"指标，Length 表示回看多少根K线
	private readonly Highest _highest;
	private readonly Lowest _lowest;

	// 用上根K线的突破位（不含当前K线），避免"自己突破自己"的作弊
	private decimal? _prevHigh;
	private decimal? _prevLow;

	// 可调参数，回测时改这几个数字
	public int BreakoutLength { get; }
	public decimal StopLossPercent { get; set; } = 2m;
	public DataType ConnectorCandleType { get; set; } = TimeSpan.FromDays(1).TimeFrame();

	public BreakoutStrategy(int breakoutLength, decimal volume)
	{
		BreakoutLength = breakoutLength;
		Volume = volume;
		_highest = new Highest { Length = breakoutLength };
		_lowest = new Lowest { Length = breakoutLength };
	}

	// [1] 看什么 + [4] 怎么保命，都在策略启动时设置
	protected override void OnStarted2(DateTime time)
	{
		base.OnStarted2(time);

		// 止损：从开仓价算，亏 2% 自动平仓（引擎自动挂单，不用手写）
		StartProtection(
			takeProfit: null,                                       // 不设止盈，让利润奔跑
			stopLoss: new Unit(StopLossPercent, UnitTypes.Percent));

		// 订阅K线，每根完整K线算出"过去N天最高/最低"，喂给 OnProcess
		SubscribeCandles(ConnectorCandleType)
			.Bind(_highest, _lowest, OnProcess)
			.Start();
	}

	// [3] 什么时候动手：引擎每收完一根K线就调用一次
	private void OnProcess(ICandleMessage candle, decimal highestValue, decimal lowestValue)
	{
		// 只处理已完结的K线（未走完的K线价格还会变，不能当信号）
		if (candle.State != CandleStates.Finished)
			return;

		// 指标需要 N 根K线才能算出有效值（20日突破，前20天没有信号）
		if (_highest.IsFormed && _lowest.IsFormed && _prevHigh is not null && _prevLow is not null
			&& IsFormedAndOnlineAndAllowTrading())
		{
			// ---- 核心交易逻辑：只有这几行 ----
			//
			// 【重要教训】新手常见写法是"下单手数 = 基础手数 + 当前仓位"来反转仓位，
			// 但限价单可能延迟成交、信号可能连续多天重复触发，仓位会像 1,2,4,8 一样翻倍。
			// 正确姿势是"目标仓位法"：先想清楚策略此刻"应该持有多少仓"，再补差额。

			// 1) 目标仓位：突破上沿 -> 持多 +Volume；跌破下沿 -> 持空 -Volume
			var targetPosition = candle.ClosePrice > _prevHigh
				? Volume
				: candle.ClosePrice < _prevLow
					? -Volume
					: (decimal?)null;

			if (targetPosition is not null)
			{
				// 2) 撤掉还没成交的旧挂单，避免旧单干扰
				CancelActiveOrders();

				// 3) 只补差额：目标仓位 - 当前仓位 = 还需要买/卖多少
				var diff = targetPosition.Value - Position;

				if (diff != 0)
				{
					var direction = diff > 0 ? Sides.Buy : Sides.Sell;
					var price = Security.ShrinkPrice(candle.ClosePrice);
					RegisterOrder(CreateOrder(direction, price, Math.Abs(diff)));
					LogInfo($"{(targetPosition > 0 ? "BREAK UP" : "BREAK DOWN")} {candle.OpenTime:yyyy-MM-dd} close={candle.ClosePrice} pos={Position} -> target {targetPosition} (order {direction} {Math.Abs(diff)})");
				}
			}
		}

		// 记录本轮突破位，供下一根K线比较
		_prevHigh = highestValue;
		_prevLow = lowestValue;
	}
}
