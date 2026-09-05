// ============================================================================
// MyTrading 量化工作台 前端（回退稳定版 v22 重建）
// 视图：回测工作台 / 数据 / 实盘监控 / 策略库 / 策略工坊(AI)
// ============================================================================
"use strict";

let candleSeries, equitySeries, chart, eqChart;
let curSymbol = "EURUSD", curTf = "D1";
let strategiesCache = [], selStrategy = null;

// ---------- 页面加载 ----------
window.addEventListener("DOMContentLoaded", async () => {
  const res = await fetch("/api/candles");
  const data = await res.json();
  curSymbol = data.security.split("@")[0];
  curTf = data.timeframe;
  document.getElementById("dataInfo").textContent =
    `数据: ${curSymbol} ${curTf} · ${data.count} 根真实K线`;

  initCharts(data.candles);

  document.querySelectorAll(".tab[data-view]").forEach(tab => {
    tab.addEventListener("click", () => switchView(tab.dataset.view));
  });

  document.getElementById("strategy").addEventListener("change", (e) => {
    const id = +e.target.value;
    const s = strategiesCache.find(x => x.id === id);
    if (s) applyStrategyRecord(s, false);
  });
  await refreshStrategySelect();

  document.getElementById("runBtn").addEventListener("click", runBacktest);
  document.getElementById("fetchBtn").addEventListener("click", startFetch);

  // 恢复实盘监控状态
  fetch("/api/live/status").then(r => r.json()).then(r => {
    if (r.alive && !liveTimer) {
      setLiveUI(true);
      liveTimer = setInterval(pollLive, 3000);
      pollLive();
    }
  }).catch(() => {});
});

// ---------- 视图切换 ----------
function switchView(name) {
  document.querySelectorAll(".tab[data-view]").forEach(t =>
    t.classList.toggle("active", t.dataset.view === name));
  ["backtest", "data", "live", "strategy", "ai"].forEach(v =>
    document.getElementById(`view-${v}`).classList.toggle("hidden", v !== name));
  if (name === "data") refreshDataList();
  if (name === "strategy") refreshStrategies();
  if (name === "ai") autoLoadLatestConv();
  if (name === "live") {
    refreshLiveStrategySelect();
    fetch("/api/live/status").then(r => r.json()).then(r => {
      if (r.alive && !liveTimer) {
        setLiveUI(true);
        liveTimer = setInterval(pollLive, 3000);
        pollLive();
      }
    }).catch(() => {});
  }
}

// ---------- 图表 ----------
function initCharts(candles) {
  const common = {
    layout: { background: { color: "#1e222d" }, textColor: "#787b86" },
    grid: { vertLines: { color: "#2a2e39" }, horzLines: { color: "#2a2e39" } },
    timeScale: { borderColor: "#2a2e39" },
    rightPriceScale: { borderColor: "#2a2e39" },
  };

  chart = LightweightCharts.createChart(document.getElementById("candleChart"), {
    ...common, height: document.getElementById("candleChart").clientHeight,
  });
  candleSeries = chart.addCandlestickSeries({
    upColor: "#26a69a", downColor: "#ef5350",
    borderVisible: false, wickUpColor: "#26a69a", wickDownColor: "#ef5350",
  });
  setCandles(candles);

  eqChart = LightweightCharts.createChart(document.getElementById("equityChart"), {
    ...common, height: document.getElementById("equityChart").clientHeight,
  });
  equitySeries = eqChart.addAreaSeries({
    lineColor: "#2962ff", topColor: "rgba(41,98,255,.35)", bottomColor: "rgba(41,98,255,.02)",
    lineWidth: 2,
  });

  const fit = () => {
    chart.applyOptions({ width: document.getElementById("candleChart").clientWidth });
    eqChart.applyOptions({ width: document.getElementById("equityChart").clientWidth });
    chart.timeScale().fitContent();
    eqChart.timeScale().fitContent();
  };
  window.addEventListener("resize", fit);
  setTimeout(fit, 50);
}

function setCandles(candles) {
  candleSeries.setData(candles.map(c => ({
    time: +c[0], open: c[1], high: c[2], low: c[3], close: c[4],
  })));
  chart.addLineSeries({ color: "#f7b32b", lineWidth: 1 }).setData(sma(candles, 10));
  chart.addLineSeries({ color: "#4da3ff", lineWidth: 1 }).setData(sma(candles, 30));
  chart.timeScale().fitContent();
}

function sma(candles, len) {
  const out = [];
  let sum = 0;
  for (let i = 0; i < candles.length; i++) {
    sum += candles[i][4];
    if (i >= len) sum -= candles[i - len][4];
    if (i >= len - 1) out.push({ time: +candles[i][0], value: sum / len });
  }
  return out;
}

