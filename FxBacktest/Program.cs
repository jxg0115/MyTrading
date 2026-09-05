namespace FxBacktest;

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
	private static readonly TimeSpan TF = TimeSpan.FromDays(1);

	static async Task<int> Main(string[] args)
	{
		var token = CancellationToken.None;

		// data storage: <MyTrading>\data\storage (4 levels up from bin\Release\net10.0)
		var dataPath = args.Length > 0
			? args[0]
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "storage"));

		var csvPath = args.Length > 1
			? args[1]
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "EURUSD_D1.csv"));

		Directory.CreateDirectory(dataPath);

		// ---------- 1. history data: load real CSV if present, otherwise generate demo data ----------
		List<TimeFrameCandleMessage> candles;

		if (File.Exists(csvPath))
		{
			candles = LoadCsv(csvPath);
			Console.WriteLine($"Loaded {candles.Count} REAL daily candles from CSV -> {csvPath}");
		}
		else
		{
			candles = GenerateDailyCandles(DateTime.UtcNow.Date.AddYears(-2), 730, seed: 42);
			WriteCsv(csvPath, candles);
			Console.WriteLine($"CSV not found, generated {candles.Count} DEMO daily candles -> {csvPath}");
		}

		// ---------- 2. put candles into local storage ----------
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
		Console.WriteLine($"Candles saved to local storage -> {dataPath}");

		// ---------- 3. backtest ----------
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

		var startTime = candles[0].OpenTime;
		var stopTime = candles[^1].CloseTime;

		var connector = new HistoryEmulationConnector(secProvider, [pf])
		{
			EmulationAdapter =
			{
				Settings =
				{
					MatchOnTouch = false,
				}
			},
			HistoryMessageAdapter =
			{
				StorageRegistry = storageRegistry,
			},
		};

		logManager.Sources.Add(connector);

		var strategy = new SmaCrossStrategy(fastLength: 10, slowLength: 30, volume: 10000)
		{
			Portfolio = connector.Portfolios.First(),
			Security = security,
			Connector = connector,
			ConnectorCandleType = candleType,
			LogLevel = LogLevels.Info,
		};

		logManager.Sources.Add(strategy);

		connector.HistoryMessageAdapter.StartDate = startTime;
		connector.HistoryMessageAdapter.StopDate = stopTime;

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

		Console.WriteLine();
		Console.WriteLine($"Backtesting SMA(10/30) on {SecId} daily candles, {startTime:yyyy-MM-dd} .. {stopTime:yyyy-MM-dd}");
		Console.WriteLine();

		await strategy.StartAsync();
		connector.Connect();
		await connector.StartAsync();

		finishedEvent.WaitOne();
		logManager.Dispose();
		return 0;
	}

	// ---------- data loading from MT5-exported CSV ----------
	private static List<TimeFrameCandleMessage> LoadCsv(string path)
	{
		var secId = new SecurityId { SecurityCode = "EURUSD", BoardCode = "FX" };
		var candles = new List<TimeFrameCandleMessage>();

		foreach (var line in File.ReadLines(path).Skip(1))
		{
			if (string.IsNullOrWhiteSpace(line))
				continue;

			var parts = line.Split(',');
			var c = new TimeFrameCandleMessage
			{
				SecurityId = secId,
				TypedArg = TF,
				OpenTime = DateTime.Parse(parts[0], CultureInfo.InvariantCulture),
				CloseTime = DateTime.Parse(parts[0], CultureInfo.InvariantCulture) + TF,
				OpenPrice = decimal.Parse(parts[1], CultureInfo.InvariantCulture),
				HighPrice = decimal.Parse(parts[2], CultureInfo.InvariantCulture),
				LowPrice = decimal.Parse(parts[3], CultureInfo.InvariantCulture),
				ClosePrice = decimal.Parse(parts[4], CultureInfo.InvariantCulture),
				TotalVolume = decimal.Parse(parts[5], CultureInfo.InvariantCulture),
				State = CandleStates.Finished,
			};
			candles.Add(c);
		}

		return candles;
	}

	// ---------- data generation: random walk with trend cycles ----------
	private static List<TimeFrameCandleMessage> GenerateDailyCandles(DateTime begin, int count, int seed)
	{
		var rnd = new Random(seed);
		var candles = new List<TimeFrameCandleMessage>(count);
		var price = 1.05m;
		var secId = new SecurityId { SecurityCode = "EURUSD", BoardCode = "FX" };

		// slow sinusoidal drift so SMA crossovers actually produce trades
		for (var i = 0; i < count; i++)
		{
			var t = begin.AddDays(i);
			var drift = 0.00008m * (decimal)Math.Sin(i / 30.0) + 0.00002m;
			var noise = (decimal)(rnd.NextDouble() - 0.5) * 0.008m;

			var open = price;
			var close = Math.Max(0.5m, Math.Min(2m, open + drift + noise));
			var high = Math.Max(open, close) + (decimal)rnd.NextDouble() * 0.004m;
			var low = Math.Min(open, close) - (decimal)rnd.NextDouble() * 0.004m;
			var volume = 10000m * (decimal)(1 + rnd.NextDouble());

			price = close;

			candles.Add(new TimeFrameCandleMessage
			{
				SecurityId = secId,
				TypedArg = TF,
				OpenTime = t,
				CloseTime = t + TF,
				OpenPrice = Math.Round(open, 5),
				HighPrice = Math.Round(high, 5),
				LowPrice = Math.Round(low, 5),
				ClosePrice = Math.Round(close, 5),
				TotalVolume = Math.Round(volume, 0),
				State = CandleStates.Finished,
			});
		}

		return candles;
	}

	private static void WriteCsv(string path, List<TimeFrameCandleMessage> candles)
	{
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);

		var lines = new List<string>(candles.Count + 1) { "time,open,high,low,close,volume" };

		foreach (var c in candles)
		{
			lines.Add(string.Create(CultureInfo.InvariantCulture,
				$"{c.OpenTime:yyyy-MM-dd},{c.OpenPrice},{c.HighPrice},{c.LowPrice},{c.ClosePrice},{c.TotalVolume}"));
		}

		File.WriteAllLines(path, lines);
	}
}

/// <summary>Simple SMA crossover strategy: long on fast-above-slow cross, short on the opposite.</summary>
class SmaCrossStrategy : Strategy
{
	private readonly SMA _fast;
	private readonly SMA _slow;
	private bool? _fastBelowSlow;

	public DataType ConnectorCandleType { get; set; } = TimeSpan.FromDays(1).TimeFrame();

	public SmaCrossStrategy(int fastLength, int slowLength, decimal volume)
	{
		_fast = new SMA { Length = fastLength };
		_slow = new SMA { Length = slowLength };
		Volume = volume;
	}

	protected override void OnStarted2(DateTime time)
	{
		base.OnStarted2(time);

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

		// crossover happened
		_fastBelowSlow = fastBelow;

		CancelActiveOrders();

		var direction = fastBelow ? Sides.Sell : Sides.Buy;
		var volume = Volume + Math.Abs(Position);

		var price = Security.ShrinkPrice(slowValue);
		RegisterOrder(CreateOrder(direction, price, volume));

		LogInfo($"{(fastBelow ? "Death" : "Golden")} cross @ {candle.CloseTime:yyyy-MM-dd} close={candle.ClosePrice} -> {direction} {volume}");
	}
}
