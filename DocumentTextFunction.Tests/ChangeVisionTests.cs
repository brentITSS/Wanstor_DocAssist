using DocumentTextFunction;
using Xunit;

namespace DocumentTextFunction.Tests;

public class ChangeVisionTests
{
    [Fact]
    public void Model_json_keeps_the_field_the_arrow_points_at()
    {
        const string payload = """
            {"choices":[{"message":{"content":"{\"changes\":[{\"page\":1,\"field\":\"Country\",\"pointedAt\":\"UK\",\"bubble\":\"Should be United Kingdom, not UK\",\"requestedValue\":\"United Kingdom\"}]}"}}]}
            """;

        var result = ChangeVision.ParseModelContent(payload);
        var change = result["changes"]![0]!;

        Assert.Equal("Country", change["field"]!.GetValue<string>());
        Assert.Equal("UK", change["pointedAt"]!.GetValue<string>());
        Assert.Equal("United Kingdom", change["requestedValue"]!.GetValue<string>());
    }

    [Fact]
    public void Mixed_results_become_partial()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {
              "summary": "3 changes identified. 1 applied.",
              "status": "pass",
              "changes": [
                {"request":"Name must change from James to John","field":"Name","oldValue":"James","newValue":"John","result":"PASS","notes":"Live document shows John"},
                {"change":"Address must change from 3 Main Rd to 3 Main Road","field":"Address","old":"3 Main Rd","new":"3 Main Road","status":"no","note":"Old address remains"},
                {"request":"Registration must change from AO10gyv to AO10GYV","result":"FAIL","notes":"Old registration remains"}
              ]
            }
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model);

        Assert.Equal("PARTIAL", result["status"]!.GetValue<string>());
        Assert.Equal("PARTIAL", result["outcome"]!.GetValue<string>());
        Assert.Equal(33, result["changePercentCompleted"]!.GetValue<int>());
        Assert.Null(result["score"]);
        Assert.Equal(3, result["changes"]!.AsArray().Count);
        Assert.Equal("Name", result["changes"]![0]!["field"]!.GetValue<string>());
        Assert.Equal("James", result["changes"]![0]!["oldValue"]!.GetValue<string>());
        Assert.Equal("John", result["changes"]![0]!["newValue"]!.GetValue<string>());
        Assert.Equal("PASS", result["changes"]![0]!["result"]!.GetValue<string>());
        Assert.Equal("3 Main Road", result["changes"]![1]!["newValue"]!.GetValue<string>());
        Assert.Equal("FAIL", result["changes"]![1]!["result"]!.GetValue<string>());
    }

    [Fact]
    public void All_requested_changes_pass()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"changes":[{"request":"Name must change from James to John","field":"Name","oldValue":"James","newValue":"John","result":"PASS","notes":"Shows John"},{"request":"Address must change","field":"Address","oldValue":"3 Main Rd","newValue":"3 Main Road","result":"PASS","notes":"Shows the new address"}]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model);

        Assert.Equal("PASS", result["status"]!.GetValue<string>());
        Assert.Equal(100, result["changePercentCompleted"]!.GetValue<int>());
    }

    [Fact]
    public void No_requested_changes_applied_is_fail()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"changes":[{"request":"Name must change from James to John","result":"FAIL","notes":"Still James"}]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model);

        Assert.Equal("FAIL", result["status"]!.GetValue<string>());
        Assert.Equal(0, result["changePercentCompleted"]!.GetValue<int>());
    }

    [Fact]
    public void A_comment_without_a_field_is_kept()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"changes":[{"request":"change this","field":"","oldValue":"","newValue":"","result":"PASS","notes":""}]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model);
        var change = result["changes"]![0]!;

        Assert.Equal("Unidentified Field Changes", change["field"]!.GetValue<string>());
        Assert.Equal("FAIL", change["result"]!.GetValue<string>());
        Assert.Equal("", change["oldValue"]!.GetValue<string>());
        Assert.Equal("", change["newValue"]!.GetValue<string>());
        Assert.Equal("FAIL", result["status"]!.GetValue<string>());
    }

    [Fact]
    public void The_same_field_change_is_counted_once()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"changes":[
              {"request":"Country must change from UK to United Kingdom","field":"Country","oldValue":"UK","newValue":"United Kingdom","result":"PASS","notes":"Live document shows United Kingdom."},
              {"request":"Country must change from UK to United Kingdom","field":"Country","oldValue":"UK","newValue":"United Kingdom","result":"FAIL","notes":"Live document shows United Kingdom, which is a typo."},
              {"request":"Address must change","field":"Address","oldValue":"123 Main Road, NR44 6TH","newValue":"123 Mainly Road, NR44 8TH","result":"FAIL","notes":"Old address remains"}
            ]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model);

        Assert.Equal(2, result["changes"]!.AsArray().Count);
        Assert.Equal("FAIL", result["changes"]![0]!["result"]!.GetValue<string>());
        Assert.Equal("FAIL", result["status"]!.GetValue<string>());
    }

    [Fact]
    public void A_misspelt_live_value_fails_even_when_the_model_says_pass()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"changes":[{"request":"Country must change from UK to United Kingdom","field":"Country","oldValue":"UK","newValue":"United Kingdom","result":"PASS","notes":"Live document correctly shows United Kingdom."}]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model, "Country\tUnited Kingdon\nAddress 123 Main Road");
        var change = result["changes"]![0]!;

        Assert.Equal("FAIL", change["result"]!.GetValue<string>());
        Assert.Contains("United Kingdom", change["notes"]!.GetValue<string>());
        Assert.Equal("FAIL", result["status"]!.GetValue<string>());
    }

    [Fact]
    public void An_exact_live_value_still_passes()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"changes":[{"request":"Registration must change","field":"Registration","oldValue":"AR12AO","newValue":"AR1 2AO","result":"PASS","notes":"Shows AR1 2AO"}]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model, "Vehicle registration:\n AR1   2AO");

        Assert.Equal("PASS", result["status"]!.GetValue<string>());
    }

    [Fact]
    public void Pdf_requests_include_page_count_and_comment_text()
    {
        var content = new System.Text.Json.Nodes.JsonArray();

        ChangeVision.AppendDocument(content, "Change request", "change.pdf", PdfCalloutTests.PdfWithCallout(), visionOnly: false);

        var text = string.Join("\n", content
            .Select(part => part?["text"])
            .OfType<System.Text.Json.Nodes.JsonValue>()
            .Select(value => value.GetValue<string>()));
        Assert.Contains("1 page. Check every page.", text);
        Assert.Contains("Comment on page 1", text);
        Assert.Contains("Change Limited to PLC", text);
        Assert.Contains(content, part => part?["type"]?.GetValue<string>() == "file");
    }

    [Fact]
    public void A_mismatch_note_quotes_the_live_text()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"changes":[{"request":"Page 67 should be 68","field":"Criminal Act","oldValue":"page 67","newValue":"page 68","expectedText":"See 'What we won't pay for' page 68","result":"PASS","notes":""}]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model, "criminal purpose. See \u2018What we won\u2019t pay for\u2019 page 67. Deliberate damage");
        var notes = result["changes"]![0]!["notes"]!.GetValue<string>();

        Assert.Contains("page 67", notes);
        Assert.Contains("live document shows", notes);
    }

    [Fact]
    public void A_value_found_elsewhere_does_not_pass()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"changes":[{"request":"Change £200 to £500","field":"Alternative travel costs","oldValue":"£200","newValue":"£500","expectedText":"we will pay up to £500 towards alternative travel costs","result":"PASS","notes":"Shows £500"}]}
            """)!.AsObject();

        var live = "we will pay up to £200 towards alternative travel costs if you are unable to do so. A £500 excess applies to young drivers.";
        var result = ChangeCheck.Normalize(model, live);

        Assert.Equal("FAIL", result["changes"]![0]!["result"]!.GetValue<string>());
        Assert.Equal("FAIL", result["status"]!.GetValue<string>());
    }

    [Fact]
    public void Added_words_must_be_in_the_sentence()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"changes":[{"request":"Add on page 99 to the end of the sentence","field":"When does the cover start and end?","oldValue":"specified in your Policy Schedule.","newValue":"specified in your Policy Schedule on page 99.","expectedText":"Cover lasts for one year and the dates of cover are specified in your Policy Schedule on page 99.","result":"PASS","notes":"Added"}]}
            """)!.AsObject();

        var unchanged = ChangeCheck.Normalize(model, "Cover lasts for one year and the dates of cover are specified in your Policy Schedule.\nHow do I cancel the contract?");
        var changed = ChangeCheck.Normalize(model, "Cover lasts for one year and the dates of cover are\nspecified in your Policy Schedule on page 99.");

        Assert.Equal("FAIL", unchanged["status"]!.GetValue<string>());
        Assert.Equal("PASS", changed["status"]!.GetValue<string>());
    }

    [Fact]
    public void Curly_quotes_in_the_live_pdf_still_match()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"changes":[{"request":"Page 67 should be page 68","field":"Criminal Act","oldValue":"page 67","newValue":"page 68","expectedText":"See 'What we won't pay for' page 68.","result":"PASS","notes":"Shows page 68"}]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model, "purpose. See \u2018What we won\u2019t pay for\u2019 page 68.");

        Assert.Equal("PASS", result["status"]!.GetValue<string>());
    }

    [Fact]
    public void Model_name_and_token_counts_are_returned()
    {
        const string payload = """
            {"model":"gpt-4o-2024-11-20","usage":{"prompt_tokens":1850,"completion_tokens":240,"total_tokens":2090},"choices":[{"message":{"content":"{\"changes\":[]}"}}]}
            """;

        var result = ChangeCheck.Normalize(ChangeVision.ParseModelContent(payload));

        Assert.Equal("gpt-4o-2024-11-20", result["model"]!.GetValue<string>());
        Assert.Equal(1850, result["promptTokens"]!.GetValue<int>());
        Assert.Equal(240, result["completionTokens"]!.GetValue<int>());
        Assert.Equal(2090, result["totalTokens"]!.GetValue<int>());
        Assert.Equal(0.0077m, result["estimatedCost"]!.GetValue<decimal>());
        Assert.Equal("USD", result["costCurrency"]!.GetValue<string>());
    }

    [Fact]
    public void DocRef_comes_from_the_model()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"docRef":"POL-889123","changes":[]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model, null, "anything.pdf");

        Assert.Equal("POL-889123", result["docRef"]!.GetValue<string>());
    }

    [Fact]
    public void DocRef_falls_back_to_the_file_name()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"docRef":"Not Found","changes":[]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model, null, "Policy 1234567_change.rtf");

        Assert.Equal("1234567", result["docRef"]!.GetValue<string>());
    }

    [Fact]
    public void DocRef_is_not_found_when_nothing_matches()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""{"changes":[]}""")!.AsObject();

        var result = ChangeCheck.Normalize(model, null, "change request.pdf");

        Assert.Equal("Not Found", result["docRef"]!.GetValue<string>());
    }

    [Fact]
    public void DocRef_is_limited_to_fifty_characters()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"docRef":"REF-0000000000-1111111111-2222222222-3333333333-4444444444","changes":[]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model);

        Assert.True(result["docRef"]!.GetValue<string>().Length <= 50);
    }

    [Fact]
    public void Model_confidence_is_returned_with_its_reason()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"confidence":88,"confidenceReason":"Every request was clearly marked.","changes":[{"request":"Registration must change","field":"Registration","oldValue":"AR12AO","newValue":"AR1 2AO","result":"FAIL","notes":"Still AR12AO"}]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model);

        Assert.Equal(88, result["confidence"]!.GetValue<int>());
        Assert.Equal("Every request was clearly marked.", result["confidenceReason"]!.GetValue<string>());
    }

    [Fact]
    public void Confidence_drops_when_the_model_was_corrected()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"confidence":95,"confidenceReason":"Clear request.","changes":[{"request":"Country must change","field":"Country","oldValue":"UK","newValue":"United Kingdom","result":"PASS","notes":"Shows United Kingdom"}]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model, "Country United Kingdon");

        Assert.Equal(70, result["confidence"]!.GetValue<int>());
        Assert.Contains("corrected", result["confidenceReason"]!.GetValue<string>());
    }

    [Fact]
    public void Confidence_drops_for_an_unidentified_comment()
    {
        var model = System.Text.Json.Nodes.JsonNode.Parse("""
            {"confidence":0.9,"changes":[{"request":"change this","field":"","result":"FAIL","notes":"change this"}]}
            """)!.AsObject();

        var result = ChangeCheck.Normalize(model);

        Assert.Equal(60, result["confidence"]!.GetValue<int>());
        Assert.Contains("could not be tied", result["confidenceReason"]!.GetValue<string>());
    }
}
