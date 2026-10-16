/* Copyright (c) 2024 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

// Ported from SAM.Game/KeyValue.cs, KeyValueType.cs and StreamHelpers.cs (modified).

using System.Globalization;
using System.Text;

namespace SAM.App.Services;

internal enum KeyValueType : byte
{
    None = 0,
    String = 1,
    Int32 = 2,
    Float32 = 3,
    Pointer = 4,
    WideString = 5,
    Color = 6,
    UInt64 = 7,
    End = 8,
}

/// <summary>A node of Valve's binary KeyValues format, as used by appcache\stats\*.bin.</summary>
internal sealed class KeyValue
{
    private static readonly KeyValue Invalid = new();

    public string Name { get; private set; } = "<root>";

    public KeyValueType Type { get; private set; } = KeyValueType.None;

    public object? Value { get; private set; }

    public bool Valid { get; private set; }

    public List<KeyValue>? Children { get; private set; }

    /// <summary>First child whose name matches <paramref name="key"/> case-insensitively, or an invalid node.</summary>
    public KeyValue this[string key]
    {
        get
        {
            if (this.Children == null)
            {
                return Invalid;
            }

            foreach (var child in this.Children)
            {
                if (string.Equals(child.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }

            return Invalid;
        }
    }

    public string AsString(string defaultValue)
    {
        if (this.Valid == false || this.Value == null)
        {
            return defaultValue;
        }

        return Convert.ToString(this.Value, CultureInfo.InvariantCulture) ?? defaultValue;
    }

    public int AsInteger(int defaultValue)
    {
        if (this.Valid == false)
        {
            return defaultValue;
        }

        return this.Type switch
        {
            KeyValueType.String or KeyValueType.WideString =>
                int.TryParse((string)this.Value!, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : defaultValue,
            KeyValueType.Int32 => (int)this.Value!,
            KeyValueType.Float32 => (int)(float)this.Value!,
            KeyValueType.UInt64 => (int)((ulong)this.Value! & 0xFFFFFFFF),
            KeyValueType.Color or KeyValueType.Pointer => unchecked((int)(uint)this.Value!),
            _ => defaultValue,
        };
    }

    public float AsFloat(float defaultValue)
    {
        if (this.Valid == false)
        {
            return defaultValue;
        }

        return this.Type switch
        {
            KeyValueType.String or KeyValueType.WideString =>
                float.TryParse((string)this.Value!, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : defaultValue,
            KeyValueType.Int32 => (int)this.Value!,
            KeyValueType.Float32 => (float)this.Value!,
            KeyValueType.UInt64 => (ulong)this.Value! & 0xFFFFFFFF,
            _ => defaultValue,
        };
    }

    public double AsDouble(double defaultValue)
    {
        if (this.Valid == false)
        {
            return defaultValue;
        }

        return this.Type switch
        {
            KeyValueType.String or KeyValueType.WideString =>
                double.TryParse((string)this.Value!, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : defaultValue,
            KeyValueType.Int32 => (int)this.Value!,
            KeyValueType.Float32 => (float)this.Value!,
            KeyValueType.UInt64 => (ulong)this.Value!,
            _ => defaultValue,
        };
    }

    public bool AsBoolean(bool defaultValue)
    {
        if (this.Valid == false)
        {
            return defaultValue;
        }

        return this.Type switch
        {
            KeyValueType.String or KeyValueType.WideString =>
                int.TryParse((string)this.Value!, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value != 0
                    : defaultValue,
            KeyValueType.Int32 => (int)this.Value! != 0,
            KeyValueType.Float32 => (int)(float)this.Value! != 0,
            KeyValueType.UInt64 => (ulong)this.Value! != 0,
            _ => defaultValue,
        };
    }

    public override string ToString()
    {
        if (this.Valid == false)
        {
            return "<invalid>";
        }

        return this.Type == KeyValueType.None ? this.Name : $"{this.Name} = {this.Value}";
    }

    /// <summary>Reads a binary KeyValues file. Returns null if it's missing or malformed. Never throws.</summary>
    public static KeyValue? LoadAsBinary(string path)
    {
        try
        {
            if (File.Exists(path) == false)
            {
                return null;
            }

            // Read the whole file first: Steam may rewrite it while we're reading, and the
            // byte-at-a-time parser is much faster against memory than against a FileStream.
            byte[] bytes;
            using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[file.Length];
                file.ReadExactly(bytes);
            }

            using var input = new MemoryStream(bytes, false);
            var kv = new KeyValue();
            return kv.ReadAsBinary(input) ? kv : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads this node's children from <paramref name="input"/>. False if the data is malformed.</summary>
    public bool ReadAsBinary(Stream input)
    {
        try
        {
            this.ReadChildren(input);
            return input.Position == input.Length;
        }
        catch (Exception e) when (e is FormatException or EndOfStreamException)
        {
            return false;
        }
    }

    private void ReadChildren(Stream input)
    {
        this.Children = [];
        while (true)
        {
            var type = (KeyValueType)ReadU8(input);

            if (type == KeyValueType.End)
            {
                break;
            }

            var current = new KeyValue
            {
                Type = type,
                Name = ReadString(input),
            };

            switch (type)
            {
                case KeyValueType.None:
                    current.ReadChildren(input);
                    break;

                case KeyValueType.String:
                    current.Valid = true;
                    current.Value = ReadString(input);
                    break;

                case KeyValueType.WideString:
                    throw new FormatException("wstring is unsupported");

                case KeyValueType.Int32:
                    current.Valid = true;
                    current.Value = BitConverter.ToInt32(ReadBytes(input, 4));
                    break;

                case KeyValueType.UInt64:
                    current.Valid = true;
                    current.Value = BitConverter.ToUInt64(ReadBytes(input, 8));
                    break;

                case KeyValueType.Float32:
                    current.Valid = true;
                    current.Value = BitConverter.ToSingle(ReadBytes(input, 4));
                    break;

                case KeyValueType.Color:
                case KeyValueType.Pointer:
                    current.Valid = true;
                    current.Value = BitConverter.ToUInt32(ReadBytes(input, 4));
                    break;

                default:
                    throw new FormatException($"Unknown KeyValue type {(byte)type}");
            }

            if (input.Position >= input.Length)
            {
                throw new FormatException("Unexpected end of KeyValues data");
            }

            this.Children.Add(current);
        }

        this.Valid = true;
    }

    private static byte ReadU8(Stream input)
    {
        var value = input.ReadByte();
        return value < 0 ? throw new EndOfStreamException() : (byte)value;
    }

    private static byte[] ReadBytes(Stream input, int count)
    {
        var data = new byte[count];
        input.ReadExactly(data);
        return data;
    }

    private static string ReadString(Stream input)
    {
        var buffer = new List<byte>(32);
        while (true)
        {
            var value = ReadU8(input);
            if (value == 0)
            {
                break;
            }

            buffer.Add(value);
        }

        return buffer.Count == 0 ? "" : Encoding.UTF8.GetString(buffer.ToArray());
    }
}
