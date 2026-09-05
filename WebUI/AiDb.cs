// ============================================================================
// AI 层数据库：接入方案 / 会话 / 消息，与策略库同一个 SQLite 文件。
// ============================================================================
namespace WebUI;

using Microsoft.Data.Sqlite;

public class AiProvider
{
	public long Id { get; set; }
	public string Name { get; set; } = "";
	public string Protocol { get; set; } = "openai";    // openai | anthropic
	public string BaseUrl { get; set; } = "";
	public string ApiKey { get; set; } = "";
	public string Model { get; set; } = "";
	public double Temperature { get; set; } = 0.4;
	public int IsDefault { get; set; }
}

public class AiConversation
{
	public long Id { get; set; }
	public string Title { get; set; } = "新会话";
	public string Created { get; set; } = "";
}

public class AiMessage
{
	public long Id { get; set; }
	public long ConvId { get; set; }
	public string Role { get; set; } = "user";           // user | assistant
	public string Content { get; set; } = "";            // user: 纯文本; assistant: 结构化JSON
	public string Created { get; set; } = "";
}

public static class AiDb
{
	private static string DbPath => Path.Combine(Program.DataDir, "workbench.db");
	private static bool _initialized;

	public static void Init()
	{
		if (_initialized)
			return;

		using var conn = Open();
		foreach (var sql in new[]
		{
			"""
			CREATE TABLE IF NOT EXISTS ai_providers (
				id INTEGER PRIMARY KEY AUTOINCREMENT,
				name TEXT NOT NULL,
				protocol TEXT NOT NULL DEFAULT 'openai',
				base_url TEXT NOT NULL DEFAULT '',
				api_key TEXT NOT NULL DEFAULT '',
				model TEXT NOT NULL DEFAULT '',
				temperature REAL NOT NULL DEFAULT 0.4,
				is_default INTEGER NOT NULL DEFAULT 0,
				created TEXT NOT NULL DEFAULT (datetime('now','localtime'))
			);
			""",
			"""
			CREATE TABLE IF NOT EXISTS ai_conversations (
				id INTEGER PRIMARY KEY AUTOINCREMENT,
				title TEXT NOT NULL DEFAULT '新会话',
				created TEXT NOT NULL DEFAULT (datetime('now','localtime'))
			);
			""",
			"""
			CREATE TABLE IF NOT EXISTS ai_messages (
				id INTEGER PRIMARY KEY AUTOINCREMENT,
				conv_id INTEGER NOT NULL,
				role TEXT NOT NULL,
				content TEXT NOT NULL,
				created TEXT NOT NULL DEFAULT (datetime('now','localtime'))
			);
			""",
		})
		{
			using var cmd = conn.CreateCommand();
			cmd.CommandText = sql;
			cmd.ExecuteNonQuery();
		}
		_initialized = true;
	}

	private static SqliteConnection Open()
	{
		var conn = new SqliteConnection($"Data Source={DbPath}");
		conn.Open();
		return conn;
	}

	// ---------- 接入方案 ----------
	public static List<AiProvider> ListProviders()
	{
		Init();
		var list = new List<AiProvider>();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT id,name,protocol,base_url,api_key,model,temperature,is_default FROM ai_providers ORDER BY id";
		using var r = cmd.ExecuteReader();
		while (r.Read())
			list.Add(new AiProvider
			{
				Id = r.GetInt64(0), Name = r.GetString(1), Protocol = r.GetString(2),
				BaseUrl = r.GetString(3), ApiKey = r.GetString(4), Model = r.GetString(5),
				Temperature = r.GetDouble(6), IsDefault = r.GetInt32(7),
			});
		return list;
	}

