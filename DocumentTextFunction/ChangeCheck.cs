using System.Text.Json.Nodes;

namespace DocumentTextFunction;

public static class ChangeCheck
{
    private const string Instruction =
        """
        Compare the change request with the live document. The files will not follow one template. A request may be a full policy, a section, a photo, a spreadsheet, a Word file, an RTF file, or a PDF. A change may be a sentence, a table row, a comment bubble, or an arrow.
        A change exists only where the request explicitly asks for a different value: a comment bubble, an arrow, the words "should be", an old/new pair, or a handwritten mark. An old row and a new row for the same field are one change.
        Do not list a field just because it is printed. "Must remain" is not a change. An unchanged policy number or name is not a change.
        A red, black, or solid bar covering text is a redaction, not a request to change that field. Ignore redacted names, addresses, document numbers, registration numbers, and amounts. Do not invent a change for a field you cannot read.
        Check every page of the change request. A mark on a later page counts the same as one on page 1.
        A PDF may also come with its text and comment bubbles. A line such as [Comment on page 2, pointing at "..."] is a bubble; the quoted words are what its arrow points at. Use these lines together with the page images so that no bubble is missed.
        When an arrow or bubble is visible, the target is the value at the arrow tip. Text marked [Text box] is a comment bubble. Include it as a requested change and tie it to the field whose current value it names.
        If a bubble asks for a change but does not name the field or the new value, for example it only says "change this", still include it. Set field to "Unidentified Field Changes", leave oldValue and newValue empty, set result to FAIL, and quote the bubble in notes.
        A change can replace text, add text, or remove text. An arrow from new words to the end of a sentence means those words are added there. For "Cover lasts for one year and the dates of cover are specified in your Policy Schedule." with an arrow to "on page 99", the sentence should read "Cover lasts for one year and the dates of cover are specified in your Policy Schedule on page 99."
        For each change, set expectedText to the complete sentence, table cell, or line as it should read in the live document once the change is made. Copy the surrounding words exactly from the change request and apply only the requested change. For a table, expectedText is only the value cell, without the label.
        Judge each change at the same place in the live document. Find the sentence or row with the same surrounding words and read it there. The same value appearing somewhere else in the live document does not count. If the live sentence still shows the old value, the result is FAIL.
        Compare the requested value with the live document as exact text. Spaces, punctuation, and capital letters matter. AR12AO is not the same as AR1 2AO. A value that was changed but misspelt, such as United Kingdon for United Kingdom, is FAIL; say what the live document shows in notes. Result is PASS only when the live document shows the requested text exactly. The wording inside the change request does not count as the live value.
        The changes array must contain only those requested changes. If the same field, old value, and new value appear more than once, return that change once.
        For each change, name the field, the value it had before, and the value it should have now.
        Return JSON only, in this shape:
        {"docRef":"1234567","summary":"3 changes identified. 1 applied.","status":"PARTIAL","confidence":85,"confidenceReason":"All three requests were clearly marked and every value was readable.","changes":[{"request":"Address must change from 123 Main Road, NR44 6TH to 123 Mainly Road, NR44 8TH","field":"Address","oldValue":"123 Main Road, NR44 6TH","newValue":"123 Mainly Road, NR44 8TH","expectedText":"123 Mainly Road, NR44 8TH","result":"FAIL","notes":"Live document still shows 123 Main Road, NR44 6TH"}]}
        status is PASS when every requested change has been made.
        status is FAIL when none of the requested changes have been made.
        status is PARTIAL when at least one requested change has been made and at least one has not.
        result is PASS or FAIL.
        confidence is an integer from 0 to 100 for how sure you are that you found every requested change and read each field, old value, and new value correctly. Lower it when a bubble is vague, an arrow is ambiguous, text is hard to read, or part of the request may be missing. Do not raise it because the result is good or bad.
        confidenceReason is one short sentence explaining the confidence.
        docRef is the reference that identifies the change request: a policy number, document number, reference number, or similar. Look in the change request first, then in its file name, which is shown in brackets after "Change request". Return only the reference itself, not its label. Do not guess a reference that is redacted. Use "Not Found" when there is none.
        """;