// ---------- 回测 ----------
async function runBacktest() {
  const btn = document.getElementById("runBtn");
  btn.disabled = true;
  document.getElementById("progress").classList.remove("hidden");

  if (!selStrategy) {
    alert("请先在策略下拉中选择一个策略（可到策略库新建）");
    btn.disabled = false;
    document.getElementById("progress").classList.add("hidden");
    return;
  }

  const body = {
    strategy: selStrategy.template,
    symbol: document.getElementById("symbol").value.toUpperCase().trim(),
    tf: document.getElementById("tf").value,
    volume: +document.getElementById("volume").value || 1,
    stop: +document.getElementById("stop").value,
    take: +document.getElementById("take").value,
    trail: +document.getElementById("trail").value,
    partial: +document.getElementById("partial").value,
    fast: +document.getElementById("fast").value,
    slow: +document.getElementById("slow").value,
    length: +document.getElementById("length").value,
  };

  try {
    const res = await fetch("/api/backtest", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    const r = await res.json();
    if (r.error) { alert("回测失败: " + r.error); return; }
    curSymbol = r.symbol; curTf = r.tf;
    lastBacktest = { symbol: r.symbol, tf: r.tf, stats: r.stats };

    document.getElementById("chartSymbol").textContent = r.symbol;
    document.getElementById("chartTf").textContent = r.tf + (r.tf === "D1" ? " 日线" : " 线");
    document.getElementById("dataInfo").textContent =
      `数据: ${r.symbol} ${r.tf} · ${r.candles.length} 根真实K线`;

    chart.remove();
    eqChart.remove();
    document.getElementById("candleChart").innerHTML = "";
    document.getElementById("equityChart").innerHTML = "";
    initCharts(r.candles);

    render(r);
  } finally {
    btn.disabled = false;
    document.getElementById("progress").classList.add("hidden");
  }
}

let lastBacktest = null;

function render(r) {
  const markers = r.trades
    .filter(t => t.volume !== 0)
    .map(t => ({
      time: t.time,
      position: t.side === "buy" ? "belowBar" : "aboveBar",
      color: t.side === "buy" ? "#26a69a" : "#ef5350",
      shape: t.side === "buy" ? "arrowUp" : "arrowDown",
      text: `${t.side === "buy" ? "买" : "卖"} ${t.price}`,
    }))
    .sort((a, b) => a.time - b.time);
  candleSeries.setMarkers(markers);

  equitySeries.setData(r.equity.map(e => ({ time: +e.time, value: +e.value })));
  eqChart.timeScale().fitContent();

  // 成绩单：核心指标大数字 + 分组明细
  const get = n => { const p = r.stats.find(s => s.name === n); return p ? parseFloat(p.value) : null; };
  const net = get("NetProfit"), netPct = get("NetProfitPercent");
  const win = get("WinningTrades") ?? 0, loss = get("LossingTrades") ?? 0;
  const total = win + loss, winRate = total ? (win / total * 100) : 0;

  function row(name, v, cls) {
    if (v == null) return "";
    const text = Math.abs(v) >= 1000 ? v.toLocaleString("en-US", { maximumFractionDigits: 0 })
               : (Math.abs(v) < 10 ? v.toFixed(2) : v.toFixed(1));
    return `<tr><td class="muted">${zh(name)}</td><td class="${cls ?? ""}">${text}</td></tr>`;
  }

  document.getElementById("statsBody").innerHTML = `
    <div class="statHero ${net >= 0 ? "pos" : "neg"}">${net == null ? "—" : net.toLocaleString("en-US", { maximumFractionDigits: 0 })}</div>
    <div class="statHeroLabel">净利润（${netPct ?? 0}%）</div>
    <div class="statGrid">
      <div class="statTile"><span class="muted">夏普比率</span><b>${fmt("SharpeRatio", get("SharpeRatio"))}</b></div>
      <div class="statTile"><span class="muted">最大回撤</span><b class="neg">${fmt("MaxDrawdownPercent", get("MaxDrawdownPercent"))}%</b></div>
      <div class="statTile"><span class="muted">盈利因子</span><b>${fmt("ProfitFactor", get("ProfitFactor"))}</b></div>
      <div class="statTile"><span class="muted">胜率</span><b>${winRate.toFixed(0)}% <span class="muted">(${win}胜/${loss}负)</span></b></div>
    </div>
    <h4 class="muted" style="margin-top:10px">盈利分析</h4>
    <table class="statTable">
      ${row("总盈利", get("GrossProfit"), "pos")}${row("总亏损", get("GrossLoss"), "neg")}
      ${row("平均盈利", get("AverageWinTrade"))}${row("平均亏损", get("AverageLossTrade"))}
      ${row("期望收益/笔", get("Expectancy"))}${row("手续费", get("Commission"))}
    </table>
    <h4 class="muted" style="margin-top:10px">交易统计</h4>
    <table class="statTable">
      ${row("往返交易数", get("RoundtripCount"))}${row("成交笔数", get("TradeCount"))}
      ${row("收益比", get("Return"))}${row("复本因子", get("RecoveryFactor"))}
    </table>`;

  document.getElementById("tradeCount").textContent =
    ` · ${r.trades.length} 笔成交（${r.stats.find(s => s.name === "RoundtripCount")?.value ?? "?"} 个来回）`;

  const tb = document.querySelector("#tradesTable tbody");
  tb.innerHTML = r.trades.map(t => {
    const pnlCls = t.pnl == null ? "" : (t.pnl >= 0 ? "pos" : "neg");
    const pnlText = t.pnl == null ? "—" : (+t.pnl).toFixed(2);
    const tstr = new Date(t.time * 1000).toLocaleString("zh-CN", { hour12: false });
    return `<tr>
      <td>${tstr}</td>
      <td class="${t.side}">${t.side === "buy" ? "买" : "卖"}</td>
      <td>${(+t.price).toFixed(2)}</td>
      <td>${t.volume} 手</td>
      <td class="${pnlCls}">${pnlText}</td>
    </tr>`;
  }).join("");
}

function zh(name) {
  const map = {
    NetProfit: "净利润", NetProfitPercent: "净利润 %", SharpeRatio: "夏普比率",
    MaxDrawdown: "最大回撤", MaxDrawdownPercent: "最大回撤 %", MaxRelativeDrawdown: "相对回撤",
    ProfitFactor: "盈利因子", WinningTrades: "盈利笔数", LossingTrades: "亏损笔数",
    RoundtripCount: "往返交易数", Commission: "手续费", GrossProfit: "总盈利",
    GrossLoss: "总亏损", Expectancy: "期望收益", RecoveryFactor: "复本因子",
    MaxProfit: "最大浮盈", AverageWinTrade: "平均盈利", AverageLossTrade: "平均亏损",
    TradeCount: "成交笔数", AverageTradeProfit: "单笔均利", Return: "收益率",
    MaxLongPosition: "最大多头", MaxShortPosition: "最大空头",
    OrderCount: "委托单数", OrderRegisterErrorCount: "废单数",
  };
  return map[name] ?? name;
}

function fmt(name, v) {
  if (v == null) return "—";
  const n = +v;
  if (isNaN(n)) return v;
  const abs = ["NetProfit", "MaxDrawdown", "GrossProfit", "GrossLoss", "Commission",
    "MaxProfit", "AverageWinTrade", "AverageLossTrade", "AverageTradeProfit", "Expectancy"];
  if (abs.includes(name)) return n.toLocaleString("en-US", { maximumFractionDigits: 0 });
  if (n % 1 === 0 && Math.abs(n) < 1e6) return n.toString();
  return n.toFixed(4).replace(/0+$/, "").replace(/\.$/, "");
}

// ---------- 数据管理 ----------
async function refreshDataList() {
  const res = await fetch("/api/data/list");
  const r = await res.json();
  const tb = document.querySelector("#dataList tbody");
  tb.innerHTML = r.items.map(i => `<tr>
    <td><b>${i.symbol}</b></td>
    <td>${i.tf}</td>
    <td>${i.range}</td>
    <td>${i.count}</td>
    <td>${i.sizeKb} KB</td>
    <td>${i.source}</td>
  </tr>`).join("") || '<tr><td colspan="6" class="muted">还没有数据，先用下面表单下载</td></tr>';
}

async function startFetch() {
  const btn = document.getElementById("fetchBtn");
  btn.disabled = true;
  const body = {
    symbol: document.getElementById("fSymbol").value.toUpperCase().trim(),
    tf: document.getElementById("fTf").value,
    begin: document.getElementById("fBegin").value,
  };
  const res = await fetch("/api/data/fetch", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body),
  });
  const r = await res.json();
  if (r.error) { alert(r.error); btn.disabled = false; return; }
  pollFetch(btn);
}

