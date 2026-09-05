// ============================================================================
// AI 服务：统一调用 OpenAI 兼容协议 / Anthropic 协议，附引擎系统提示词。
// ============================================================================
namespace WebUI;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public static class AiService
{
	public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

	// ---------- 引擎系统提示词：积木清单 + 输出格式 ----------
	public const string EngineSpec = """
		你是量化策略设计师，工作在 MyTrading 本地工作台（积木引擎：C# + StockSharp + MT5 数据）。
		用户描述策略想法，你输出"积木配置"JSON。

		## 积木目录（只能用这些）

		### 入场 entry.type（18种）
		sma_cross{fast,slow} | ema_cross{fast,slow} | price_cross_ma{period} | triple_ma{p1,p2,p3}
		rsi_reversal{period,enter,exit} | rsi_trend{period} | kdj_cross{n,k,d} | kdj_ext{n,k,d,jlow,jhigh}
		macd_cross{fast,slow,signal} | macd_zero{fast,slow,signal} | breakout{length} | bb_lower{period,mult}
		bb_upper{period,mult} | bb_squeeze{period,mult,lookback} | atr_channel{period,mult} | momentum{length,threshold}
		bias{period,threshold} | sar{step,max}

		### 过滤器 filters[]（可叠加，AND生效；全部写0参数也可）
		trend_ma{period} | ema_dir{fast,slow} | adx{period,threshold} | atr_min{period,pct} | atr_max{period,pct}
		session{from,to}(服务器时间) | weekday{weekdays:[1~5]} | rsi_neutral{period,low,high} | volume{period,mult}

		### 出场 exit（全部填0表示关闭）
		stop(固定止损%) | take(固定止盈%) | trail(移动止损%) | partial(分批落袋%)
		breakeven_at(保本触发%)+breakeven_lock(锁定%) | atr_trail_period+atr_trail_mult(ATR移动止损)
		time_bars(持仓N根K线强制平) | ladder_step+ladder_count(阶梯止盈)

		### 仓位 position
		mode: fixed{lots} | risk_pct{risk_pct,max_lots} | fixed_amount{amount,max_lots} | atr_target{atr_pct,max_lots}

		## 输出格式（严格JSON）
		{"reply":"给用户的回复",
		 "card":{"name":"","entry":{"type":"","params":{}},"filters":[],"exit":{},"position":{},"symbol":"","tf":"","notes":""},
		 "code":"配置JSON字符串","advice":["建议"],"risks":["风险"]}

		规则：
		- 不需要策略时 card=null，code=""
		- 马丁/逆势加仓 明确不支持，必须拒绝并解释爆仓风险
		- 主动建议过滤器（如趋势过滤）和出场组合
		- advice 至少2条（含参数优化建议范围），risks 至少1条
		- 用中文回复
		""";

	// ---------- 各协议调用 ----------
	public static async Task<string> ChatAsync(AiProvider p, string system, List<(string role, string content)> history)
	{
		return p.Protocol switch
		{
			"anthropic" => await ChatAnthropic(p, system, history),
			_ => await ChatOpenAiCompatible(p, system, history),
		};
	}

	private static async Task<string> ChatOpenAiCompatible(AiProvider p, string system, List<(string role, string content)> history)
	{
		var url = p.BaseUrl.TrimEnd('/') + "/chat/completions";
		var messages = new List<object> { new { role = "system", content = system } };
		foreach (var (role, content) in history)
			messages.Add(new { role, content });

		var body = JsonSerializer.Serialize(new
		{
			model = p.Model,
			messages,
			temperature = p.Temperature,
		});

		using var req = new HttpRequestMessage(HttpMethod.Post, url);
		req.Content = new StringContent(body, Encoding.UTF8, "application/json");
		if (!string.IsNullOrEmpty(p.ApiKey))
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", p.ApiKey);

		var resp = await Http.SendAsync(req);
		var text = await resp.Content.ReadAsStringAsync();
		if (!resp.IsSuccessStatusCode)
			throw new InvalidOperationException($"AI接口错误 {(int)resp.StatusCode}: {Truncate(text, 300)}");

		using var doc = JsonDocument.Parse(text);
		return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
	}

	private static async Task<string> ChatAnthropic(AiProvider p, string system, List<(string role, string content)> history)
	{
		var url = p.BaseUrl.TrimEnd('/') + "/v1/messages";
		var messages = history.Select(m => new { role = m.role, content = m.content });

		var body = JsonSerializer.Serialize(new
		{
			model = p.Model,
			max_tokens = 4096,
			system,
			messages,
		});

		using var req = new HttpRequestMessage(HttpMethod.Post, url);
		req.Content = new StringContent(body, Encoding.UTF8, "application/json");
		req.Headers.Add("x-api-key", p.ApiKey);
		req.Headers.Add("anthropic-version", "2023-06-01");

		var resp = await Http.SendAsync(req);
		var text = await resp.Content.ReadAsStringAsync();
		if (!resp.IsSuccessStatusCode)
			throw new InvalidOperationException($"AI接口错误 {(int)resp.StatusCode}: {Truncate(text, 300)}");

		using var doc = JsonDocument.Parse(text);
		return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
	}

	public static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

	// ---------- 解析结构化回复 ----------
	public class AiPanelReply
	{
		public string Reply { get; set; } = "";
		public JsonElement? Card { get; set; }
		public string Code { get; set; } = "";
		public List<string> Advice { get; set; } = new();
		public List<string> Risks { get; set; } = new();
		public string Raw { get; set; } = "";
	}

	public static AiPanelReply ParseReply(string raw)
	{
		var result = new AiPanelReply { Raw = raw };

		// 去掉可能的 markdown 包裹
		var text = raw.Trim();
		if (text.StartsWith("```"))
		{
			var firstNl = text.IndexOf('\n');
			if (firstNl > 0)
				text = text[(firstNl + 1)..];
			if (text.TrimEnd().EndsWith("```"))
				text = text.TrimEnd()[^3..].EndsWith("```") ? text.TrimEnd()[..^3] : text;
		}

		var jsonStart = text.IndexOf('{');
		var jsonEnd = text.LastIndexOf('}');
		if (jsonStart < 0 || jsonEnd <= jsonStart)
		{
			result.Reply = raw;   // 不是JSON就当纯聊天
			return result;
		}

		try
		{
			using var doc = JsonDocument.Parse(text[jsonStart..(jsonEnd + 1)]);
			var root = doc.RootElement;

			if (root.TryGetProperty("reply", out var reply))
				result.Reply = reply.GetString() ?? raw;

			if (root.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object)
			{
				result.Card = card.Clone();
				if (root.TryGetProperty("code", out var code))
					result.Code = code.GetString() ?? "";
			}

			if (root.TryGetProperty("advice", out var advice) && advice.ValueKind == JsonValueKind.Array)
				result.Advice = advice.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList();

			if (root.TryGetProperty("risks", out var risks) && risks.ValueKind == JsonValueKind.Array)
				result.Risks = risks.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s != "").ToList();
		}
		catch
		{
			result.Reply = raw;
		}

		return result;
	}
}
