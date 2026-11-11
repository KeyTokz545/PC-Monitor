// Bilans de fin de partie : les 10 derniers, gardés dans %AppData%\PCMonitor\parties.json.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PCMonitor;

public static class GameReports
{
    static readonly string FilePath = Path.Combine(Log.Dir, "parties.json");
    static readonly object Gate = new();
    static List<Dictionary<string, object>> list;

    static List<Dictionary<string, object>> Items
    {
        get
        {
            if (list != null) return list;
            list = new List<Dictionary<string, object>>();
            try
            {
                if (File.Exists(FilePath))
                {
                    var raw = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(File.ReadAllText(FilePath));
                    foreach (var r in raw ?? new())
                        list.Add(r.ToDictionary(kv => kv.Key, kv => (object)(kv.Value.ValueKind switch
                        {
                            JsonValueKind.Number => kv.Value.TryGetInt64(out var l) ? l : kv.Value.GetDouble(),
                            JsonValueKind.String => kv.Value.GetString(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => null,
                        })));
                }
            }
            catch (Exception e)
            {
                Log.Error("Lecture des bilans de partie", e);
            }
            return list;
        }
    }

    public static long? LatestId
    {
        get
        {
            lock (Gate) return Items.Count > 0 && Items[0].TryGetValue("id", out var id) && id is long l ? l : null;
        }
    }

    public static List<Dictionary<string, object>> All()
    {
        lock (Gate) return Items.ToList();
    }

    public static void Add(Dictionary<string, object> report)
    {
        lock (Gate)
        {
            Items.Insert(0, report);
            while (Items.Count > 10) Items.RemoveAt(Items.Count - 1);
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, Collector.Json)); }
            catch (Exception e) { Log.Error("Enregistrement du bilan de partie", e); }
        }
    }
}