async function pollFetch(btn) {
  const status = document.getElementById("fetchStatus");
  const timer = setInterval(async () => {
    const res = await fetch("/api/data/fetchstatus");
    const s = await res.json();
    status.textContent = s.running
      ? `⏳ 正在下载 ${s.symbol} ${s.tf} …（已运行 ${s.elapsedSec} 秒）\nMT5 会在首次拉取时自动补历史，可能需要几十秒`
      : `输出:\n${(s.output || []).join("\n")}`;
    if (!s.running && s.exitCode !== -999) {
      clearInterval(timer);
      btn.disabled = false;
      refreshDataList();
    }
  }, 1500);
}

// ---------- 策略库 ----------
document.getElementById("strNew").addEventListener("click", () => openStrModal(null));
document.getElementById("strClose").addEventListener("click", () => document.getElementById("strModal").classList.add("hidden"));
document.getElementById("saveClose").addEventListener("click", () => document.getElementById("saveModal").classList.add("hidden"));
document.getElementById("strSave").addEventListener("click", saveStrategy);
document.getElementById("saveDo").addEventListener("click", doSaveToLibrary);

function esc(s) { return (s ?? "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/"/g, "&quot;"); }

async function refreshStrategies() {
  const res = await fetch("/api/strategies");
  const list = await res.json();
  const statusZh = { draft: "📝草案", tested: "✅已回测", paper: "🧪模拟验证", retired: "🗑退役" };
  document.querySelector("#strTable tbody").innerHTML = list.map(s => {
    let net = "—";
    try {
      const rep = JSON.parse(s.lastReport);
      net = rep.stats?.find(x => x.name === "NetProfit")?.value ?? "—";
    } catch (e) {}
    return `<tr>
      <td><b>${esc(s.name)}</b></td>
      <td>${s.template === "sma" ? "SMA交叉" : "N日突破"}</td>
      <td>${s.symbol} ${s.tf}</td>
      <td>${statusZh[s.status] ?? s.status}</td>
      <td class="${parseFloat(net) < 0 ? "neg" : "pos"}">${net}</td>
      <td class="muted" style="max-width:220px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap;" title="${esc(s.notes)}">${esc(s.notes)}</td>
      <td class="muted">${s.updated}</td>
      <td>
        <button class="mini" onclick="loadStrategyToBacktest(${s.id})">回测</button>
        <button class="mini" onclick="exportEa(${s.id})">⬇EA</button>
        <button class="mini" onclick="openStrModal(${s.id})">编辑</button>
        <button class="mini danger2" onclick="delStrategy(${s.id})">删</button>
      </td>
    </tr>`;
  }).join("") || '<tr><td colspan="8" class="muted">策略库为空：在回测工作台回测后点"存入策略库"，或点上方"新建策略"</td></tr>';
}

