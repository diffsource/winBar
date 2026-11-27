using System.Text.Json.Serialization;

sealed class AppStackConfig
{
    public bool Enabled { get; set; }
    public bool AutoCompact { get; set; }
    public int CompactAtPercent { get; set; } = 75;
    public List<AppStackGroup> Groups { get; set; } = [];

    // Four 10-bit slots: 5-bit zero-based start, then 5-bit count.
    // Invalid or overlapping ranges fail closed and are omitted.
    [JsonIgnore]
    public ulong PackedRanges => PackRanges();

    public ulong PackRanges()
    {
        if (!Enabled) return 0;
        ulong packed = 0;
        var occupied = new bool[32];
        int slot = 0;
        foreach (var group in Groups ?? [])
        {
            if (slot >= 4) break;
            if (!group.Enabled) continue;
            int start = group.Start - 1;
            int count = group.Count;
            if (start < 0 || start >= 32 || count < 2 || count > 31 || start + count > 32 ||
                Enumerable.Range(start, count).Any(index => occupied[index])) continue;
            for (int i = start; i < start + count; i++) occupied[i] = true;
            packed |= ((ulong)(uint)start | ((ulong)(uint)count << 5)) << (slot * 10);
            slot++;
        }
        return packed;
    }

    public AppStackConfig Copy() => new()
    {
        Enabled = Enabled,
        AutoCompact = AutoCompact,
        CompactAtPercent = CompactAtPercent,
        Groups = (Groups ?? []).Select(group => group.Copy()).ToList()
    };

    public bool IsValid(out string reason)
    {
        if (Groups == null || Groups.Count > 4)
        {
            reason = "Du kannst höchstens vier Stapel anlegen.";
            return false;
        }
        var occupied = new bool[32];
        foreach (var group in Groups)
        {
            if (!group.Enabled) continue;
            int start = group.Start - 1;
            int count = group.Count;
            if (start < 0 || start >= 32 || count < 2 || count > 31 || start + count > 32)
            {
                reason = $"{group.Name}: Position oder Anzahl liegt außerhalb der ersten 32 Apps.";
                return false;
            }
            if (Enumerable.Range(start, count).Any(index => occupied[index]))
            {
                reason = $"{group.Name}: Der Bereich überschneidet sich mit einem anderen Stapel.";
                return false;
            }
            for (int i = start; i < start + count; i++) occupied[i] = true;
        }
        reason = "";
        return true;
    }
}

sealed class AppStackGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "Stapel";
    public int Start { get; set; } = 1; // 1-based position in Windows pin order
    public int Count { get; set; } = 2;
    public List<string> LaunchPaths { get; set; } = [];

    public AppStackGroup Copy() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Name = Name,
        Start = Start,
        Count = Count,
        LaunchPaths = [.. (LaunchPaths ?? [])]
    };
}