    public static async Task<JsonObject> CompareAsync(
        string requestName,
        byte[] requestBytes,
        string liveName,
        byte[] liveBytes,
        CancellationToken cancellationToken)
    {
        var content = new JsonArray();
        ChangeVision.AppendDocument(content, "Change request", requestName, requestBytes, visionOnly: false);
        ChangeVision.AppendDocument(content, "Live document", liveName, liveBytes, visionOnly: false);
        content.Add(new JsonObject { ["type"] = "text", ["text"] = Instruction });
        var model = await ChangeVision.CompleteAsync(content, cancellationToken);
        return Normalize(model, LiveText(liveName, liveBytes), requestName);
    }

    private static string? LiveText(string liveName, byte[] liveBytes)
    {
        try
        {
            var text = DocumentTextExtractor.Extract(liveName, liveBytes).Text;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static JsonObject Normalize(JsonObject model, string? liveText = null, string? requestName = null)
    {
        var live = liveText is null ? null : Squash(liveText);
        var changes = new JsonArray();
        var corrected = 0;
        var unidentifiedCount = 0;
        if (model["changes"] is JsonArray source)
        {
            foreach (var item in source)
            {
                if (item is not JsonObject change)
                    continue;
                var field = Text(change, "field", "fieldName");
                var unidentified = string.IsNullOrWhiteSpace(field)
                    || field.Contains("unidentified", StringComparison.OrdinalIgnoreCase)
                    || field.Equals("unknown", StringComparison.OrdinalIgnoreCase);
                if (unidentified)
                    field = "Unidentified Field Changes";
                var result = Text(change, "result", "status");
                var notes = Text(change, "notes", "note");
                if (unidentified && string.IsNullOrWhiteSpace(notes))
                    notes = "The comment could not be tied to a field.";
                var row = new JsonObject
                {
                    ["request"] = Text(change, "request", "change", "description", "bubble"),
                    ["field"] = field,
                    ["oldValue"] = unidentified ? "" : Text(change, "oldValue", "old", "from", "pointedAt"),
                    ["newValue"] = unidentified ? "" : Text(change, "newValue", "new", "to", "requestedValue"),
                    ["expectedText"] = unidentified ? "" : Text(change, "expectedText", "expected"),
                    ["result"] = unidentified || !result.Equals("PASS", StringComparison.OrdinalIgnoreCase) ? "FAIL" : "PASS",
                    ["notes"] = notes
                };
                var expected = row["expectedText"]!.GetValue<string>();
                var lookFor = expected.Length > 0 ? expected : row["newValue"]!.GetValue<string>();
                if (live is not null
                    && row["result"]!.GetValue<string>() == "PASS"
                    && lookFor.Length > 0
                    && !live.Contains(Squash(lookFor), StringComparison.Ordinal))
                {
                    row["result"] = "FAIL";
                    row["notes"] = MismatchNote(live, lookFor, row["oldValue"]!.GetValue<string>());
                    corrected++;
                }
                var key = unidentified
                    ? "unidentified|" + row["request"]!.GetValue<string>()
                    : field.ToLowerInvariant() + "|" + row["oldValue"]!.GetValue<string>() + "|" + row["newValue"]!.GetValue<string>();
                var existing = changes.FirstOrDefault(item => item?["key"]?.GetValue<string>() == key);
                if (existing is JsonObject prior)
                {
                    if (prior["result"]!.GetValue<string>() != row["result"]!.GetValue<string>())
                        corrected++;
                    if (prior["result"]!.GetValue<string>() == "PASS" && row["result"]!.GetValue<string>() == "FAIL")
                    {
                        prior["result"] = "FAIL";
                        prior["notes"] = row["notes"]!.GetValue<string>();
                        prior["request"] = row["request"]!.GetValue<string>();
                    }
                    continue;
                }

                if (unidentified)
                    unidentifiedCount++;
                row["key"] = key;
                changes.Add(row);
            }
        }

        foreach (var item in changes)
            item?.AsObject().Remove("key");

        var total = changes.Count;
        var passed = changes.Count(item => item?["result"]?.GetValue<string>() == "PASS");
        var status = passed == total && total > 0 ? "PASS" : passed > 0 ? "PARTIAL" : "FAIL";
        var changePercentCompleted = total == 0 ? 0 : (int)Math.Round(passed * 100.0 / total);
        var summary = total == 0
            ? "No requested changes were identified."
            : $"{total} changes identified. {passed} applied.";
        var (confidence, confidenceReason) = Confidence(model, corrected, unidentifiedCount);

        return new JsonObject
        {
            ["docRef"] = DocRef(model, requestName),
            ["summary"] = summary,
            ["status"] = status,
            ["outcome"] = status,
            ["changePercentCompleted"] = changePercentCompleted,
            ["confidence"] = confidence,
            ["confidenceReason"] = confidenceReason,
            ["model"] = Text(model, "model"),
            ["promptTokens"] = Count(model, "promptTokens"),
            ["completionTokens"] = Count(model, "completionTokens"),
            ["totalTokens"] = Count(model, "totalTokens"),
            ["estimatedCost"] = model["estimatedCost"] is JsonValue cost && cost.TryGetValue<decimal>(out var amount) ? amount : 0m,
            ["costCurrency"] = Text(model, "costCurrency") is { Length: > 0 } currency ? currency : "USD",
            ["changes"] = changes
        };
    }

    private static int Count(JsonObject model, string name) =>
        model[name] is JsonValue value && value.TryGetValue<int>(out var count) ? count : 0;

    private static (int Confidence, string Reason) Confidence(JsonObject model, int corrected, int unidentified)
    {
        int? reported = model["confidence"] is JsonValue value
            ? value.TryGetValue<int>(out var whole) ? whole
            : value.TryGetValue<double>(out var fraction) ? (int)Math.Round(fraction <= 1 ? fraction * 100 : fraction)
            : value.TryGetValue<string>(out var text) && double.TryParse(text.Trim().TrimEnd('%'), out var parsed) ? (int)Math.Round(parsed)
            : null
            : null;

        var confidence = Math.Clamp(reported ?? 50, 0, 100);
        var reasons = new List<string>();
        var modelReason = Text(model, "confidenceReason", "confidence_reason");
        reasons.Add(reported is null ? "The model did not report a confidence." : modelReason);

        if (corrected > 0)
        {
            confidence = Math.Min(confidence, 70);
            reasons.Add(corrected == 1
                ? "One model result was corrected by the exact text check."
                : $"{corrected} model results were corrected by the exact text check.");
        }

        if (unidentified > 0)
        {
            confidence = Math.Min(confidence, 60);
            reasons.Add(unidentified == 1
                ? "One comment could not be tied to a field."
                : $"{unidentified} comments could not be tied to a field.");
        }

        return (confidence, string.Join(" ", reasons.Where(reason => !string.IsNullOrWhiteSpace(reason))));
    }

    private static string MismatchNote(string live, string lookFor, string oldValue)
    {
        var words = Squash(lookFor).Split(' ');
        for (var count = words.Length - 1; count >= 3; count--)
        {
            var prefix = string.Join(' ', words.Take(count));
            var index = live.IndexOf(prefix, StringComparison.Ordinal);
            if (index < 0)
                continue;
            var length = Math.Min(live.Length - index, Squash(lookFor).Length + 20);
            var shown = live.Substring(index, length).Trim();
            return $"Expected \"{lookFor}\" but the live document shows \"{shown}\".";
        }

        if (oldValue.Length > 0 && live.Contains(Squash(oldValue), StringComparison.Ordinal))
            return $"Expected \"{lookFor}\" but the live document still shows \"{oldValue}\".";

        return $"The live document does not contain \"{lookFor}\" exactly.";
    }

    private const string DocRefNotFound = "Not Found";

    private static string DocRef(JsonObject model, string? requestName)
    {
        var reference = Text(model, "docRef", "DocRef", "documentReference", "reference");
        var missing = reference.Length == 0
            || reference.Equals(DocRefNotFound, StringComparison.OrdinalIgnoreCase)
            || reference.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || reference.Equals("n/a", StringComparison.OrdinalIgnoreCase)
            || reference.Equals("none", StringComparison.OrdinalIgnoreCase);
        if (missing)
            reference = ReferenceFromFileName(requestName) ?? DocRefNotFound;

        reference = Squash(reference);
        return reference.Length > 50 ? reference[..50].TrimEnd() : reference;
    }

    private static string? ReferenceFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var tokens = name.Split(
            new[] { ' ', '_', '.', '(', ')', '[', ']', ',' },
            StringSplitOptions.RemoveEmptyEntries);
        return tokens.FirstOrDefault(token => token.Count(char.IsDigit) >= 4);
    }

    private static string Squash(string value)
    {
        var plain = value
            .Replace('\u2018', '\'').Replace('\u2019', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"')
            .Replace('\u2013', '-').Replace('\u2014', '-')
            .Replace('\u00A0', ' ');
        return string.Join(' ', plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Text(JsonObject change, params string[] names)
    {
        foreach (var name in names)
        {
            if (change[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return "";
    }
}