async function loadStrategyToBacktest(id) {
  const res = await fetch("/api/strategies/" + id);
  const s = await res.json();
  fillForm(s);
  switchView("backtest");
}

function openStrModal(id) {
  document.getElementById("strModal").classList.remove("hidden");
  document.getElementById("strId").value = id ?? "";
  document.getElementById("strModalTitle").textContent = id ? "编辑策略" : "新建策略";
  if (id == null) {
    document.getElementById("strName").value = "";
    document.getElementById("strNotes").value = "";
    document.getElementById("strTags").value = "";
    document.getElementById("strStatus").value = "draft";
    const f = collectForm();
    document.getElementById("strTemplate").value = f.template;
    document.getElementById("strSymbol").value = f.symbol;
    document.getElementById("strTf").value = f.tf;
    document.getElementById("strParams").value = f.params;
  } else {
    fetch("/api/strategies/" + id).then(r => r.json()).then(s => {
      document.getElementById("strName").value = s.name;
      document.getElementById("strTemplate").value = s.template;
      document.getElementById("strSymbol").value = s.symbol;
      document.getElementById("strTf").value = s.tf;
      document.getElementById("strParams").value = s.paramsJson;
      document.getElementById("strStatus").value = s.status;
      document.getElementById("strTags").value = s.tags;
      document.getElementById("strNotes").value = s.notes;
    });
  }
}

async function saveStrategy() {
  const id = document.getElementById("strId").value;
  const rec = {
    name: document.getElementById("strName").value.trim(),
    template: document.getElementById("strTemplate").value,
    symbol: document.getElementById("strSymbol").value.toUpperCase().trim(),
    tf: document.getElementById("strTf").value,
    paramsJson: document.getElementById("strParams").value || "{}",
    status: document.getElementById("strStatus").value,
    tags: document.getElementById("strTags").value,
    notes: document.getElementById("strNotes").value,
    lastReport: "",
  };
  if (!rec.name) { alert("请填写名称"); return; }
  const res = await fetch(id ? "/api/strategies/" + id : "/api/strategies", {
    method: id ? "PUT" : "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(rec),
  });
  document.getElementById("strModal").classList.add("hidden");
  refreshStrategies();
  refreshStrategySelect();
}

async function delStrategy(id) {
  if (!confirm("确定删除该策略？")) return;
  await fetch("/api/strategies/" + id, { method: "DELETE" });
  refreshStrategies();
  refreshStrategySelect();
}

function collectForm() {
  const st = selStrategy ? selStrategy.template : "sma";
  const p = st === "sma"
    ? { fast: +document.getElementById("fast").value, slow: +document.getElementById("slow").value }
    : { length: +document.getElementById("length").value };
  p.volume = +document.getElementById("volume").value || 1;
  p.stop = +document.getElementById("stop").value;
  p.take = +document.getElementById("take").value;
  p.trail = +document.getElementById("trail").value;
  p.partial = +document.getElementById("partial").value;
  return {
    template: st,
    symbol: document.getElementById("symbol").value.toUpperCase().trim(),
    tf: document.getElementById("tf").value,
    params: JSON.stringify(p),
  };
}

function fillForm(s) {
  selStrategy = s;
  const p = JSON.parse(s.paramsJson || s.params || "{}");
  document.getElementById("strategy").value = String(s.id);
  document.getElementById("symbol").value = s.symbol;
  document.getElementById("tf").value = s.tf;
  const isSma = s.template === "sma";
  document.getElementById("smaParams").classList.toggle("hidden", !isSma);
  document.getElementById("boParams").classList.toggle("hidden", isSma);
  if (isSma) {
    document.getElementById("fast").value = p.fast ?? 10;
    document.getElementById("slow").value = p.slow ?? 30;
  } else {
    document.getElementById("length").value = p.length ?? 20;
  }
  document.getElementById("volume").value = p.volume ?? 1;
  document.getElementById("stop").value = p.stop ?? 2;
  document.getElementById("take").value = p.take ?? 0;
  document.getElementById("trail").value = p.trail ?? 0;
  document.getElementById("partial").value = p.partial ?? 0;

  const banner = document.getElementById("curStrategyBanner");
  banner.textContent = `📌 当前策略：${s.name}（${s.symbol} ${s.tf}）` + (s.notes ? ` · ${s.notes}` : "");
  banner.classList.remove("hidden");
}

