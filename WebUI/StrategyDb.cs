// ============================================================================
// 策略库：本地 SQLite 存储。一个策略 = 模板 + 参数 + 元数据。
// AI 工坊生成的策略、手工建的策略、优化产出的策略都存这里。
// ============================================================================
namespace WebUI;

using Microsoft.Data.Sqlite;

public class StrategyRecord
{
	public long Id { get; set; }
	public string Name { get; set; } = "";
	public string Template { get; set; } = "sma";           // sma | breakout
	public string ParamsJson { get; set; } = "{}";          // {"fast":10,"slow":30,"volume":10000,"stop":2}
	public string Symbol { get; set; } = "EURUSD";
	public string Tf { get; set; } = "D1";
	public string Notes { get; set; } = "";                 // 备注：思路、来历、注意事项
	public string Tags { get; set; } = "";                  // 逗号分隔：趋势,日线,试验中
	public string Status { get; set; } = "draft";           // draft | tested | paper | retired
	public string LastReport { get; set; } = "";            // 最近回测成绩 JSON 快照
	public string Created { get; set; } = "";
	public string Updated { get; set; } = "";
}

public static class StrategyDb
{
	private static readonly string DbPath =
		Path.Combine(Program.DataDir, "workbench.db");

	private static bool _initialized;

	public static void Init()
	{
		if (_initialized)
			return;

		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS strategies (
				id          INTEGER PRIMARY KEY AUTOINCREMENT,
				name        TEXT NOT NULL,
				template    TEXT NOT NULL DEFAULT 'sma',
				params_json TEXT NOT NULL DEFAULT '{}',
				symbol      TEXT NOT NULL DEFAULT 'EURUSD',
				tf          TEXT NOT NULL DEFAULT 'D1',
				notes       TEXT NOT NULL DEFAULT '',
				tags        TEXT NOT NULL DEFAULT '',
				status      TEXT NOT NULL DEFAULT 'draft',
				last_report TEXT NOT NULL DEFAULT '',
				created     TEXT NOT NULL DEFAULT (datetime('now','localtime')),
				updated     TEXT NOT NULL DEFAULT (datetime('now','localtime'))
			);
			""";
		cmd.ExecuteNonQuery();
		_initialized = true;
	}

	private static SqliteConnection Open()
	{
		var conn = new SqliteConnection($"Data Source={DbPath}");
		conn.Open();
		return conn;
	}

	public static List<StrategyRecord> List()
	{
		Init();
		var list = new List<StrategyRecord>();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT id,name,template,params_json,symbol,tf,notes,tags,status,last_report,created,updated FROM strategies ORDER BY updated DESC";
		using var reader = cmd.ExecuteReader();
		while (reader.Read())
			list.Add(Read(reader));
		return list;
	}

	public static StrategyRecord? Get(long id)
	{
		Init();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT id,name,template,params_json,symbol,tf,notes,tags,status,last_report,created,updated FROM strategies WHERE id=$id";
		cmd.Parameters.AddWithValue("$id", id);
		using var reader = cmd.ExecuteReader();
		return reader.Read() ? Read(reader) : null;
	}

	public static long Create(StrategyRecord r)
	{
		Init();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = """
			INSERT INTO strategies (name,template,params_json,symbol,tf,notes,tags,status,last_report)
			VALUES ($name,$template,$params,$symbol,$tf,$notes,$tags,$status,$report);
			SELECT last_insert_rowid();
			""";
		Bind(cmd, r);
		return (long)cmd.ExecuteScalar()!;
	}

	public static bool Update(StrategyRecord r)
	{
		Init();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = """
			UPDATE strategies SET name=$name, template=$template, params_json=$params,
				symbol=$symbol, tf=$tf, notes=$notes, tags=$tags, status=$status,
				last_report=$report, updated=datetime('now','localtime')
			WHERE id=$id
			""";
		Bind(cmd, r);
		cmd.Parameters.AddWithValue("$id", r.Id);
		return cmd.ExecuteNonQuery() > 0;
	}

	public static bool Delete(long id)
	{
		Init();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "DELETE FROM strategies WHERE id=$id";
		cmd.Parameters.AddWithValue("$id", id);
		return cmd.ExecuteNonQuery() > 0;
	}

	private static void Bind(SqliteCommand cmd, StrategyRecord r)
	{
		cmd.Parameters.AddWithValue("$name", r.Name);
		cmd.Parameters.AddWithValue("$template", r.Template);
		cmd.Parameters.AddWithValue("$params", r.ParamsJson);
		cmd.Parameters.AddWithValue("$symbol", r.Symbol);
		cmd.Parameters.AddWithValue("$tf", r.Tf);
		cmd.Parameters.AddWithValue("$notes", r.Notes);
		cmd.Parameters.AddWithValue("$tags", r.Tags);
		cmd.Parameters.AddWithValue("$status", r.Status);
		cmd.Parameters.AddWithValue("$report", r.LastReport);
	}

	private static StrategyRecord Read(SqliteDataReader r) => new()
	{
		Id = r.GetInt64(0),
		Name = r.GetString(1),
		Template = r.GetString(2),
		ParamsJson = r.GetString(3),
		Symbol = r.GetString(4),
		Tf = r.GetString(5),
		Notes = r.GetString(6),
		Tags = r.GetString(7),
		Status = r.GetString(8),
		LastReport = r.GetString(9),
		Created = r.GetString(10),
		Updated = r.GetString(11),
	};
}
