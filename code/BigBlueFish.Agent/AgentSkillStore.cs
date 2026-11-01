using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BigBlueFish.Agent;

public sealed record AgentPersona(string Id, string Name, bool Builtin);

public sealed class AgentSkillStore
{
    private static readonly JsonSerializerOptions IndexJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public const string DefaultPersonaId = "default";
    public const string DefaultPersonaName = "蓝色大肥鱼";

    public string Workspace { get; }
    public string DirectoryPath { get; }
    public string PersonaPath => Path.Combine(DirectoryPath, "persona.md");
    public string SkillPath => Path.Combine(DirectoryPath, "SKILL.md");
    public string PersonasDirectory => Path.Combine(DirectoryPath, "personas");
    private string IndexPath => Path.Combine(PersonasDirectory, "personas.json");

    public AgentSkillStore(string root, HarnessDefinition harness, string? workspace = null)
    {
        Workspace = Path.GetFullPath(workspace ?? Path.Combine(root, "workspaces", harness.Id));
        DirectoryPath = Path.Combine(Workspace, harness.SkillDirectory.Replace('/', Path.DirectorySeparatorChar), "big-blue-fish");
    }

    public void Install()
    {
        Directory.CreateDirectory(DirectoryPath);
        // Skills are installed inside the pet-owned workspace, never the user's global harness configuration.
        string receipt = Path.Combine(DirectoryPath, ".fish-skill.sha256");
        string? currentHash = File.Exists(SkillPath) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(SkillPath))) : null;
        bool managed = currentHash == null || currentHash == "C48D05F6D922B1C7EF68C5E991E6E790484FA5916D8C85A429E0599A17CF03DF"
            || File.Exists(receipt) && File.ReadAllText(receipt) == currentHash;
        if (managed)
        {
            string content = Resource("SKILL.md");
            if (!File.Exists(SkillPath) || File.ReadAllText(SkillPath) != content) AtomicText.Write(SkillPath, content);
            AtomicText.Write(receipt, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(SkillPath))));
        }
        EnsurePersonas();
    }

    public string Guide() => File.ReadAllText(SkillPath) + "\n\n" + File.ReadAllText(PersonaPath);

    public AgentPersona[] ListPersonas()
    {
        EnsurePersonas();
        return ReadIndex().Items.Select(x => new AgentPersona(x.Id, x.Name, x.Builtin)).ToArray();
    }

    public AgentPersona ActivePersona()
    {
        EnsurePersonas();
        var index = ReadIndex();
        var item = index.Items.FirstOrDefault(x => x.Id == index.ActiveId) ?? index.Items.First(x => x.Id == DefaultPersonaId);
        return new AgentPersona(item.Id, item.Name, item.Builtin);
    }

    public string ReadPersonaContent(string id)
    {
        EnsurePersonas();
        string path = PersonaFile(id);
        if (!File.Exists(path)) throw new FileNotFoundException("人设不存在。", path);
        return File.ReadAllText(path);
    }

    public AgentPersona CreatePersona(string name, string? content = null)
    {
        EnsurePersonas();
        name = NormalizeName(name);
        var index = ReadIndex();
        string id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        while (index.Items.Any(x => x.Id == id)) id = "custom-" + Guid.NewGuid().ToString("N")[..8];
        AtomicText.Write(PersonaFile(id), content ?? "");
        index.Items.Add(new PersonaItem { Id = id, Name = name, Builtin = false });
        WriteIndex(index);
        return new AgentPersona(id, name, false);
    }

    public void SavePersona(string id, string name, string content)
    {
        EnsurePersonas();
        var index = ReadIndex();
        var item = index.Items.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("人设不存在。");
        item.Name = NormalizeName(name);
        AtomicText.Write(PersonaFile(id), content ?? "");
        WriteIndex(index);
        if (index.ActiveId == id) AtomicText.Write(PersonaPath, content ?? "");
    }

    public void ApplyPersona(string id)
    {
        EnsurePersonas();
        var index = ReadIndex();
        if (!index.Items.Any(x => x.Id == id)) throw new InvalidOperationException("人设不存在。");
        string content = File.Exists(PersonaFile(id)) ? File.ReadAllText(PersonaFile(id)) : "";
        AtomicText.Write(PersonaPath, content);
        index.ActiveId = id;
        WriteIndex(index);
    }

    public void DeletePersona(string id)
    {
        EnsurePersonas();
        if (id == DefaultPersonaId) throw new InvalidOperationException("默认人设不能删除。");
        var index = ReadIndex();
        var item = index.Items.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("人设不存在。");
        if (item.Builtin) throw new InvalidOperationException("内置人设不能删除。");
        bool wasActive = index.ActiveId == id;
        index.Items.RemoveAll(x => x.Id == id);
        if (wasActive) index.ActiveId = DefaultPersonaId;
        WriteIndex(index);
        string path = PersonaFile(id);
        if (File.Exists(path)) File.Delete(path);
        if (wasActive) ApplyPersona(DefaultPersonaId);
    }

    public void ResetDefaultPersona()
    {
        EnsurePersonas();
        string content = Resource("persona.md");
        AtomicText.Write(PersonaFile(DefaultPersonaId), content);
        var index = ReadIndex();
        var item = index.Items.First(x => x.Id == DefaultPersonaId);
        item.Name = DefaultPersonaName;
        WriteIndex(index);
        if (index.ActiveId == DefaultPersonaId) AtomicText.Write(PersonaPath, content);
    }

    private void EnsurePersonas()
    {
        Directory.CreateDirectory(PersonasDirectory);
        string builtin = Resource("persona.md");
        if (!File.Exists(IndexPath))
        {
            string seed = File.Exists(PersonaPath) ? File.ReadAllText(PersonaPath) : builtin;
            AtomicText.Write(PersonaFile(DefaultPersonaId), seed);
            if (!File.Exists(PersonaPath)) AtomicText.Write(PersonaPath, seed);
            WriteIndex(new PersonaIndex
            {
                ActiveId = DefaultPersonaId,
                Items = [new PersonaItem { Id = DefaultPersonaId, Name = DefaultPersonaName, Builtin = true }]
            });
            return;
        }

        var index = ReadIndex();
        if (!index.Items.Any(x => x.Id == DefaultPersonaId))
            index.Items.Insert(0, new PersonaItem { Id = DefaultPersonaId, Name = DefaultPersonaName, Builtin = true });
        foreach (var item in index.Items.Where(x => x.Id == DefaultPersonaId)) item.Builtin = true;
        if (!File.Exists(PersonaFile(DefaultPersonaId))) AtomicText.Write(PersonaFile(DefaultPersonaId), builtin);
        if (!index.Items.Any(x => x.Id == index.ActiveId)) index.ActiveId = DefaultPersonaId;
        WriteIndex(index);
        if (!File.Exists(PersonaPath)) ApplyPersona(index.ActiveId);
    }

    private string PersonaFile(string id) => Path.Combine(PersonasDirectory, SanitizeId(id) + ".md");

    private PersonaIndex ReadIndex()
    {
        var index = JsonSerializer.Deserialize<PersonaIndex>(File.ReadAllText(IndexPath), IndexJson)
            ?? new PersonaIndex();
        if (string.IsNullOrWhiteSpace(index.ActiveId)) index.ActiveId = DefaultPersonaId;
        index.Items ??= [];
        return index;
    }

    private void WriteIndex(PersonaIndex index) => AtomicText.Write(IndexPath, JsonSerializer.Serialize(index, IndexJson));

    private static string NormalizeName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("请填写人设名称。");
        if (name.Length > 40) name = name[..40];
        return name;
    }

    private static string SanitizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("人设编号无效。");
        foreach (char c in id)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new ArgumentException("人设编号无效。");
        return id;
    }

    private static string Resource(string name)
    {
        using var stream = typeof(AgentSkillStore).Assembly.GetManifestResourceStream("BigBlueFish.Agent.Skills." + name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8); return reader.ReadToEnd();
    }

    private sealed class PersonaIndex
    {
        public string ActiveId { get; set; } = DefaultPersonaId;
        public List<PersonaItem> Items { get; set; } = [];
    }

    private sealed class PersonaItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Builtin { get; set; }
    }
}

public static class AtomicText
{
    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