async function refreshStrategySelect(preferId) {
  strategiesCache = await (await fetch("/api/strategies")).json();
  const sel = document.getElementById("strategy");
  sel.innerHTML = strategiesCache.map(s => `<option value="${s.id}">${esc(s.name)}</option>`).join("")
    + '<option value="">— 策略库为空，去[策略库]新建或让AI生成 —</option>';
  const target = preferId ?? (strategiesCache.length ? strategiesCache[0].id : 0);
  if (target) {
    sel.value = String(target);
    const s = strategiesCache.find(x => x.id === target);
    if (s) fillForm(s);
  } else {
    selStrategy = null;
  }
}

// 存入策略库
document.getElementById("saveClose").addEventListener("click", () => document.getElementById("saveModal").classList.add("hidden"));
document.getElementById("saveDo").addEventListener("click", doSaveToLibrary);

(function installSaveEntry() {
  const h3 = document.querySelector("#stats h3");
  const btn = document.createElement("button");
  btn.className = "secondary"; btn.style.width = "auto";
  btn.style.padding = "4px 12px"; btn.style.fontSize = "12px"; btn.style.marginLeft = "10px";
  btn.textContent = "💾 存入策略库";
  btn.addEventListener("click", () => {
    if (!lastBacktest) { alert("先运行一次回测"); return; }
    const f = collectForm();
    const p = JSON.parse(f.params);
    const ptxt = f.template === "sma" ? ` ${p.fast}/${p.slow}` : ` ${p.length}日`;
    document.getElementById("saveName").value = `${f.symbol}${f.tf} ${f.template === "sma" ? "SMA" : "突破"}${ptxt}`;
    document.getElementById("saveModal").classList.remove("hidden");
  });
  h3.appendChild(btn);
})();

async function doSaveToLibrary() {
  const f = collectForm();
  const rec = {
    name: document.getElementById("saveName").value.trim(),
    template: f.template, symbol: f.symbol, tf: f.tf, paramsJson: f.params,
    status: "tested", tags: "", notes: document.getElementById("saveNotes").value,
    lastReport: JSON.stringify({ stats: lastBacktest.stats, time: new Date().toLocaleString("zh-CN") }),
  };
  if (!rec.name) { alert("请填写名称"); return; }
  await fetch("/api/strategies", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(rec),
  });
  document.getElementById("saveModal").classList.add("hidden");
}

// ---------- EA 导出 ----------
function exportEa(id) {
  window.open("/api/strategies/" + id + "/ea", "_blank");
}

// ---------- 实盘监控 ----------
let liveChartObj, liveSeries, liveTimer = null;
let selLiveStrategy = null;

document.getElementById("lStrategy").addEventListener("change", (e) => {
  const id = +e.target.value;
  const s = strategiesCache.find(x => x.id === id);
  if (s) applyLiveStrategy(s);
});

async function refreshLiveStrategySelect(preferId) {
  if (!strategiesCache.length)
    strategiesCache = await (await fetch("/api/strategies")).json();
  const sel = document.getElementById("lStrategy");
  sel.innerHTML = strategiesCache.map(s => `<option value="${s.id}">${esc(s.name)}</option>`).join("")
    + '<option value="">— 策略库为空，请先到[策略库]新建 —</option>';
  const target = preferId ?? (strategiesCache.length ? strategiesCache[0].id : 0);
  if (target) {
    sel.value = String(target);
    const s = strategiesCache.find(x => x.id === target);
    if (s) applyLiveStrategy(s);
  }
}

function applyLiveStrategy(s) {
  selLiveStrategy = s;
  const p = JSON.parse(s.paramsJson || "{}");
  document.getElementById("lSymbol").value = s.symbol;
  document.getElementById("lTf").value = s.tf;
  const isSma = s.template === "sma";
  document.getElementById("lSmaParams").classList.toggle("hidden", !isSma);
  document.getElementById("lBoParams").classList.toggle("hidden", isSma);
  if (isSma) {
    document.getElementById("lFast").value = p.fast ?? 10;
    document.getElementById("lSlow").value = p.slow ?? 30;
  } else {
    document.getElementById("lLength").value = p.length ?? 20;
  }
  document.getElementById("lStop").value = p.stop ?? 2;
  document.getElementById("lTake").value = p.take ?? 0;
  document.getElementById("lTrail").value = p.trail ?? 0;
  document.getElementById("lPartial").value = p.partial ?? 0;
  document.getElementById("liveStrategyBanner").textContent =
    `📌 ${s.name}（${s.symbol} ${s.tf}）` + (s.notes ? ` · ${s.notes}` : "");
}

document.getElementById("lStart").addEventListener("click", async () => {
  if (!selLiveStrategy) { alert("请先在策略下拉中选择一个策略（可到策略库新建）"); return; }
  const body = {
    symbol: document.getElementById("lSymbol").value.toUpperCase().trim(),
    tf: document.getElementById("lTf").value,
    strategy: selLiveStrategy.template,
    fast: +document.getElementById("lFast").value,
    slow: +document.getElementById("lSlow").value,
    length: +document.getElementById("lLength").value,
    lots: +document.getElementById("lLots").value,
    stop: +document.getElementById("lStop").value,
    take: +document.getElementById("lTake").value,
    trail: +document.getElementById("lTrail").value,
    partial: +document.getElementById("lPartial").value,
    autoTrade: document.getElementById("lAuto").checked,
  };
  const res = await fetch("/api/live/start", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body),
  });
  const r = await res.json();
  if (r.error) { alert(r.error); return; }
  setLiveUI(true);
  if (liveTimer) clearInterval(liveTimer);
  liveTimer = setInterval(pollLive, 3000);
  pollLive();
});

