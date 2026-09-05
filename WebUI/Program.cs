// ============================================================================
// WebUI 后端：StockSharp 回测引擎 + 数据管理的 HTTP 薄壳。
// 浏览器打开 http://localhost:5000
//
// 接口一览：
//   GET  /api/candles                     初始K线（EURUSD D1）
//   POST /api/backtest                    跑回测（任意品种/周期，读 data/{品种}_{周期}.csv）
//   GET  /api/data/list                   本地数据仓库列表
//   POST /api/data/fetch                  调 MT5 拉新数据（后台任务）
//   GET  /api/data/fetchstatus            查询下载任务状态
// ============================================================================

namespace WebUI;

using System.Diagnostics;
using System.Text;
using System.Globalization;
using System.Text.Json;

using Ecng.Logging;

using StockSharp.Algo;
using StockSharp.Algo.Storages;
using StockSharp.Algo.Strategies;
using StockSharp.Algo.Testing;
using StockSharp.BusinessEntities;
using StockSharp.Configuration;
using StockSharp.Messages;

public class Program
{
	private static readonly string BaseDir =
		Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
	public static readonly string DataDir = Path.Combine(BaseDir, "data");
	private static readonly string StoragePath = Path.Combine(DataDir, "storage");
	private static readonly string FetchScript = Path.Combine(BaseDir, "Mt5Data", "fetch_mt5.py");

	// ---------- 实盘监控状态 ----------
	private static Process _liveProc;
	private static readonly string LiveStateFile = Path.Combine(DataDir, "live_status.json");
	private static readonly string LiveScript = Path.Combine(BaseDir, "LiveData", "live_worker.py");

	// ---------- 参数优化状态 ----------
	private static readonly object _optLock = new();
	private static bool _optRunning;
	private static int _optDone;
	private static int _optTotal;
	private static readonly List<object> _optResults = new();

	// ---------- 下载任务状态（同一时间只跑一个，够用） ----------
	private static readonly object _jobLock = new();
	private static bool _jobRunning;
	private static string _jobSymbol;
	private static string _jobTf;
	private static DateTime _jobStart;
	private static readonly List<string> _jobOutput = new();
	private static int _jobExitCode = -999;