	public static void SaveProvider(AiProvider p)
	{
		Init();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		if (p.Id > 0)
		{
			cmd.CommandText = """
				UPDATE ai_providers SET name=$n, protocol=$p, base_url=$u, api_key=$k,
					model=$m, temperature=$t, is_default=$d WHERE id=$id
				""";
			cmd.Parameters.AddWithValue("$id", p.Id);
		}
		else
		{
			cmd.CommandText = """
				INSERT INTO ai_providers (name,protocol,base_url,api_key,model,temperature,is_default)
				VALUES ($n,$p,$u,$k,$m,$t,$d)
				""";
		}
		cmd.Parameters.AddWithValue("$n", p.Name);
		cmd.Parameters.AddWithValue("$p", p.Protocol);
		cmd.Parameters.AddWithValue("$u", p.BaseUrl);
		cmd.Parameters.AddWithValue("$k", p.ApiKey);
		cmd.Parameters.AddWithValue("$m", p.Model);
		cmd.Parameters.AddWithValue("$t", p.Temperature);
		cmd.Parameters.AddWithValue("$d", p.IsDefault);
		cmd.ExecuteNonQuery();

		if (p.IsDefault == 1)
		{
			using var cmd2 = conn.CreateCommand();
			cmd2.CommandText = "UPDATE ai_providers SET is_default=0 WHERE id != $id";
			cmd2.Parameters.AddWithValue("$id", p.Id > 0 ? p.Id : LastProviderId());
			cmd2.ExecuteNonQuery();
		}
	}

	private static long LastProviderId()
	{
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT last_insert_rowid()";
		return (long)cmd.ExecuteScalar()!;
	}

	public static void DeleteProvider(long id)
	{
		Init();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "DELETE FROM ai_providers WHERE id=$id";
		cmd.Parameters.AddWithValue("$id", id);
		cmd.ExecuteNonQuery();
	}

	public static AiProvider? GetDefaultProvider()
	{
		var list = ListProviders();
		return list.FirstOrDefault(p => p.IsDefault == 1) ?? list.FirstOrDefault();
	}

	// ---------- 会话 ----------
	public static List<AiConversation> ListConversations()
	{
		Init();
		var list = new List<AiConversation>();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = """
			SELECT c.id, c.title, c.created, COUNT(m.id) FROM ai_conversations c
			LEFT JOIN ai_messages m ON m.conv_id = c.id
			GROUP BY c.id ORDER BY c.id DESC
			""";
		using var r = cmd.ExecuteReader();
		while (r.Read())
			list.Add(new AiConversation { Id = r.GetInt64(0), Title = r.GetString(1), Created = r.GetString(2) });
		return list;
	}

	public static long CreateConversation(string title)
	{
		Init();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "INSERT INTO ai_conversations (title) VALUES ($t); SELECT last_insert_rowid();";
		cmd.Parameters.AddWithValue("$t", title);
		return (long)cmd.ExecuteScalar()!;
	}

	public static void DeleteConversation(long id)
	{
		Init();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "DELETE FROM ai_conversations WHERE id=$id; DELETE FROM ai_messages WHERE conv_id=$id";
		cmd.Parameters.AddWithValue("$id", id);
		cmd.ExecuteNonQuery();
	}

	public static List<AiMessage> GetMessages(long convId)
	{
		Init();
		var list = new List<AiMessage>();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "SELECT id,conv_id,role,content,created FROM ai_messages WHERE conv_id=$id ORDER BY id";
		cmd.Parameters.AddWithValue("$id", convId);
		using var r = cmd.ExecuteReader();
		while (r.Read())
			list.Add(new AiMessage
			{
				Id = r.GetInt64(0), ConvId = r.GetInt64(1), Role = r.GetString(2),
				Content = r.GetString(3), Created = r.GetString(4),
			});
		return list;
	}

	public static void AddMessage(long convId, string role, string content)
	{
		Init();
		using var conn = Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "INSERT INTO ai_messages (conv_id,role,content) VALUES ($c,$r,$m)";
		cmd.Parameters.AddWithValue("$c", convId);
		cmd.Parameters.AddWithValue("$r", role);
		cmd.Parameters.AddWithValue("$m", content);
		cmd.ExecuteNonQuery();

		// 会话第一条消息作为标题
		using var cmd2 = conn.CreateCommand();
		cmd2.CommandText = "UPDATE ai_conversations SET title=$t WHERE id=$id AND title='新会话'";
		cmd2.Parameters.AddWithValue("$t", content.Length > 24 ? content[..24] + "…" : content);
		cmd2.Parameters.AddWithValue("$id", convId);
		cmd2.ExecuteNonQuery();
	}
}