document.getElementById("lStop").addEventListener("click", async () => {
  await fetch("/api/live/stop", { method: "POST" });
  setLiveUI(false);
  if (liveTimer) { clearInterval(liveTimer); liveTimer = null; }
});

function setLiveUI(running) {
  document.getElementById("lStart").disabled = running;
  document.getElementById("lStop").disabled = !running;
  document.getElementById("liveDot").className = "dot " + (running ? "on" : "off");
}

function ensureLiveChart() {
  if (liveChartObj) return;
  const el = document.getElementById("liveChart");
  liveChartObj = LightweightCharts.createChart(el, {
    layout: { background: { color: "#1e222d" }, textColor: "#787b86" },
    grid: { vertLines: { color: "#2a2e39" }, horzLines: { color: "#2a2e39" } },
    timeScale: { borderColor: "#2a2e39", timeVisible: true },
    rightPriceScale: { borderColor: "#2a2e39" },
    height: el.clientHeight,
    width: el.clientWidth,
  });
  liveSeries = liveChartObj.addCandlestickSeries({
    upColor: "#26a69a", downColor: "#ef5350",
    borderVisible: false, wickUpColor: "#26a69a", wickDownColor: "#ef5350",
  });
}

async function pollLive() {
  const res = await fetch("/api/live/status");
  const r = await res.json();
  const s = r.state;
  const errEl = document.getElementById("liveErr");
  if (!r.workerAlive && (!s || s.running !== true)) {
    errEl.textContent = "引擎未运行";
    return;
  }
  ensureLiveChart();
  if (s.error) { errEl.textContent = "错误: " + s.error; return; }
  errEl.textContent = "";

  if (s.candles && s.candles.length) {
    liveSeries.setData(s.candles.map(c => ({
      time: c[0], open: c[1], high: c[2], low: c[3], close: c[4],
    })));
    liveChartObj.timeScale().fitContent();
  }

  document.getElementById("liveSymbol").textContent = s.symbol;
  document.getElementById("liveTf").textContent = s.tf;
  document.getElementById("liveQuote").textContent = s.bid ? ` 买 ${s.ask} / 卖 ${s.bid}` : "";
  document.getElementById("liveUpdated").textContent = `策略:${s.strategy} 更新于 ${s.last_update}`;

  if (s.account) {
    document.querySelector("#acctTable tbody").innerHTML = `
      <tr><td>账户</td><td>${s.account.login} @ ${s.account.server}</td></tr>
      <tr><td>余额</td><td>${s.account.balance}</td></tr>
      <tr><td>净值</td><td>${s.account.equity}</td></tr>
      <tr><td>模式</td><td>${s.autotrade ? "🔴 自动下单" : "🟡 仅信号"}</td></tr>`;
  }

  document.getElementById("livePosition").innerHTML = s.position
    ? `<span class="${s.position.side}">${s.position.side === "buy" ? "多单" : "空单"}</span>
       ${s.position.volume} 手 @ ${s.position.entry}
       <br>浮盈 <span class="${s.position.profit >= 0 ? "pos" : "neg"}">${s.position.profit}</span>`
    : "无";

  document.getElementById("signalLog").innerHTML = (s.signals || [])
    .map(e => `<div>${e.time} ${e.text}</div>`).join("") || "暂无信号";
}

// ---------- 策略工坊(AI) ----------
let aiConvId = 0;
let aiCard = null;

document.getElementById("aiMgrBtn").addEventListener("click", openAiMgr);
document.getElementById("aiMgrClose").addEventListener("click", () => document.getElementById("aiMgrModal").classList.add("hidden"));
document.getElementById("aiProvNew").addEventListener("click", () => clearProvForm());
document.getElementById("aiProvSave").addEventListener("click", saveProvider);
document.getElementById("aiProvDel").addEventListener("click", deleteProvider);
document.getElementById("aiProvTest").addEventListener("click", testProvider);
document.getElementById("aiProvList").addEventListener("change", async (e) => {
  const id = +e.target.value;
  if (!id) { clearProvForm(); return; }
  const list = await (await fetch("/api/ai/providers")).json();
  const p = list.find(x => x.id === id);
  if (!p) return;
  document.getElementById("aiPName").value = p.name;
  document.getElementById("aiPProtocol").value = p.protocol;
  document.getElementById("aiPUrl").value = p.baseUrl;
  document.getElementById("aiPKey").value = p.apiKey;
  document.getElementById("aiPModel").value = p.model;
  document.getElementById("aiPTemp").value = p.temperature;
  document.getElementById("aiPDefault").checked = p.isDefault === 1;
  document.getElementById("aiProvList").dataset.currentId = p.id;
});

function clearProvForm() {
  ["aiPName", "aiPUrl", "aiPKey", "aiPModel"].forEach(id => document.getElementById(id).value = "");
  document.getElementById("aiPTemp").value = "0.4";
  document.getElementById("aiPDefault").checked = false;
  document.getElementById("aiProvList").dataset.currentId = "";
}

