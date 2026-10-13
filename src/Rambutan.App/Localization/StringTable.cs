using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Rambutan.App.Localization;

/// <summary>
/// One locale's strings, read from a versioned JSON table:
/// <code>
/// { "version": 1, "locale": "en", "name": "English",
///   "strings": { "settings.title": "Settings", … } }
/// </code>
/// <c>strings</c> is flat: dotted keys, no nesting, so a table is one key→string
/// map to a translator or Crowdin. Read with <see cref="Utf8JsonReader"/>, not a
/// serializer, which keeps it NativeAOT-clean and lets it reject what the format
/// doesn't allow (nesting, non-string values) with a message naming the key.
/// </summary>
public sealed class StringTable
{
    /// <summary>The table format this build reads. A newer file is refused rather
    /// than half-read; an older one is migrated here when the format changes.</summary>
    public const int CurrentVersion = 1;

    public required string Locale { get; init; }

    /// <summary>The language's own name for itself ("English", "Français").</summary>
    public required string NativeName { get; init; }

    public required IReadOnlyDictionary<string, string> Strings { get; init; }

    public static StringTable Parse(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        int? version = null;
        string? locale = null;
        string? name = null;
        Dictionary<string, string>? strings = null;

        Read(ref reader);
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException("A string table is a JSON object.");

        while (Read(ref reader) && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = reader.GetString();
            Read(ref reader);
            switch (property)
            {
                case "version":
                    version = reader.GetInt32();
                    break;
                case "locale":
                    locale = reader.GetString();
                    break;
                case "name":
                    name = reader.GetString();
                    break;
                case "strings":
                    strings = ReadStrings(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (version is not { } v)
            throw new InvalidDataException("A string table needs a \"version\".");
        if (v > CurrentVersion)
            throw new InvalidDataException($"String table version {v} is newer than this build reads ({CurrentVersion}).");
        if (string.IsNullOrWhiteSpace(locale))
            throw new InvalidDataException("A string table needs a \"locale\".");

        return new StringTable
        {
            Locale = locale,
            NativeName = string.IsNullOrWhiteSpace(name) ? locale : name,
            Strings = strings ?? [],
        };
    }

    private static Dictionary<string, string> ReadStrings(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException("\"strings\" is an object of key → string.");

        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        while (Read(ref reader) && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString()!;
            Read(ref reader);
            if (reader.TokenType != JsonTokenType.String)
                throw new InvalidDataException($"\"{key}\" must be a string; tables are flat, dotted keys.");
            if (!strings.TryAdd(key, reader.GetString()!))
                throw new InvalidDataException($"\"{key}\" appears twice.");
        }

        return strings;
    }

    private static bool Read(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
            throw new InvalidDataException("The string table ends early.");
        return reader.TokenType != JsonTokenType.EndObject;
    }
}
