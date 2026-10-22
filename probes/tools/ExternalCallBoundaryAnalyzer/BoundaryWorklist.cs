using System.Text.Json;

internal static class BoundaryWorklist
{
    private static readonly string[] KeyFields =
    [
        "CallerAssembly", "CallerType", "CallerMember", "Instruction",
        "ExternalAssembly", "ExternalType", "ExternalMember", "Resolved"
    ];

    private static readonly string[] ReviewFields = ["Status", "Codes", "Evidence", "Note"];
    private static readonly string[] DefaultFields = ["ExternalAssembly", "ExternalType", "ExternalMember"];
    private static readonly string[] Statuses = ["Unreviewed", "Covered", "NotApplicable", "Implemented", "Deferred"];

    /// <summary>Validates saved data, writes a worklist and removes confirmed stale decisions on request.</summary>
    /// <param name="arguments">Report path, review path, limit and optional output mode.</param>
    /// <exception cref="ArgumentException">Arguments or output modes are invalid.</exception>
    /// <exception cref="InvalidDataException">The report or review violates its data contract.</exception>
    /// <exception cref="IOException">An input file cannot be read.</exception>
    /// <exception cref="JsonException">An input file contains invalid JSON.</exception>
    public static void Run(string[] arguments)
    {
        if (arguments.Length is not (3 or 4) ||
            (arguments.Length == 4 && arguments[3] is not
                ("--include-reviewed" or "--orphans" or "--validate" or "--remove-not-applicable-orphans")))
            throw new ArgumentException("Worklist requires report, review and limit arguments.");

        if (!int.TryParse(arguments[2], out var limit) || limit <= 0)
            throw new ArgumentException("Limit must be greater than zero");

        if (!File.Exists(arguments[0]))
            throw new FileNotFoundException($"External call boundary report was not found: {arguments[0]}");

        var mode = arguments.Length == 4 ? arguments[3] : "";
        var includeReviewed = mode == "--include-reviewed";

        if (mode is "--validate" or "--remove-not-applicable-orphans" && !File.Exists(arguments[1]))
            throw new FileNotFoundException($"External call boundary review was not found: {arguments[1]}");

        using var review = JsonDocument.Parse(File.Exists(arguments[1])
            ? File.ReadAllText(arguments[1]) : "{\"Version\":2,\"Defaults\":[],\"Reviewed\":[]}");
        var version = Property(review.RootElement, "Version");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 2)
            throw new InvalidDataException($"Unsupported external call boundary review version: {Text(version)}; expected: 2");
        var defaults = ReadDecisions(review.RootElement, "Defaults", DefaultKey, "Default");
        var reviewed = ReadDecisions(review.RootElement, "Reviewed", BoundaryKey, "Reviewed boundary");
        var unseenDefaults = defaults.Keys.ToHashSet(StringComparer.Ordinal);
        var unseenReviewed = reviewed.Keys.ToHashSet(StringComparer.Ordinal);
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);

        var comparer = Comparer<Item>.Create(Compare);
        var selected = new PriorityQueue<Item, Item>(Comparer<Item>.Create((left, right) => Compare(right, left)));

        ReadReport(arguments[0], boundary =>
        {
            var sortKey = SortKey(boundary);
            var boundaryKey = string.Join('\n', sortKey);
            var defaultKey = DefaultKey(boundary);
            unseenReviewed.Remove(boundaryKey);

            var resolved = Property(boundary, "Resolved").GetBoolean();

            if (resolved) unseenDefaults.Remove(defaultKey);

            if (mode is "--orphans" or "--validate" or "--remove-not-applicable-orphans") return;

            reviewed.TryGetValue(boundaryKey, out var decision);

            if (resolved)
            {
                if (!groups.TryGetValue(defaultKey, out var group))
                {
                    defaults.TryGetValue(defaultKey, out var defaultDecision);
                    groups.Add(defaultKey, group = new Group(
                        DefaultFields.Select(field => Text(Property(boundary, field))).ToArray(), defaultDecision));
                }

                group.BoundaryCount++;
                if (decision.ValueKind != JsonValueKind.Undefined)
                {
                    group.OverrideCount++;
                    if (includeReviewed) group.Overrides.TryAdd(boundaryKey, decision);
                }
                else decision = group.Review;

                if (IsPending(decision))
                {
                    group.PendingCount++;
                    var representative = new Item(boundary, decision, sortKey);
                    if (group.Representatives.Count < 3 || Compare(representative, group.Representatives[^1]) < 0)
                    {
                        group.Representatives.Add(representative with { Boundary = boundary.Clone() });
                        group.Representatives.Sort(Compare);
                        if (group.Representatives.Count > 3) group.Representatives.RemoveAt(3);
                    }
                }

                return;
            }

            if (!includeReviewed && !IsPending(decision)) return;

            var item = new Item(boundary, decision, sortKey);

            if (selected.Count == limit && Compare(item, selected.Peek()) >= 0) return;

            item = item with { Boundary = boundary.Clone() };

            if (selected.Count == limit) selected.Dequeue();

            selected.Enqueue(item, item);
        });

        if (mode == "--validate") return;

        if (mode == "--remove-not-applicable-orphans")
        {
            var removedReviewed = unseenReviewed.Count(key => IsNotApplicable(reviewed[key]));
            var removedDefaults = unseenDefaults.Count(key => IsNotApplicable(defaults[key]));

            if (removedReviewed + removedDefaults > 0)
                WriteReviewWithoutNotApplicableOrphans(arguments[1], review.RootElement, unseenReviewed, unseenDefaults);

            using var result = new Utf8JsonWriter(Console.OpenStandardOutput());
            result.WriteStartObject();
            result.WriteNumber("Reviewed", removedReviewed);
            result.WriteNumber("Defaults", removedDefaults);
            result.WriteEndObject();

            return;
        }

        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput());
        writer.WriteStartArray();

        if (mode == "--orphans")
        {
            foreach (var key in unseenReviewed.Order(StringComparer.Ordinal)) reviewed[key].WriteTo(writer);
            foreach (var key in unseenDefaults.Order(StringComparer.Ordinal)) defaults[key].WriteTo(writer);
        }
        else
        {
            var remaining = limit;

            foreach (var group in groups.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Value))
            {
                if (!includeReviewed && group.PendingCount == 0) continue;
                if (remaining-- == 0) break;
                WriteGroup(writer, group, includeReviewed);
            }

            foreach (var item in selected.UnorderedItems.Select(entry => entry.Element).Order(comparer))
            {
                if (remaining-- <= 0) break;
                WriteBoundary(writer, item);
            }
        }

        writer.WriteEndArray();
    }

    private static Dictionary<string, JsonElement> ReadDecisions(
        JsonElement document, string collection, Func<JsonElement, string> keyOf, string context)
    {
        var entries = Property(document, collection);
        if (entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"External call boundary review must contain {collection} array");
        var decisions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            if (collection == "Reviewed") ValidateBoundary(entry, context);
            else foreach (var field in DefaultFields) RequireText(entry, field, context);

            var key = keyOf(entry);
            var status = Property(entry, "Status");

            if (status.ValueKind != JsonValueKind.String ||
                !Statuses.Contains(status.GetString(), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"{context} contains unknown status '{Text(status)}'");

            var codes = Property(entry, "Codes");
            if (codes.ValueKind != JsonValueKind.Array ||
                codes.EnumerateArray().Any(code => code.ValueKind != JsonValueKind.String))
                throw new InvalidDataException($"{context} Codes must be an array of strings: {key}");

            if (codes.EnumerateArray().Select(code => code.GetString()!).Distinct(StringComparer.Ordinal).Count() !=
                codes.GetArrayLength())
                throw new InvalidDataException($"{context} Codes must not contain duplicates: {key}");

            var evidence = Property(entry, "Evidence");
            if (evidence.ValueKind != JsonValueKind.Array ||
                evidence.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new InvalidDataException($"{context} Evidence must be an array of strings: {key}");

            if (Property(entry, "Note").ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"{context} Note must be a string: {key}");

            if (!decisions.TryAdd(key, entry))
                throw new InvalidDataException($"Duplicate {context.ToLowerInvariant()}: {key}");
        }
        return decisions;
    }

    private static bool IsPending(JsonElement decision) => decision.ValueKind == JsonValueKind.Undefined ||
        string.Equals(Text(Property(decision, "Status")), "Unreviewed", StringComparison.OrdinalIgnoreCase);

    private static bool IsNotApplicable(JsonElement decision) =>
        string.Equals(Text(Property(decision, "Status")), "NotApplicable", StringComparison.OrdinalIgnoreCase);

    private static void WriteReviewWithoutNotApplicableOrphans(
        string path, JsonElement review, HashSet<string> unseenReviewed, HashSet<string> unseenDefaults)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporaryPath))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in review.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Name == "Reviewed")
                        WriteRemainingDecisions(writer, property.Value, unseenReviewed, BoundaryKey);
                    else if (property.Name == "Defaults")
                        WriteRemainingDecisions(writer, property.Value, unseenDefaults, DefaultKey);
                    else property.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void WriteRemainingDecisions(
        Utf8JsonWriter writer, JsonElement decisions, HashSet<string> unseen, Func<JsonElement, string> keyOf)
    {
        writer.WriteStartArray();
        foreach (var decision in decisions.EnumerateArray())
            if (!unseen.Contains(keyOf(decision)) || !IsNotApplicable(decision)) decision.WriteTo(writer);
        writer.WriteEndArray();
    }

    private static void WriteGroup(Utf8JsonWriter writer, Group group, bool includeReviewed)
    {
        writer.WriteStartObject();
        for (var index = 0; index < DefaultFields.Length; index++)
            writer.WriteString(DefaultFields[index], group.Key[index]);
        writer.WriteBoolean("Resolved", true);
        writer.WriteNumber("BoundaryCount", group.BoundaryCount);
        writer.WriteNumber("PendingCount", group.PendingCount);
        writer.WriteStartArray("Representatives");
        foreach (var representative in group.Representatives) WriteBoundary(writer, representative);
        writer.WriteEndArray();
        if (includeReviewed)
        {
            writer.WriteNumber("OverrideCount", group.OverrideCount);
            writer.WriteStartArray("Overrides");
            foreach (var decision in group.Overrides.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                decision.Value.WriteTo(writer);
            writer.WriteEndArray();
        }
        foreach (var field in ReviewFields)
        {
            writer.WritePropertyName(field);
            WriteReviewField(writer, group.PendingCount == 0 ? group.Review : default, field);
        }
        writer.WriteEndObject();
    }

    private static void WriteBoundary(Utf8JsonWriter writer, Item item)
    {
        writer.WriteStartObject();
        foreach (var property in item.Boundary.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (ReviewFields.Contains(property.Name, StringComparer.Ordinal))
                WriteReviewField(writer, item.Review, property.Name);
            else property.Value.WriteTo(writer);
        }
        foreach (var field in ReviewFields)
        {
            if (item.Boundary.TryGetProperty(field, out _)) continue;
            writer.WritePropertyName(field);
            WriteReviewField(writer, item.Review, field);
        }
        writer.WriteEndObject();
    }

    private static void ReadReport(string path, Action<JsonElement> visit)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[65536];
        var buffered = stream.Read(buffer, 0, 3);
        if (buffered == 3 && buffer.AsSpan(0, 3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf })) buffered = 0;
        var state = new JsonReaderState();
        var property = "";
        var schemaSeen = false;
        var boundariesSeen = false;
        long? declaredCount = null;
        long boundaryCount = 0;
        var inBoundaries = false;
        while (true)
        {
            var read = stream.Read(buffer, buffered, buffer.Length - buffered);
            var final = read == 0;
            buffered += read;
            var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), final, state);
            while (true)
            {
                var checkpoint = reader;
                if (!reader.Read()) break;
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    property = reader.GetString()!;
                    continue;
                }
                if (reader.CurrentDepth == 0 && reader.TokenType != JsonTokenType.StartObject &&
                    reader.TokenType != JsonTokenType.EndObject)
                    throw new InvalidDataException("External call boundary report must be an object");
                if (reader.CurrentDepth == 1 && property == "SchemaVersion")
                {
                    if (schemaSeen)
                        throw new InvalidDataException("External call boundary report contains duplicate SchemaVersion");
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var schema) || schema != 2)
                        throw new InvalidDataException("Unsupported external call boundary report schema version: " +
                            (reader.TokenType == JsonTokenType.String ? reader.GetString() :
                                System.Text.Encoding.UTF8.GetString(reader.ValueSpan)) + "; expected: 2");
                    schemaSeen = true;
                }
                if (reader.CurrentDepth == 1 && property == "BoundaryCount")
                {
                    if (declaredCount is not null)
                        throw new InvalidDataException("External call boundary report contains duplicate BoundaryCount");
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var count) || count < 0)
                        throw new InvalidDataException("External call boundary report BoundaryCount must be a nonnegative integer");
                    declaredCount = count;
                }
                if (reader.CurrentDepth == 1 && property == "Boundaries")
                {
                    if (reader.TokenType != JsonTokenType.StartArray)
                        throw new InvalidDataException("External call boundary report must contain Boundaries array");
                    if (boundariesSeen)
                        throw new InvalidDataException("External call boundary report contains duplicate Boundaries array");
                    boundariesSeen = true;
                    inBoundaries = true;
                }
                if (reader.CurrentDepth == 1) property = "";
                if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 1) inBoundaries = false;
                if (!inBoundaries || reader.CurrentDepth != 2) continue;
                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new InvalidDataException("External call boundary report Boundaries must contain objects");
                if (!JsonDocument.TryParseValue(ref reader, out var document))
                {
                    reader = checkpoint;
                    break;
                }
                using (document)
                {
                    ValidateBoundary(document.RootElement, "External boundary");
                    boundaryCount++;
                    visit(document.RootElement);
                }
            }
            var consumed = checked((int)reader.BytesConsumed);
            state = reader.CurrentState;
            buffered -= consumed;
            Buffer.BlockCopy(buffer, consumed, buffer, 0, buffered);
            if (final) break;
            if (buffered == buffer.Length) Array.Resize(ref buffer, checked(buffer.Length * 2));
        }
        if (!schemaSeen)
            throw new InvalidDataException("Unsupported external call boundary report schema version: ; expected: 2");
        if (!boundariesSeen)
            throw new InvalidDataException("External call boundary report must contain Boundaries array");
        if (declaredCount is null)
            throw new InvalidDataException("External call boundary report must contain BoundaryCount");
        if (declaredCount != boundaryCount)
            throw new InvalidDataException("External call boundary report BoundaryCount does not match Boundaries.Count");
    }

    private static void ValidateBoundary(JsonElement boundary, string context)
    {
        var resolved = Property(boundary, "Resolved");
        if (resolved.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"{context} Resolved must be a boolean");
        foreach (var field in KeyFields[..^1])
            RequireText(boundary, field, context,
                resolved.ValueKind == JsonValueKind.False && DefaultFields.Contains(field));
        if (resolved.ValueKind == JsonValueKind.True) return;
        foreach (var field in new[] { "IlOffset", "MetadataToken" })
        {
            var value = Property(boundary, field);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0)
                throw new InvalidDataException($"{context} {field} must be a nonnegative integer");
        }
    }

    private static void RequireText(JsonElement record, string field, string context, bool allowEmpty = false)
    {
        var value = Property(record, field);
        if (value.ValueKind != JsonValueKind.String || (!allowEmpty && string.IsNullOrWhiteSpace(value.GetString())))
            throw new InvalidDataException($"{context} must contain key field {field}");
    }

    private static string DefaultKey(JsonElement record) =>
        string.Join('\n', DefaultFields.Select(field => Text(Property(record, field))));

    private static string BoundaryKey(JsonElement record) => string.Join('\n', SortKey(record));

    private static string[] SortKey(JsonElement record)
    {
        var resolved = Property(record, "Resolved").ValueKind == JsonValueKind.True;
        var parts = KeyFields.Select(field => Text(Property(record, field))).ToList();
        parts[^1] = resolved.ToString();
        if (!resolved)
        {
            parts.Add(Text(Property(record, "IlOffset")));
            parts.Add(Text(Property(record, "MetadataToken")));
        }
        return parts.ToArray();
    }

    private static int Compare(Item left, Item right)
    {
        for (var index = 0; index < left.SortKey.Length; index++)
        {
            var comparison = StringComparer.Ordinal.Compare(left.SortKey[index], right.SortKey[index]);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static JsonElement Property(JsonElement record, string name) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var value) ? value : default;

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => "",
        JsonValueKind.String => value.GetString()!,
        _ => value.ToString()
    };

    private static void WriteReviewField(Utf8JsonWriter writer, JsonElement review, string field)
    {
        if (review.ValueKind == JsonValueKind.Undefined)
        {
            if (field is "Codes" or "Evidence")
            {
                writer.WriteStartArray();
                writer.WriteEndArray();
            }
            else writer.WriteStringValue(field == "Status" ? "Unreviewed" : "");
            return;
        }
        var value = Property(review, field);
        if (value.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
        else value.WriteTo(writer);
    }

    private sealed record Item(JsonElement Boundary, JsonElement Review, string[] SortKey);

    private sealed class Group(string[] key, JsonElement review)
    {
        public string[] Key { get; } = key;
        public JsonElement Review { get; } = review;
        public int BoundaryCount { get; set; }
        public int PendingCount { get; set; }
        public int OverrideCount { get; set; }
        public List<Item> Representatives { get; } = new();
        public Dictionary<string, JsonElement> Overrides { get; } = new(StringComparer.Ordinal);
    }
}