async function openAiMgr() {
  document.getElementById("aiMgrModal").classList.remove("hidden");
  const list = await (await fetch("/api/ai/providers")).json();
  const sel = document.getElementById("aiProvList");
  sel.innerHTML = list.map(p => `<option value="${p.id}">${esc(p.name)} (${p.protocol})${p.isDefault ? " ★默认" : ""}</option>`).join("")
    + '<option value="">＋ 新增方案…</option>';
  sel.dataset.currentId = "";
  if (list.length) { sel.value = list[0].id; sel.dispatchEvent(new Event("change")); }
  else clearProvForm();
}

async function saveProvider() {
  const curId = +(document.getElementById("aiProvList").dataset.currentId || 0);
  const rec = {
    id: curId,
    name: document.getElementById("aiPName").value.trim(),
    protocol: document.getElementById("aiPProtocol").value,
    baseUrl: document.getElementById("aiPUrl").value.trim(),
    apiKey: document.getElementById("aiPKey").value.trim(),
    model: document.getElementById("aiPModel").value.trim(),
    temperature: +document.getElementById("aiPTemp").value,
    isDefault: document.getElementById("aiPDefault").checked ? 1 : 0,
  };
  if (!rec.name || !rec.baseUrl || !rec.model) { alert("名称/接口地址/模型 必填"); return; }
  const res = await fetch("/api/ai/providers", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(rec),
  });
  const r = await res.json();
  if (r.error) { alert(r.error); return; }
  openAiMgr();
}

async function deleteProvider() {
  const id = +(document.getElementById("aiProvList").dataset.currentId || 0);
  if (!id) { alert("先选择要删除的方案"); return; }
  if (!confirm("确定删除该接入方案？")) return;
  await fetch("/api/ai/providers/" + id, { method: "DELETE" });
  openAiMgr();
}

async function testProvider() {
  const el = document.getElementById("aiTestResult");
  el.textContent = "测试中…";
  await saveProvider();
  const list = await (await fetch("/api/ai/providers")).json();
  const p = list.find(x => x.name === document.getElementById("aiPName").value.trim());
  if (!p) { el.textContent = "保存失败"; return; }
  const res = await fetch("/api/ai/providers/" + p.id + "/test", { method: "POST" });
  const r = await res.json();
  el.innerHTML = r.ok ? '<span class="pos">✓ 连通正常</span>' : '<span class="neg">✗ ' + esc(r.error || "失败") + '</span>';
}

// ---------- AI 会话 ----------
document.getElementById("aiNewConv").addEventListener("click", () => {
  aiConvId = 0;
  document.getElementById("aiMessages").innerHTML = "";
  document.getElementById("aiConvList").value = "";
});
document.getElementById("aiDelConv").addEventListener("click", async () => {
  if (!aiConvId) return;
  if (!confirm("删除当前会话？")) return;
  await fetch("/api/ai/conversations/" + aiConvId, { method: "DELETE" });
  aiConvId = 0;
  document.getElementById("aiMessages").innerHTML = "";
  refreshConvList();
});
document.getElementById("aiConvList").addEventListener("change", async (e) => {
  const id = +e.target.value;
  if (!id) return;
  aiConvId = id;
  const msgs = await (await fetch("/api/ai/conversations/" + id + "/messages")).json();
  const box = document.getElementById("aiMessages");
  box.innerHTML = "";
  for (const m of msgs) {
    if (m.role === "user") addChatBubble("user", m.content);
    else {
      try {
        const p = JSON.parse(m.content);
        addChatBubble("assistant", p.reply);
        renderPanels(p);
      } catch { addChatBubble("assistant", m.content); }
    }
  }
});

async function refreshConvList() {
  const list = await (await fetch("/api/ai/conversations")).json();
  document.getElementById("aiConvList").innerHTML =
    '<option value="">— 历史会话 —</option>' +
    list.map(c => `<option value="${c.id}">${esc(c.title)}</option>`).join("");
  if (aiConvId) document.getElementById("aiConvList").value = aiConvId;
}

function addChatBubble(role, text) {
  const box = document.getElementById("aiMessages");
  const div = document.createElement("div");
  div.className = "bubble " + role;
  div.textContent = text;
  box.appendChild(div);
  box.scrollTop = box.scrollHeight;
  return div;
}

function renderPanels(p) {
  const cardBody = document.getElementById("aiCardBody");
  const btnBt = document.getElementById("aiCardBacktest");
  const btnSave = document.getElementById("aiCardSave");
  if (p.card) {
    aiCard = p.card;
    if (!aiCard.symbol) aiCard.symbol = curSymbol;
    if (!aiCard.tf) aiCard.tf = curTf;
    const pr = aiCard.params || {};
    cardBody.innerHTML = `
      <b>${esc(aiCard.name || "未命名")}</b>
      <span class="muted">（${aiCard.symbol} ${aiCard.tf}）</span><br>
      <div style="margin:6px 0;">📖 ${esc(cardToText(aiCard)).replace(/【(.+?)】/g, "<b>$1</b>")}</div>
      <span class="muted">${esc(aiCard.notes || "")}</span>`;
    btnBt.classList.remove("hidden");
    btnSave.classList.remove("hidden");
  } else {
    aiCard = null;
    cardBody.innerHTML = '<span class="muted">本次回复未包含策略</span>';
    btnBt.classList.add("hidden");
    btnSave.classList.add("hidden");
  }

  const plain = aiCard ? cardToText(aiCard) : "";
  document.getElementById("aiPlainBody").innerHTML =
    aiCard ? esc(plain).replace(/【(.+?)】/g, "<b>$1</b>") : "—";
  document.getElementById("aiCodeBody").textContent = p.code || "—";
  document.getElementById("aiCodeBody").classList.toggle("muted", !p.code);

  document.getElementById("aiAdviceList").innerHTML =
    (p.advice || []).map(a => `<li>${esc(a)}</li>`).join("") || "<li>—</li>";
  document.getElementById("aiRiskList").innerHTML =
    (p.risks || []).map(a => `<li>${esc(a)}</li>`).join("") || "<li>—</li>";
}