	public static void Main(string[] args)
	{
		Directory.CreateDirectory(DataDir);

		var builder = WebApplication.CreateBuilder(args);
		builder.WebHost.UseUrls("http://localhost:5000");
		var app = builder.Build();

		// HTML页面禁用缓存，避免前端更新后浏览器用旧JS配新接口（必须在静态文件中间件之前）
		app.Use(async (ctx, next) =>
		{
			var path = ctx.Request.Path.Value ?? "/";
			if (path == "/" || path.EndsWith(".html"))
				ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
			await next();
		});

		app.UseDefaultFiles();
		app.UseStaticFiles();

		// ---------- 初始K线 ----------
		app.MapGet("/api/candles", () =>
		{
			var (candles, symbol, _) = LoadDataset("EURUSD", "D1");
			return Results.Json(new
			{
				security = symbol,
				timeframe = "D1",
				count = candles.Count,
				candles = candles.Select(c => new object[]
				{
					Ep(c.OpenTime),
					c.OpenPrice, c.HighPrice, c.LowPrice, c.ClosePrice, c.TotalVolume,
				}),
			});
		});

		// ---------- 回测 ----------
		// POST {"strategy":"sma"|"breakout","fast":10,"slow":30,"length":20,
		//        "volume":10000,"stop":2,"symbol":"EURUSD","tf":"D1"}
		app.MapPost("/api/backtest", async (BacktestRequest req) =>
		{
			try
			{
				return Results.Json(await RunBacktest(req));
			}
			catch (Exception ex)
			{
				return Results.Json(new { error = ex.Message });
			}
		});

		// ---------- 数据管理 ----------
		app.MapGet("/api/data/list", () =>
		{
			var items = new List<object>();

			foreach (var file in Directory.GetFiles(DataDir, "*_*.csv"))
			{
				try
				{
					var name = Path.GetFileNameWithoutExtension(file);   // e.g. EURUSD_D1
					var sep = name.LastIndexOf('_');
					var symbol = name[..sep];
					var tf = name[(sep + 1)..];
					var (count, first, last) = ScanCsv(file);
					items.Add(new
					{
						symbol,
						tf,
						count,
						range = count > 0 ? $"{first:yyyy-MM-dd} ~ {last:yyyy-MM-dd}" : "空文件",
						sizeKb = (int)(new FileInfo(file).Length / 1024),
						source = "MT5",
					});
				}
				catch { /* 跳过坏文件 */ }
			}

			return Results.Json(new { items });
		});

		app.MapPost("/api/data/fetch", (FetchRequest req) =>
		{
			lock (_jobLock)
			{
				if (_jobRunning)
					return Results.Json(new { error = "已有下载任务在运行，请稍候" }, statusCode: 409);

				_jobRunning = true;
				_jobSymbol = req.Symbol.ToUpperInvariant();
				_jobTf = req.Tf.ToUpperInvariant();
				_jobStart = DateTime.Now;
				_jobOutput.Clear();
				_jobExitCode = -999;
			}

			var psi = new ProcessStartInfo("python",
				$"\"{FetchScript}\" {_jobSymbol} {_jobTf} {req.Begin:yyyy-MM-dd}")
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			var proc = new Process { StartInfo = psi };
			proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_jobLock) _jobOutput.Add(e.Data); };
			proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_jobLock) _jobOutput.Add(e.Data); };
			proc.Start();
			proc.BeginOutputReadLine();
			proc.BeginErrorReadLine();
			proc.Exited += (_, _) =>
			{
				lock (_jobLock)
				{
					_jobExitCode = proc.ExitCode;
					_jobRunning = false;
				}
			};
			proc.EnableRaisingEvents = true;

			return Results.Json(new { started = true });
		});

		app.MapGet("/api/data/fetchstatus", () => Results.Json(new
		{
			running = _jobRunning,
			symbol = _jobSymbol,
			tf = _jobTf,
			elapsedSec = (int)(DateTime.Now - _jobStart).TotalSeconds,
			exitCode = _jobExitCode,
			output = _jobOutput.TakeLast(5),
		}));

		// ---------- 实盘监控（本地 MT5 实时数据 + 可选模拟下单） ----------
		app.MapPost("/api/live/start", (LiveRequest req) =>
		{
			if (_liveProc is { HasExited: false })
				return Results.Json(new { error = "实时监控已在运行，请先停止" }, statusCode: 409);

			try { File.Delete(LiveStateFile); } catch { }

			var args = $"{req.Symbol.ToUpperInvariant()} {req.Tf.ToUpperInvariant()} {req.Strategy} " +
					   $"{Math.Max(2, req.Fast)} {Math.Max(3, req.Slow)} {Math.Max(2, req.Length)} " +
					   $"{req.Lots} {req.Stop} {(req.AutoTrade ? 1 : 0)} " +
					   $"{req.Take} {req.Trail} {req.PartialAt}";
			var psi = new ProcessStartInfo("python", $"\"{LiveScript}\" {args}")
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			_liveProc = Process.Start(psi);
			return Results.Json(new { started = true });
		});

		app.MapPost("/api/live/stop", () =>
		{
			var killed = false;

			// 1) 本会话启动的进程
			if (_liveProc is { HasExited: false })
			{
				try { _liveProc.Kill(entireProcessTree: true); killed = true; } catch { }
			}

			// 2) 兜底：服务重启后 _liveProc 丢失，从状态文件里读引擎 PID 精准击杀
			if (File.Exists(LiveStateFile))
			{
				try
				{
					using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(LiveStateFile));
					if (doc.RootElement.TryGetProperty("pid", out var pidEl) && pidEl.TryGetInt32(out var pid))
					{
						var proc = Process.GetProcessById(pid);
						proc.Kill(entireProcessTree: true);
						killed = true;
					}
				}
				catch { /* 进程已不在 */ }
			}

			_liveProc = null;
			return Results.Json(new { stopped = true, killed });
		});

		app.MapGet("/api/live/status", () =>
		{
			var workerAlive = _liveProc is { HasExited: false };
			object state = null;
			var alive = workerAlive;

			if (File.Exists(LiveStateFile))
			{
				try
				{
					var stateEl = System.Text.Json.JsonDocument.Parse(File.ReadAllText(LiveStateFile)).RootElement;
					state = stateEl.Clone();

					// 即使服务器重启过，只要引擎心跳还在（60秒内有更新且报告running）就算存活
					if (stateEl.TryGetProperty("running", out var runningEl) && runningEl.GetBoolean()
						&& stateEl.TryGetProperty("updated_epoch", out var epEl))
					{
						var ageSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - epEl.GetInt64();
						if (ageSec < 60)
							alive = true;
					}
				}
				catch { }
			}

			return Results.Json(new { workerAlive, alive, state });
		});

		// ---------- 参数优化 ----------
		app.MapPost("/api/optimize", (OptimizeRequest req) =>
		{
			lock (_optLock)
			{
				if (_optRunning)
					return Results.Json(new { error = "优化任务已在运行" }, statusCode: 409);
			}

			var combos = new List<(int a, int b)>();
			for (var a = req.AFrom; a <= req.ATo; a += Math.Max(1, req.AStep))
				for (var b = req.BFrom; b <= req.BTo; b += Math.Max(1, req.BStep))
					combos.Add((a, b));

			// SMA 要求慢线 > 快线，过滤无效组合
			if (req.Strategy == "sma")
				combos.RemoveAll(c => c.b <= c.a);

			if (combos.Count == 0)
				return Results.Json(new { error = "没有有效的参数组合（注意：快线必须小于慢线）" });
			if (combos.Count > 100)
				return Results.Json(new { error = $"组合数 {combos.Count} 超过上限 100，请加大步长" });

			lock (_optLock)
			{
				_optRunning = true;
				_optDone = 0;
				_optTotal = combos.Count;
				_optResults.Clear();
			}

			_ = Task.Run(async () =>
			{
				foreach (var (a, b) in combos)
				{
					try
					{
						// SMA：A=快线, B=慢线；突破：A=突破天数, B=止损%
						var r = await RunBacktest(new BacktestRequest
						{
							Strategy = req.Strategy,
							Symbol = req.Symbol,
							Tf = req.Tf,
							Volume = req.Volume,
							Stop = req.Strategy == "breakout" ? b : req.Stop,
							Fast = a,
							Slow = b,
							Length = a,
						});

						var map = r.StatsMap;
						lock (_optLock)
						{
							_optResults.Add(new
							{
								a,
								b,
								netProfit = map.GetValueOrDefault("NetProfit"),
								sharpe = map.GetValueOrDefault("SharpeRatio"),
								maxDd = map.GetValueOrDefault("MaxDrawdownPercent"),
								pf = map.GetValueOrDefault("ProfitFactor"),
								trades = map.GetValueOrDefault("RoundtripCount"),
							});
						}
					}
					catch { /* 单组失败不影响整批 */ }
					lock (_optLock) _optDone++;
				}
				lock (_optLock) _optRunning = false;
			});

			return Results.Json(new { started = true, total = combos.Count });
		});

		app.MapGet("/api/optimize/status", () => Results.Json(new
		{
			running = _optRunning,
			done = _optDone,
			total = _optTotal,
			results = _optResults,
		}));

		// ---------- 策略库 ----------
		StrategyDb.Init();

		app.MapGet("/api/strategies", () => Results.Json(StrategyDb.List()));

		app.MapGet("/api/strategies/{id}", (long id) =>
		{
			var s = StrategyDb.Get(id);
			return s is null ? Results.NotFound() : Results.Json(s);
		});

		app.MapPost("/api/strategies", (StrategyRecord r) =>
		{
			if (string.IsNullOrWhiteSpace(r.Name))
				return Results.Json(new { error = "策略名称不能为空" });
			r.Id = StrategyDb.Create(r);
			return Results.Json(r);
		});

		app.MapPut("/api/strategies/{id}", (long id, StrategyRecord r) =>
		{
			r.Id = id;
			return StrategyDb.Update(r) ? Results.Json(r) : Results.NotFound();
		});

		app.MapDelete("/api/strategies/{id}", (long id) =>
			StrategyDb.Delete(id) ? Results.Json(new { deleted = true }) : Results.NotFound());

		// ---------- 策略 -> EA (MQL5) 导出 ----------
		app.MapGet("/api/strategies/{id}/ea", (long id) =>
		{
			var s = StrategyDb.Get(id);
			if (s is null)
				return Results.NotFound();

			if (s.Template != "sma" && s.Template != "breakout")
				return Results.Json(new { error = $"模板 {s.Template} 暂不支持EA导出" });

			try
			{
				var (code, fileName) = EaGenerator.Generate(s);
				return Results.Bytes(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(code)).ToArray(),
					"text/plain; charset=utf-8", fileName);
			}
			catch (Exception ex)
			{
				return Results.Json(new { error = ex.Message });
			}
		});

		// ---------- AI：接入方案管理 ----------
		AiDb.Init();

		app.MapGet("/api/ai/providers", () => Results.Json(AiDb.ListProviders()));

		app.MapPost("/api/ai/providers", (AiProvider p) =>
		{
			if (string.IsNullOrWhiteSpace(p.Name))
				return Results.Json(new { error = "名称不能为空" });
			AiDb.SaveProvider(p);
			return Results.Json(new { saved = true });
		});

		app.MapDelete("/api/ai/providers/{id}", (long id) =>
		{
			AiDb.DeleteProvider(id);
			return Results.Json(new { deleted = true });
		});

		app.MapPost("/api/ai/providers/{id}/test", async (long id) =>
		{
			var p = AiDb.ListProviders().FirstOrDefault(x => x.Id == id);
			if (p is null)
				return Results.Json(new { error = "找不到该接入方案" });
			try
			{
				var reply = await AiService.ChatAsync(p, "你是连通性测试器，回复两个字：正常",
					new List<(string, string)> { ("user", "ping") });
				return Results.Json(new { ok = true, reply });
			}
			catch (Exception ex)
			{
				return Results.Json(new { ok = false, error = ex.Message });
			}
		});

		// ---------- AI：会话与对话 ----------
		app.MapGet("/api/ai/conversations", () => Results.Json(AiDb.ListConversations()));

		app.MapDelete("/api/ai/conversations/{id}", (long id) =>
		{
			AiDb.DeleteConversation(id);
			return Results.Json(new { deleted = true });
		});

		app.MapGet("/api/ai/conversations/{id}/messages", (long id) => Results.Json(AiDb.GetMessages(id)));

		// POST {"conversationId":0或会话id,"message":"...","providerId":0用默认,"preset":"strategy|diagnose|chat"}
		app.MapPost("/api/ai/chat", async (AiChatRequest req) =>
		{
			long convId = 0;
			try
			{
				var provider = req.ProviderId > 0
					? AiDb.ListProviders().FirstOrDefault(x => x.Id == req.ProviderId)
					: AiDb.GetDefaultProvider();
				if (provider is null)
					return Results.Json(new { error = "还没有配置 AI 接入方案，请先在[AI管理]里添加" });

				convId = req.ConversationId > 0 ? req.ConversationId : AiDb.CreateConversation("新会话");

				// 组装历史（最近20条）
				var history = new List<(string role, string content)>();
				foreach (var m in AiDb.GetMessages(convId).TakeLast(20))
					history.Add((m.Role, m.Content));
				history.Add(("user", req.Message));

				var system = AiService.EngineSpec + "\n\n当前任务模式：" + (req.Preset ?? "strategy");

				var raw = await AiService.ChatAsync(provider, system, history);
				var parsed = AiService.ParseReply(raw);

				AiDb.AddMessage(convId, "user", req.Message);
				AiDb.AddMessage(convId, "assistant", JsonSerializer.Serialize(new
				{
					reply = parsed.Reply,
					card = parsed.Card,
					code = parsed.Code,
					advice = parsed.Advice,
					risks = parsed.Risks,
				}));

				return Results.Json(new
				{
					conversationId = convId,
					reply = parsed.Reply,
					card = parsed.Card,
					code = parsed.Code,
					advice = parsed.Advice,
					risks = parsed.Risks,
				});
			}
			catch (Exception ex)
			{
				return Results.Json(new { error = ex.Message, conversationId = convId });
			}
		});

		// ---------- AI：流式对话（SSE，打字机效果） ----------
		app.MapPost("/api/ai/chat/stream", async (AiChatRequest req, HttpContext http) =>
		{
			http.Response.ContentType = "text/event-stream; charset=utf-8";
			http.Response.Headers.CacheControl = "no-cache";

			async Task Sse(object payload)
			{
				await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(payload) + "\n\n");
				await http.Response.Body.FlushAsync();
			}

			long convId = 0;
			try
			{
				var provider = req.ProviderId > 0
					? AiDb.ListProviders().FirstOrDefault(x => x.Id == req.ProviderId)
					: AiDb.GetDefaultProvider();
				if (provider is null)
				{
					await Sse(new { error = "还没有配置 AI 接入方案，请先在[AI管理]里添加" });
					return;
				}

				convId = req.ConversationId > 0 ? req.ConversationId : AiDb.CreateConversation("新会话");

				var history = new List<(string role, string content)>();
				foreach (var m in AiDb.GetMessages(convId).TakeLast(20))
					history.Add((m.Role, m.Content));
				history.Add(("user", req.Message));

				var system = AiService.EngineSpec + "\n\n当前任务模式：" + (req.Preset ?? "strategy");

				// Anthropic 流式协议不同：先走非流式，一次性吐出
				if (provider.Protocol == "anthropic")
				{
					var full = await AiService.ChatAsync(provider, system, history);
					var parsedAll = AiService.ParseReply(full);
					await Sse(new { delta = parsedAll.Reply });
					AiDb.AddMessage(convId, "user", req.Message);
					AiDb.AddMessage(convId, "assistant", JsonSerializer.Serialize(new
					{
						reply = parsedAll.Reply, card = parsedAll.Card, code = parsedAll.Code,
						advice = parsedAll.Advice, risks = parsedAll.Risks,
					}));
					await Sse(new
					{
						done = true,
						conversationId = convId,
						reply = parsedAll.Reply,
						card = parsedAll.Card,
						code = parsedAll.Code,
						advice = parsedAll.Advice,
						risks = parsedAll.Risks,
					});
					return;
				}

				// OpenAI 兼容流式
				var url = provider.BaseUrl.TrimEnd('/') + "/chat/completions";
				var messages = new List<object> { new { role = "system", content = system } };
				foreach (var (role, content) in history)
					messages.Add(new { role, content });

				var body = JsonSerializer.Serialize(new
				{
					model = provider.Model,
					messages,
					temperature = provider.Temperature,
					stream = true,
				});

				using var aiReq = new HttpRequestMessage(HttpMethod.Post, url);
				aiReq.Content = new StringContent(body, Encoding.UTF8, "application/json");
				if (!string.IsNullOrEmpty(provider.ApiKey))
					aiReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);

				using var aiResp = await AiService.Http.SendAsync(aiReq, HttpCompletionOption.ResponseHeadersRead);
				if (!aiResp.IsSuccessStatusCode)
				{
					var errText = await aiResp.Content.ReadAsStringAsync();
					await Sse(new { error = $"AI接口错误 {(int)aiResp.StatusCode}: {AiService.Truncate(errText, 300)}" });
					return;
				}

				var sb = new StringBuilder();
				using var stream = await aiResp.Content.ReadAsStreamAsync();
				using var reader = new StreamReader(stream);

				while (!reader.EndOfStream)
				{
					var line = await reader.ReadLineAsync();
					if (line is null) break;
					if (!line.StartsWith("data: ")) continue;

					var data = line[6..].Trim();
					if (data == "[DONE]") break;

					try
					{
						using var doc = JsonDocument.Parse(data);
						var delta = doc.RootElement
							.GetProperty("choices")[0]
							.GetProperty("delta")
							.TryGetProperty("content", out var c) ? c.GetString() : null;

						if (!string.IsNullOrEmpty(delta))
						{
							sb.Append(delta);
							await Sse(new { delta });
						}
					}
					catch { /* 跳过无法解析的行 */ }
				}

				var raw = sb.ToString();
				var parsed = AiService.ParseReply(raw);

				AiDb.AddMessage(convId, "user", req.Message);
				AiDb.AddMessage(convId, "assistant", JsonSerializer.Serialize(new
				{
					reply = parsed.Reply,
					card = parsed.Card,
					code = parsed.Code,
					advice = parsed.Advice,
					risks = parsed.Risks,
				}));

				await Sse(new
				{
					done = true,
					conversationId = convId,
					reply = parsed.Reply,
					card = parsed.Card,
					code = parsed.Code,
					advice = parsed.Advice,
					risks = parsed.Risks,
				});
			}
			catch (Exception ex)
			{
				await Sse(new { error = ex.Message, conversationId = convId });
			}
		});

		app.Run();
	}

		// K线图时间：转成Unix秒（图表库盘中周期要求）；UTC kind保持界面显示与CSV墙钟一致
	private static long Ep(DateTime t) =>
		new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeSeconds();

	// 合约大小：外汇主流品种1手=100,000基准货币；黄金1手=100盎司
	private static decimal ContractSize(string symbol) =>
		symbol == "XAUUSD" ? 100m : symbol == "XAGUSD" ? 5000m : 100000m;

	// ---------- 回测执行 ----------
	private static async Task<BacktestResult> RunBacktest(BacktestRequest req)
	{
		var symbol = (req.Symbol ?? "EURUSD").ToUpperInvariant();
		var tfName = (req.Tf ?? "D1").ToUpperInvariant();
		var (candles, _, tf) = LoadDataset(symbol, tfName);

		if (candles.Count == 0)
			throw new InvalidOperationException($"本地没有 {symbol} {tfName} 数据，请先到[数据]页下载");

		// 手数语义：req.Volume 单位是 lot（1手=100,000基准单位），换算成引擎内部单位
		var volume = Math.Max(0.01m, req.Volume <= 0 ? 1 : req.Volume);   // lot 语义（1手=合约大小基准单位）

		Strategy strategy = req.Strategy switch
		{
			"breakout" => new BreakoutStrategy(Math.Max(2, req.Length), volume)
			{
				StopPercent = req.Stop ?? 0m,
				TakePercent = req.Take,
				TrailPercent = req.Trail,
				PartialAt = req.PartialAt,
				ConnectorCandleType = DataType.TimeFrame(tf),
			},
			_ => new SmaCrossStrategy(Math.Max(2, req.Fast), Math.Max(3, req.Slow), volume)
			{
				StopPercent = req.Stop ?? 0m,
				TakePercent = req.Take,
				TrailPercent = req.Trail,
				PartialAt = req.PartialAt,
				ConnectorCandleType = DataType.TimeFrame(tf),
			},
		};

		var exchangeInfoProvider = new InMemoryExchangeInfoProvider();
		var board = exchangeInfoProvider.GetOrCreateBoard("FX");

		var security = new Security
		{
			Id = $"{symbol}@FX",
			Code = symbol,
			Board = board,
			PriceStep = 0.00001m,
			Decimals = 5,
		};

		var secId = security.ToSecurityId();
		var storageRegistry = new StorageRegistry
		{
			DefaultDrive = new LocalMarketDataDrive(StoragePath),
		};

		var candleType = DataType.TimeFrame(tf);
		var storage = storageRegistry.GetCandleMessageStorage(secId, candleType, storageRegistry.DefaultDrive, StorageFormats.Binary);

		await storage.DeleteAsync(candles[0].OpenTime, candles[^1].CloseTime, default);
		await storage.SaveAsync(candles, default);

		var logManager = new LogManager();

		var level1Info = new Level1ChangeMessage
		{
			SecurityId = secId,
			ServerTime = candles[0].OpenTime,
		}
		.TryAdd(Level1Fields.MinPrice, 0.0001m)
		.TryAdd(Level1Fields.MaxPrice, 100000m);

		var secProvider = (ISecurityProvider)new CollectionSecurityProvider([security]);
		var pf = Portfolio.CreateSimulator();
		pf.CurrentValue = 100000;

		var connector = new HistoryEmulationConnector(secProvider, [pf])
		{
			HistoryMessageAdapter = { StorageRegistry = storageRegistry },
		};

		strategy.Portfolio = connector.Portfolios.First();
		strategy.Security = security;
		strategy.Connector = connector;
		strategy.LogLevel = LogLevels.Error;

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
			finishedEvent.Set();
		};

		await strategy.StartAsync();
		connector.Connect();
		await connector.StartAsync();

		finishedEvent.WaitOne(TimeSpan.FromMinutes(3));   // 保险丝
		logManager.Dispose();

		// ---------- 收集结果 ----------
		var contract = ContractSize(symbol);
		var trades = strategy.MyTrades
			.OrderBy(t => t.Trade.ServerTime)
			.Select(t => new
			{
				time = Ep(t.Trade.ServerTime),
				side = t.Order.Side == Sides.Buy ? "buy" : "sell",
				price = t.Trade.Price,
				volume = Math.Round(t.Trade.Volume / contract, 2),
				pnl = t.PnL,
			})
			.ToList();

		var stats = strategy.StatisticManager.Parameters
			.Select(p => new { name = p.Name, value = p.Value?.ToString() })
			.ToList();

		var statsMap = stats.ToDictionary(s => s.name, s => s.value ?? "");

		var equity = new List<object> { new { time = Ep(candles[0].OpenTime), value = 100000m } };
		decimal cum = 100000;
		foreach (var t in strategy.MyTrades.OrderBy(x => x.Trade.ServerTime))
		{
			if (t.PnL is decimal pnl)
				cum += pnl;
			equity.Add(new { time = Ep(t.Trade.ServerTime), value = cum });
		}

		return new BacktestResult
		{
			Strategy = req.Strategy,
			Symbol = symbol,
			Tf = tfName,
			Candles = candles.Select(c => new object[]
			{
				Ep(c.OpenTime),
				c.OpenPrice, c.HighPrice, c.LowPrice, c.ClosePrice, c.TotalVolume,
			}),
			Trades = trades,
			Stats = stats,
			StatsMap = statsMap,
			Equity = equity,
		};
	}

	public class BacktestResult
	{
		public string Strategy { get; set; }
		public string Symbol { get; set; }
		public string Tf { get; set; }
		public IEnumerable<object> Candles { get; set; }
		public object Trades { get; set; }
		public object Stats { get; set; }
		public Dictionary<string, string> StatsMap { get; set; }
		public object Equity { get; set; }
	}

	// ---------- 数据集加载（带缓存：文件没变就不重读） ----------
	private static readonly Dictionary<string, (List<TimeFrameCandleMessage> candles, DateTime cachedAt)> _datasetCache = new();

	private static (List<TimeFrameCandleMessage>, string, TimeSpan) LoadDataset(string symbol, string tfName)
	{
		var tf = ParseTf(tfName);
		var path = Path.Combine(DataDir, $"{symbol}_{tfName}.csv");
		var key = $"{symbol}_{tfName}";

		if (!File.Exists(path))
			throw new InvalidOperationException($"找不到数据文件 {path}，请先到[数据]页下载");

		var mtime = File.GetLastWriteTime(path);
		if (_datasetCache.TryGetValue(key, out var cached) && cached.cachedAt == mtime)
			return (cached.candles, symbol, tf);

		var candles = LoadCsv(path, tf, symbol);
		_datasetCache[key] = (candles, mtime);
		return (candles, symbol, tf);
	}

	private static TimeSpan ParseTf(string tfName) => tfName.ToUpperInvariant() switch
	{
		"M1" => TimeSpan.FromMinutes(1),
		"M5" => TimeSpan.FromMinutes(5),
		"M15" => TimeSpan.FromMinutes(15),
		"M30" => TimeSpan.FromMinutes(30),
		"H1" => TimeSpan.FromHours(1),
		"H4" => TimeSpan.FromHours(4),
		"W1" => TimeSpan.FromDays(7),
		_ => TimeSpan.FromDays(1),
	};

	private static (int count, DateTime first, DateTime last) ScanCsv(string path)
	{
		DateTime first = default, last = default;
		var count = 0;

		foreach (var line in File.ReadLines(path).Skip(1))
		{
			if (string.IsNullOrWhiteSpace(line))
				continue;

			var t = DateTime.Parse(line.Split(',')[0], CultureInfo.InvariantCulture);
			if (count == 0)
				first = t;
			last = t;
			count++;
		}

		return (count, first, last);
	}

	private static List<TimeFrameCandleMessage> LoadCsv(string path, TimeSpan tf, string code)
	{
		var secId = new SecurityId { SecurityCode = code, BoardCode = "FX" };
		var candles = new List<TimeFrameCandleMessage>();

		foreach (var line in File.ReadLines(path).Skip(1))
		{
			if (string.IsNullOrWhiteSpace(line))
				continue;

			var p = line.Split(',');
			candles.Add(new TimeFrameCandleMessage
			{
				SecurityId = secId,
				TypedArg = tf,
				OpenTime = DateTime.Parse(p[0], CultureInfo.InvariantCulture),
				CloseTime = DateTime.Parse(p[0], CultureInfo.InvariantCulture) + tf,
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

public class BacktestRequest
{
	public string Strategy { get; set; } = "sma";
	public int Fast { get; set; } = 10;
	public int Slow { get; set; } = 30;
	public int Length { get; set; } = 20;
	public decimal Volume { get; set; } = 10000;
	public decimal? Stop { get; set; } = 0m;
	public decimal Take { get; set; } = 0m;
	public decimal Trail { get; set; } = 0m;
	public decimal PartialAt { get; set; } = 0m;
	public string Symbol { get; set; } = "EURUSD";
	public string Tf { get; set; } = "D1";
}

public class FetchRequest
{
	public string Symbol { get; set; } = "EURUSD";
	public string Tf { get; set; } = "D1";
	public DateTime Begin { get; set; } = new(2015, 1, 1);
}

public class LiveRequest
{
	public string Symbol { get; set; } = "EURUSD";
	public string Tf { get; set; } = "D1";
	public string Strategy { get; set; } = "sma";
	public int Fast { get; set; } = 10;
	public int Slow { get; set; } = 30;
	public int Length { get; set; } = 20;
	public double Lots { get; set; } = 0.1;
	public double Stop { get; set; } = 2;
	public double Take { get; set; } = 0;
	public double Trail { get; set; } = 0;
	public double PartialAt { get; set; } = 0;
	public bool AutoTrade { get; set; } = false;
}

public class OptimizeRequest
{
	public string Strategy { get; set; } = "sma";
	public string Symbol { get; set; } = "EURUSD";
	public string Tf { get; set; } = "D1";
	public decimal Volume { get; set; } = 10000;
	public decimal Stop { get; set; } = 2;
	public int AFrom { get; set; } = 5;
	public int ATo { get; set; } = 30;
	public int AStep { get; set; } = 5;
	public int BFrom { get; set; } = 20;
	public int BTo { get; set; } = 60;
	public int BStep { get; set; } = 10;
}

public class AiChatRequest
{
	public long ConversationId { get; set; }
	public string Message { get; set; } = "";
	public long ProviderId { get; set; }
	public string? Preset { get; set; }
}
