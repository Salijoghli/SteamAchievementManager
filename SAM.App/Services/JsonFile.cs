using System.Text.Json;

namespace SAM.App.Services;

/// <summary>Small helpers for reading/writing JSON files atomically. Read failures return default.</summary>
public static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T? Read<T>(string path)
    {
        try
        {
            if (File.Exists(path) == false)
            {
                return default;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"Couldn't read {path}", e);
            return default;
        }
    }

    public static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log($"Couldn't write {path}", e);
        }
    }
}
