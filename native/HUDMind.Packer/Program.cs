using System.Text;
using System.Text.Json;
using HUDMind.Core.Security;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: HUDMind.Packer <profilesDir> <protectedJsonPath> <outVaultPath>");
    return 2;
}

string profilesDir = Path.GetFullPath(args[0]);
string protectedJson = Path.GetFullPath(args[1]);
string outPath = Path.GetFullPath(args[2]);

if (!Directory.Exists(profilesDir))
{
    Console.Error.WriteLine($"[HUDMind.Packer] Profiles directory not found: {profilesDir}");
    return 3;
}
if (!File.Exists(protectedJson))
{
    Console.Error.WriteLine($"[HUDMind.Packer] Protected strings JSON not found: {protectedJson}");
    return 4;
}

var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

foreach (var subDir in Directory.EnumerateDirectories(profilesDir))
{
    string gameId = Path.GetFileName(subDir).Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(gameId)) continue;

    foreach (var fileName in new[] { "manifest.json", "system_prompt.md", "knowledge_base.md" })
    {
        string fullFile = Path.Combine(subDir, fileName);
        if (!File.Exists(fullFile)) continue;
        entries[$"HUDMind.DefaultProfiles/{gameId}/{fileName}"] = File.ReadAllBytes(fullFile);
    }
}

using (var doc = JsonDocument.Parse(File.ReadAllText(protectedJson, Encoding.UTF8)))
{
    foreach (var prop in doc.RootElement.EnumerateObject())
    {
        var val = prop.Value.GetString();
        if (!string.IsNullOrEmpty(val))
            entries[$"ProtectedStrings/{prop.Name}"] = Encoding.UTF8.GetBytes(val);
    }
}

if (entries.Count == 0)
{
    Console.Error.WriteLine("[HUDMind.Packer] Zero assets collected; aborting empty vault.");
    return 5;
}

byte[] sealedBlob = ProfileVaultCodec.Seal(entries);
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
File.WriteAllBytes(outPath, sealedBlob);
Console.WriteLine($"[HUDMind.Packer] Sealed {entries.Count} protected assets -> {outPath} ({sealedBlob.Length:N0} bytes).");
return 0;