// 卡片 -> 直接回测
document.getElementById("aiCardBacktest").addEventListener("click", () => {
  if (!aiCard) return;
  const pr = aiCard.params || {};
  document.getElementById("strategy").value = aiCard.template === "breakout" ? "breakout" : "sma";
  document.getElementById("strategy").dispatchEvent(new Event("change"));
  document.getElementById("symbol").value = (aiCard.symbol || curSymbol).toUpperCase();
  document.getElementById("tf").value = aiCard.tf || curTf;
  switchView("backtest");
});

// 卡片 -> 存入策略库
document.getElementById("aiCardSave").addEventListener("click", async () => {
  if (!aiCard) return;
  const rec = {
    name: aiCard.name || "AI策略",
    template: aiCard.template,
    symbol: (aiCard.symbol || curSymbol).toUpperCase(),
    tf: aiCard.tf || curTf,
    paramsJson: JSON.stringify(aiCard.params || {}),
    status: "draft",
    tags: "AI生成",
    notes: (aiCard.notes || "") + "（AI工坊生成）",
    lastReport: "",
  };
  await fetch("/api/strategies", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(rec),
  });
  alert("已存入策略库（状态：草案）");
});

// ---------- 发送消息（流式） ----------
document.getElementById("aiSend").addEventListener("click", sendAiMessage);
document.getElementById("aiInput").addEventListener("keydown", e => {
  if (e.key === "Enter") sendAiMessage();
});

async function sendAiMessage() {
  const input = document.getElementById("aiInput");
  const msg = input.value.trim();
  if (!msg) return;
  const btn = document.getElementById("aiSend");
  btn.disabled = true;
  const bubble = addChatBubble("assistant", "…");
  input.value = "";

  const preset = document.getElementById("aiPreset").value;
  const presetHint = {
    strategy: "",
    diagnose: "（任务模式：用户会描述或粘贴一个已有策略，请从止损、仓位、趋势过滤、常见陷阱角度做诊断审查，并给出可执行的修改建议）",
    chat: "（任务模式：自由交流，不强制生成策略卡片）",
  }[preset] ?? "";

  try {
    const res = await fetch("/api/ai/chat/stream", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ conversationId: aiConvId, message: msg + presetHint, preset }),
    });
    const reader = res.body.getReader();
    const dec = new TextDecoder();
    let buf = "", reply = "", payload = null;

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      let idx;
      while ((idx = buf.indexOf("\n\n")) >= 0) {
        const chunk = buf.slice(0, idx).trim();
        buf = buf.slice(idx + 2);
        if (!chunk.startsWith("data: ")) continue;
        let ev;
        try { ev = JSON.parse(chunk.slice(6)); } catch { continue; }
        if (ev.delta) {
          if (reply === "…") reply = "";
          reply += ev.delta;
          bubble.textContent = reply;
          boxScroll();
        }
        if (ev.error) { bubble.textContent = "⚠ " + ev.error; btn.disabled = false; return; }
        if (ev.done) { payload = ev; }
      }
    }

    if (payload) {
      aiConvId = payload.conversationId;
      bubble.textContent = payload.reply || reply || "（空回复）";
      renderPanels(payload);
      refreshConvList();
    }
  } catch (ex) {
    bubble.textContent = "⚠ 请求失败: " + ex.message;
  } finally {
    btn.disabled = false;
  }
}

function boxScroll() {
  const box = document.getElementById("aiMessages");
  box.scrollTop = box.scrollHeight;
}

// 策略卡片 -> 人话描述
function cardToText(card) {
  const p = card.params || {};
  const exit = `止损 ${p.stop ?? 0}%`;
  const lot = `每笔 ${p.volume ?? 1} 手。${exit}。`;
  if (card.template === "sma")
    return `盯着 ${(card.symbol || curSymbol)} ${card.tf || curTf} 的K线，` +
      `当 ${p.fast} 期均线【上穿】${p.slow} 期均线时买入（金叉），` +
      `【下穿】时卖出（死叉）。${lot}适合趋势行情，震荡市容易反复止损。`;
  if (card.template === "breakout")
    return `盯着 ${(card.symbol || curSymbol)} ${card.tf || curTf} 的K线，` +
      `当收盘价【突破】过去 ${p.length} 天最高价时买入，` +
      `【跌破】过去 ${p.length} 天最低价时卖出。${lot}经典海龟式突破，适合趋势行情。`;
  return "";
}
