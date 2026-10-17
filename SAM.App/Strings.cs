using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Windows.ApplicationModel.Resources;
using SAM.App.Services;

namespace SAM.App;

/// <summary>
/// Localized UI text from Strings\&lt;language&gt;\Resources.resw (compiled into SAM.pri), in the
/// language chosen by <see cref="AppSettings.Language"/>. XAML uses x:Uid for the same resources.
/// </summary>
/// <remarks>
/// Never throws: when the resource system isn't available (e.g. in unit tests) the English text
/// embedded in the assembly is used, and an unknown key returns the key itself (logged once).
/// </remarks>
public static class Strings
{
    private const string EnglishResourceName = "SAM.App.Strings.en-US.Resources.resw";

    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> ReportedMissing = new(StringComparer.Ordinal);
    private static readonly Lazy<ResourceLoader?> Loader = new(CreateLoader);
    private static readonly Lazy<IReadOnlyDictionary<string, string>> English = new(LoadEnglish);

    /// <summary>The text for <paramref name="key"/>, or the key itself when there is none.</summary>
    public static string Get(string key) => Cache.GetOrAdd(key, Lookup);

    /// <summary>
    /// <see cref="Get"/>, then <see cref="string.Format(IFormatProvider, string, object[])"/> in the
    /// current culture. A text whose placeholders don't match <paramref name="args"/> is returned as-is.
    /// </summary>
    public static string Format(string key, params object?[] args)
    {
        var format = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException e)
        {
            Report(key, $"Bad format string for \"{key}\"", e);
            return format;
        }
    }

    /// <summary>
    /// A text with a count: <c>&lt;key&gt;_One</c> when <paramref name="count"/> is 1, otherwise
    /// <c>&lt;key&gt;_Other</c>. {0} is the count (with digit grouping), {1}… are <paramref name="args"/>.
    /// Languages without a plural form (e.g. Georgian) use the same text for both keys.
    /// </summary>
    public static string Plural(string key, long count, params object?[] args)
    {
        var all = new object?[args.Length + 1];
        all[0] = count.ToString("N0", CultureInfo.CurrentCulture);
        args.CopyTo(all, 1);
        return Format(count == 1 ? key + "_One" : key + "_Other", all);
    }

    private static string Lookup(string key)
    {
        if (Loader.Value is { } loader)
        {
            try
            {
                var value = loader.GetString(key);
                if (string.IsNullOrEmpty(value) == false)
                {
                    return value;
                }
            }
            catch (Exception)
            {
                // Not found: fall through to the embedded English text.
            }
        }

        if (English.Value.TryGetValue(key, out var english))
        {
            return english;
        }

        Report(key, $"Missing UI text \"{key}\"", null);
        return key;
    }

    private static void Report(string key, string message, Exception? exception)
    {
        if (ReportedMissing.TryAdd(key, 0))
        {
            Diagnostics.Log(message, exception);
        }
    }

    private static ResourceLoader? CreateLoader()
    {
        try
        {
            return new ResourceLoader();
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't load the UI text resources; using English", e);
            return null;
        }
    }

    /// <summary>The en-US .resw, embedded so text works even without the resource system.</summary>
    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = typeof(Strings).Assembly.GetManifestResourceStream(EnglishResourceName);
            if (stream != null)
            {
                foreach (var data in XDocument.Load(stream).Root?.Elements("data") ?? [])
                {
                    if (data.Attribute("name")?.Value is { } name && data.Element("value")?.Value is { } value)
                    {
                        // x:Uid property keys ("Foo.Text") are looked up as "Foo/Text".
                        strings[name.Replace('.', '/')] = value;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Diagnostics.Log("Couldn't read the embedded English UI text", e);
        }

        return strings;
    }
}
