using System.Text;

namespace SAM.App.Tests;

/// <summary>A node for building binary KeyValues fixtures: Value is a string, int, float, ulong, or child nodes.</summary>
internal sealed record Kv(string Name, object Value)
{
    public static Kv Node(string name, params Kv[] children) => new(name, children);

    /// <summary>Serializes <paramref name="roots"/> as a binary KeyValues file body (children, then End).</summary>
    public static byte[] Serialize(params Kv[] roots)
    {
        using var stream = new MemoryStream();
        foreach (var root in roots)
        {
            root.Write(stream);
        }

        stream.WriteByte(8);
        return stream.ToArray();
    }

    private void Write(Stream output)
    {
        switch (this.Value)
        {
            case Kv[] children:
                output.WriteByte(0);
                WriteString(output, this.Name);
                foreach (var child in children)
                {
                    child.Write(output);
                }

                output.WriteByte(8);
                break;

            case string text:
                output.WriteByte(1);
                WriteString(output, this.Name);
                WriteString(output, text);
                break;

            case int number:
                output.WriteByte(2);
                WriteString(output, this.Name);
                output.Write(BitConverter.GetBytes(number));
                break;

            case float number:
                output.WriteByte(3);
                WriteString(output, this.Name);
                output.Write(BitConverter.GetBytes(number));
                break;

            case ulong number:
                output.WriteByte(7);
                WriteString(output, this.Name);
                output.Write(BitConverter.GetBytes(number));
                break;

            default:
                throw new ArgumentException($"Unsupported value {this.Value}");
        }
    }

    private static void WriteString(Stream output, string value)
    {
        output.Write(Encoding.UTF8.GetBytes(value));
        output.WriteByte(0);
    }
}

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SAM.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.Path);
    }

    public string Path { get; }

    /// <summary>Writes <paramref name="content"/> to a path relative to this directory, creating folders.</summary>
    public string Write(string relativePath, string content)
    {
        var path = this.Prepare(relativePath);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public string Write(string relativePath, byte[] content)
    {
        var path = this.Prepare(relativePath);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.Path, true);
        }
        catch (IOException)
        {
        }
    }

    private string Prepare(string relativePath)
    {
        var path = System.IO.Path.Combine(this.Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return path;
    }
}

/// <summary>A fact that only runs when SAM_LOCAL_TESTS=1 (it reads the real Steam install, read-only).</summary>
internal sealed class LocalFactAttribute : FactAttribute
{
    public LocalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SAM_LOCAL_TESTS") != "1")
        {
            this.Skip = "Reads the local Steam install; set SAM_LOCAL_TESTS=1 to run.";
        }
    }
}
