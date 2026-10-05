using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json;
using Jellyfin.Plugin.SubtitlesTool.Core;

namespace Jellyfin.Plugin.SubtitlesTool.Tests;

public sealed class AnchorTests
{
    private static SpeechText Speech(long position = 150000) => new("着替えて帰ろうかなと思っています", "着替えて帰ろうかなと思っています".Select((text, index) => new SpeechWord(index, text.ToString(), position + index * 100, position + index * 100 + 100)).ToArray());
    private static SubtitleCue[] Cues(long start = 1000) => [new(7, start, start + 2000, "换衣服回去吧"), new(8, start + 3000, start + 4000, "今天辛苦了")];
    private const string Match = """{"match":true,"spoken":true,"atCueBeginning":true,"unambiguous":true,"speechPhraseStartId":0,"speechPhraseEndId":0,"cueStartId":7,"cueEndId":7,"speechQuote":"着替えて帰ろうかなと思っています","speechPrefix":"着替えて帰ろうかな","subtitleQuotes":[{"id":7,"text":"换衣服回去吧"}],"subtitlePrefix":"换衣服回去吧"}""";
    private static string Reply(string match) => new JsonObject { ["matches"] = new JsonArray(JsonNode.Parse(match)) }.ToJsonString();

    [Theory]
    [InlineData(150000, 1000, 149000)]
    [InlineData(1000, 150000, -149000)]
    [InlineData(150000, 150000, 0)]
    public void TimestampDifferenceIsRelativeToCurrentSubtitle(long speech, long cue, long expected)
    {
        var anchor = Assert.IsType<AlignmentAnchor>(AnchorMatcher.Validate(Match, Speech(speech), Cues(cue)));
        Assert.Equal(expected, anchor.VideoMilliseconds - anchor.SubtitleMilliseconds);
    }

    [Fact]
    public void ARemoteSimilarSceneCannotSuggestAnHourOfOffset()
    {
        Assert.Null(AnchorMatcher.Validate(Match, Speech(1835000), Cues(7085779)));
    }

    [Theory]
    [InlineData("speechQuote", "着替える予定です")]
    [InlineData("speechPhraseStartId", 99)]
    [InlineData("atCueBeginning", false)]
    [InlineData("spoken", false)]
    [InlineData("unambiguous", false)]
    [InlineData("subtitlePrefix", "回去吧")]
    [InlineData("speechPrefix", "実際には言っていない")]
    public void WrongReferencesNoiseAndMergedCueMiddleAreRejected(string field, object value)
    {
        var match = JsonNode.Parse(Match)!.AsObject(); match[field] = JsonValue.Create(value);
        Assert.Null(AnchorMatcher.Validate(match.ToJsonString(), Speech(), Cues()));
    }

    [Fact]
    public void UnquotedTitlePromotionAndNoMatchAreRejected()
    {
        Assert.Null(AnchorMatcher.Validate(Match, Speech(), [new(7, 0, 2000, "影片标题")]));
        var json = Match.Replace("换衣服回去吧", "欢迎订阅字幕组");
        Assert.Null(AnchorMatcher.Validate(json, Speech(), [new(7, 0, 2000, "欢迎订阅字幕组")]));
        Assert.Null(AnchorMatcher.Validate("{\"match\":false}", Speech(), Cues()));
    }

    [Fact]
    public void SplitSubtitlesRetainTheFirstCuesStartAndExactQuotes()
    {
        var value = JsonNode.Parse(Match)!.AsObject();
        value["cueEndId"] = 8;
        value["subtitleQuotes"] = JsonNode.Parse("""[{"id":7,"text":"换衣服"},{"id":8,"text":"回去吧"}]""");
        value["subtitlePrefix"] = "换衣服";
        Assert.NotNull(AnchorMatcher.Validate(value.ToJsonString(), Speech(), [new(7, 1000, 2000, "换衣服"), new(8, 2200, 3000, "回去吧")]));
        value["subtitleQuotes"]![1]!["text"] = "错误原文";
        Assert.Null(AnchorMatcher.Validate(value.ToJsonString(), Speech(), [new(7, 1000, 2000, "换衣服"), new(8, 2200, 3000, "回去吧")]));
    }

    [Fact]
    public void DisplayWhitespaceCanBeQuotedWithoutLosingTheOriginalSubtitleText()
    {
        var value = JsonNode.Parse(Match)!.AsObject();
        value["subtitleQuotes"]![0]!["text"] = "换衣服 回去吧";
        value["subtitlePrefix"] = "换衣服";
        var anchor = Assert.IsType<AlignmentAnchor>(AnchorMatcher.Validate(value.ToJsonString(), Speech(), [new(7, 1000, 3000, "换衣服 \r\n回去吧")]));
        Assert.Equal("换衣服 \r\n回去吧", anchor.SubtitleQuote);
        value["subtitleQuotes"]![0]!["text"] = "换好衣服 回去吧";
        Assert.Null(AnchorMatcher.Validate(value.ToJsonString(), Speech(), [new(7, 1000, 3000, "换衣服 \r\n回去吧")]));
    }

    [Fact]
    public void AWholeSentenceSplitIntoFiveRecognitionPhrasesRetainsItsActualStart()
    {
        var text = new[] { "先", "生", "私", "触りたく", "なりました" };
        var speech = new SpeechText(string.Concat(text), text.Select((part, index) => new SpeechWord(index, part, 150000 + index * 900, 150100 + index * 900)).ToArray());
        var value = JsonNode.Parse(Match)!.AsObject();
        value["speechPhraseEndId"] = 4; value["speechQuote"] = speech.Text; value["speechPrefix"] = "先生私";
        value["subtitleQuotes"]![0]!["text"] = "老师 我想摸啦"; value["subtitlePrefix"] = "老师 我想";
        var anchor = Assert.IsType<AlignmentAnchor>(AnchorMatcher.Validate(value.ToJsonString(), speech, [new(7, 1000, 4000, "老师 我想摸啦")]));
        Assert.Equal(150000, anchor.VideoMilliseconds);
    }

    [Fact]
    public void NativeSpeechTimestampsRestoreClipOriginAndExcludeNonLanguageEvents()
    {
        const string json = """{"event":"<|Speech|>","tokens":["本","当","に"],"timestamps":[0.6,0.8,1.2]}""";
        var speech = SpeechRecognizer.Parse(json, 30000, 3000);
        Assert.Equal(30600, speech.Words[0].StartMilliseconds);
        Assert.Empty(SpeechRecognizer.Parse(json.Replace("Speech", "Laughter"), 0, 3000).Words);
        Assert.Throws<ToolException>(() => SpeechRecognizer.Parse(json, 0, 1000));
    }

    [Fact]
    public void OmittedShortCallBeforeTheMainClauseIsNotACompleteAnchor()
    {
        var speech = Speech();
        var lead = new SpeechWord[] { new(0, "ね", 148000, 148100), new(1, "え", 148100, 148200) };
        speech = speech with { Words = [.. lead, .. speech.Words.Select(word => word with { Id = word.Id + 2 })] };
        var cues = new SubtitleCue[] { new(7, 1000, 3000, "喂 换衣服回去吧") };
        var value = JsonNode.Parse(Match.Replace("换衣服回去吧", "喂 换衣服回去吧"))!.AsObject(); value["speechPhraseStartId"] = 1; value["speechPhraseEndId"] = 1;
        Assert.Null(AnchorMatcher.Validate(value.ToJsonString(), speech, cues));
        value["speechPhraseStartId"] = 0; value["speechQuote"] = "ねえ着替えて帰ろうかなと思っています";
        value["speechPrefix"] = "ねえ着替えて帰ろうかな";
        Assert.NotNull(AnchorMatcher.Validate(value.ToJsonString(), speech, cues));
    }

    [Fact]
    public void AnUntranslatedQuestionBeforeTheAnswerDoesNotMoveTheAnchorStart()
    {
        const string text = "おい骨ですか今五十ですねおお五十歳はい。";
        var speech = new SpeechText(text, text.Select((letter, index) => new SpeechWord(index, letter.ToString(), 7325000 + index * 100, 7325000 + index * 100 + 100)).ToArray());
        const string response = """{"match":true,"spoken":true,"atCueBeginning":true,"unambiguous":true,"speechPhraseStartId":1,"speechPhraseEndId":1,"cueStartId":1306,"cueEndId":1306,"speechQuote":"今五十ですねおお五十歳はい。","speechPrefix":"今五十ですねおお","subtitleQuotes":[{"id":1306,"text":"现在50岁了呢 50岁"}],"subtitlePrefix":"现在50岁了呢"}""";
        var anchor = Assert.IsType<AlignmentAnchor>(AnchorMatcher.Validate(response, speech, [new(1306, 7325600, 7328000, "现在50岁了呢 50岁")]));
        Assert.Equal(7325600, anchor.VideoMilliseconds);
        Assert.Equal("今五十ですねおお五十歳はい。", anchor.SpeechQuote);
    }

    [Fact]
    public void AGenericHappyCommentCannotAnchorAnUnrelatedExcitementQuestion()
    {
        var text = "めちゃ嬉しそう";
        var speech = new SpeechText(text, text.Select((letter, index) => new SpeechWord(index, letter.ToString(), index * 100, index * 100 + 100)).ToArray());
        const string response = """{"match":true,"spoken":true,"atCueBeginning":true,"unambiguous":true,"speechPhraseStartId":0,"speechPhraseEndId":0,"cueStartId":1,"cueEndId":1,"speechQuote":"めちゃ嬉しそう","speechPrefix":"めちゃ嬉しそう","subtitleQuotes":[{"id":1,"text":"你兴奋了吗      兴奋了"}],"subtitlePrefix":"你兴奋了吗"}""";
        Assert.Null(AnchorMatcher.Validate(response, speech, [new(1, 0, 1000, "你兴奋了吗      兴奋了")]));
    }

    [Fact]
    public void AnUntranslatedFillerCannotBecomeTheSubtitleStart()
    {
        var baseSpeech = Speech();
        SpeechWord[] words = [new(0, "あ", 149500, 149650), new(1, "の", 149650, 149800), .. baseSpeech.Words.Select(word => word with { Id = word.Id + 2 })];
        var speech = new SpeechText("あの" + baseSpeech.Text, words);
        var value = JsonNode.Parse(Match)!.AsObject(); value["speechQuote"] = "あの" + baseSpeech.Text; value["speechPrefix"] = "あの着替えて帰ろうかな";
        Assert.Null(AnchorMatcher.Validate(value.ToJsonString(), speech, Cues()));
        value["speechPrefix"] = "着替えて帰ろうかな";
        Assert.Equal(150000, Assert.IsType<AlignmentAnchor>(AnchorMatcher.Validate(value.ToJsonString(), speech, Cues())).VideoMilliseconds);
    }

    [Theory]
    [InlineData("てと着替えて帰ろっかな", "好啦 换衣服 回家啦")]
    [InlineData("あんた今から始めるんでしょう", "喂 你现在开始吧")]
    public void AClippedOrOmittedCallCannotConfirmTheBeginningOfTheSubtitle(string text, string subtitle)
    {
        var speech = new SpeechText(text, text.Select((letter, index) => new SpeechWord(index, letter.ToString(), 150000 + index * 100, 150100 + index * 100)).ToArray());
        var value = JsonNode.Parse(Match)!.AsObject(); value["speechQuote"] = text; value["speechPrefix"] = text;
        value["subtitleQuotes"]![0]!["text"] = subtitle; value["subtitlePrefix"] = subtitle;
        MatchFailure? failure = null;
        Assert.Null(AnchorMatcher.Validate(value.ToJsonString(), speech, [new(7, 1000, 3000, subtitle)], problem => failure = problem));
        Assert.Equal("omitted_call", failure?.Code);
    }

    [Fact]
    public void CustomParametersPreserveNestedVendorFieldsWithoutChangingProtectedFields()
    {
        var config = new Configuration { LlmModel = "deepseek-flash", LlmParameters = """{"thinking":{"type":"disabled"},"reasoning_effort":"low","temperature":0.2,"vendor":{"nested":[1,{"value":true}]},"max_completion_tokens":512}""" };
        var body = AnchorMatcher.BuildRequest(config, "{}");
        Assert.Equal("disabled", body["thinking"]!["type"]!.GetValue<string>());
        Assert.True(body["vendor"]!["nested"]![1]!["value"]!.GetValue<bool>());
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.Equal(1, body["n"]!.GetValue<int>());
        Assert.Equal(0.2, body["temperature"]!.GetValue<double>());
        Assert.Equal(0, AnchorMatcher.BuildRequest(new Configuration(), "{}")["temperature"]!.GetValue<int>());
        Assert.False(AnchorMatcher.BuildRequest(new Configuration(), "{}").ContainsKey("thinking"));
        config.LlmParameters = "{\"messages\":[]}";
        Assert.Throws<ToolException>(() => AnchorMatcher.BuildRequest(config, "{}"));
        config.LlmParameters = "{\"max_tokens\":4097}";
        Assert.Throws<ToolException>(() => AnchorMatcher.BuildRequest(config, "{}"));
    }

    [Fact]
    public async Task CustomHeadersAndDisabledThinkingAreSentAgainOnNetworkRetry()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https://opencode.ai/zen/go/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal("session-test", Assert.Single(request.Headers.GetValues("x-opencode-session")));
            Assert.Equal("jellyfin-jav-subtitles/test", Assert.Single(request.Headers.GetValues("User-Agent")));
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-only", request.Headers.Authorization.Parameter);
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            Assert.Equal("deepseek-v4.1-flash", body["model"]!.GetValue<string>());
            Assert.Equal("disabled", body["thinking"]!["type"]!.GetValue<string>());
            Assert.Null(body["headers"]);
            return ++calls == 1 ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK)
                { Content = new StringContent("""{"choices":[{"message":{"content":"{\"matches\":[]}"}}]}""") };
        }));
        var config = new Configuration { LlmApiBaseUrl = "https://opencode.ai/zen/go/v1", LlmApiKey = "test-only", LlmModel = "deepseek-v4.1-flash",
            LlmParameters = """{"thinking":{"type":"disabled"}}""", LlmHeaders = """{"x-opencode-session":"session-test","User-Agent":"jellyfin-jav-subtitles/test"}""" };
        Assert.Null(AnchorMatcher.ConfigurationError(config));
        var matcher = new AnchorMatcher(http, config);
        Assert.Empty(await matcher.MatchAsync(Speech(), Cues(), Cues(), default));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"x-opencode-session\":1}")]
    [InlineData("{\"bad header\":\"value\"}")]
    [InlineData("{\"Authorization\":\"replacement\"}")]
    [InlineData("{\"x-opencode-session\":\"first\\r\\nsecond\"}")]
    public void InvalidCustomHeadersAreReportedBeforeAnalysis(string headers)
    {
        var error = AnchorMatcher.ConfigurationError(new Configuration { LlmApiBaseUrl = "https://api.example.test", LlmApiKey = "test-only", LlmModel = "test", LlmHeaders = headers });
        Assert.NotNull(error);
        Assert.Contains("请求头", error);
    }

    [Fact]
    public async Task NetworkRetryAndPhraseReviewShareThePerClipBudget()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            if (calls == 1) return new(HttpStatusCode.ServiceUnavailable);
            var response = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = Reply(Match) } }), ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5 } };
            return new(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        }));
        var matcher = new AnchorMatcher(http, new Configuration { LlmApiBaseUrl = "https://api.example.test/v1", LlmApiKey = "test-only", LlmModel = "test" });
        SubtitleCue[] all = [.. Cues(), new(20, 4000, 5000, "换衣服回去吧")];
        Assert.NotEmpty(await matcher.MatchAsync(Speech(), Cues(), all, default));
        Assert.Equal(new AlignmentUsage(3, 20, 10), matcher.Usage);
        Assert.True(matcher.CanRequest);
    }

    [Fact]
    public async Task CancelStopsAnOutstandingLlmRequest()
    {
        using var http = new HttpClient(new WaitingHandler());
        var matcher = new AnchorMatcher(http, new Configuration { LlmApiBaseUrl = "https://api.example.test", LlmApiKey = "test-only", LlmModel = "test" });
        using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => matcher.MatchAsync(Speech(), Cues(), Cues(), cancel.Token));
        Assert.Equal(1, matcher.Usage.Requests);
    }

    [Fact]
    public async Task WrongQuotesReceiveSpecificFeedbackFollowedByStartVerification()
    {
        var calls = new List<JsonObject>();
        using var http = new HttpClient(new Handler(request =>
        {
            calls.Add(JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject());
            var content = Reply(calls.Count == 1 ? Match.Replace("着替えて帰ろうかなと思っています", "引用错误") : Match);
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }) }.ToJsonString()) };
        }));
        var matcher = new AnchorMatcher(http, new Configuration { LlmApiBaseUrl = "https://api.example.test", LlmApiKey = "test-only", LlmModel = "test" });
        SubtitleCue[] all = [.. Cues(), new(20, 5000, 6000, "换衣服回去吧")];
        Assert.NotEmpty(await matcher.MatchAsync(Speech(), Cues(), all, default));
        Assert.Equal(3, matcher.Usage.Requests);
        Assert.Equal(2, calls[0]["messages"]!.AsArray().Count);
        Assert.Equal("assistant", calls[1]["messages"]![2]!["role"]!.GetValue<string>());
        Assert.Contains("speech_quote", calls[1]["messages"]![3]!["content"]!.GetValue<string>());
        Assert.Contains("台词引用", calls[1]["messages"]![3]!["content"]!.GetValue<string>());
        Assert.DoesNotContain("\\u", calls[1]["messages"]![3]!["content"]!.GetValue<string>());
        Assert.Contains("anchor_start_review", calls[2]["messages"]![3]!["content"]!.GetValue<string>());
        Assert.True(matcher.CanRequest);
    }

    [Fact]
    public async Task AValidNoMatchDoesNotTriggerARepairRequest()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            count++;
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"{\"matches\":[]}"}}]}""") };
        }));
        var matcher = new AnchorMatcher(http, new Configuration { LlmApiBaseUrl = "https://api.example.test", LlmApiKey = "test-only", LlmModel = "test" });
        Assert.Empty(await matcher.MatchAsync(Speech(), Cues(), Cues(), default));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AReviewWithMissingCueIdsReceivesTheirNamesAndCanRepairWithinTheBudget()
    {
        var calls = new List<JsonObject>();
        using var http = new HttpClient(new Handler(request =>
        {
            calls.Add(JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject());
            var content = JsonNode.Parse(Match)!.AsObject();
            if (calls.Count == 2) { content.Remove("cueStartId"); content.Remove("cueEndId"); }
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = Reply(content.ToJsonString()) } }) }.ToJsonString()) };
        }));
        var matcher = new AnchorMatcher(http, new Configuration { LlmApiBaseUrl = "https://api.example.test", LlmApiKey = "test-only", LlmModel = "test" });
        Assert.NotEmpty(await matcher.MatchAsync(Speech(), Cues(), [.. Cues(), new(20, 5000, 6000, "换衣服回去吧")], default));
        Assert.Equal(3, calls.Count);
        var feedback = calls[2]["messages"]![3]!["content"]!.GetValue<string>();
        Assert.Contains("missing_fields", feedback);
        Assert.Contains("cueStartId、cueEndId", feedback);
        Assert.Contains("完整 JSON", feedback);
    }

    [Fact]
    public async Task InvalidJsonRepairsStopAtTheClipAndTaskBudgets()
    {
        var count = 0;
        var feedback = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var body = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!;
            if (body["messages"]!.AsArray().Count > 2) feedback.Add(body["messages"]![3]!["content"]!.GetValue<string>());
            count++; var content = "not JSON";
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }) }.ToJsonString()) };
        }));
        var matcher = new AnchorMatcher(http, new Configuration { LlmApiBaseUrl = "https://api.example.test", LlmApiKey = "test-only", LlmModel = "test" });
        for (var clip = 0; clip < 8; clip++)
        {
            Assert.Empty(await matcher.MatchAsync(Speech(), Cues(), Cues(), default));
            Assert.Equal((clip + 1) * 3, count);
        }
        Assert.False(matcher.CanRequest);
        Assert.Empty(await matcher.MatchAsync(Speech(), Cues(), Cues(), default));
        Assert.Equal(24, count);
        Assert.Equal(16, feedback.Count);
        Assert.All(feedback, value => Assert.Contains("invalid_json", value));
    }

    private static AlignmentPoint Point(int sample, int cue, long offset) => new(sample, new(0, 10, cue, cue, "实际台词", "对应字幕", cue * 100000L + offset, cue * 100000L));

    [Fact]
    public async Task TwoDifferentUtterancesInOneClipCanConfirmTheSameOffset()
    {
        var first = Speech(); const string second = "今日はありがとうございました。";
        var speech = new SpeechText(first.Text + second, [.. first.Words, .. second.Select((letter, index) => new SpeechWord(first.Words.Count + index, letter.ToString(), 154000 + index * 100, 154100 + index * 100))]);
        SubtitleCue[] cues = [new(7, 1000, 3000, "换衣服回去吧"), new(8, 5000, 7000, "今天辛苦了")];
        var count = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            var value = JsonNode.Parse(Match)!.AsObject();
            if (++count == 2)
            {
                value["speechPhraseStartId"] = 1; value["speechPhraseEndId"] = 1; value["cueStartId"] = 8; value["cueEndId"] = 8;
                value["speechQuote"] = second; value["speechPrefix"] = "今日はありがとうございました";
                value["subtitleQuotes"] = JsonNode.Parse("""[{"id":8,"text":"今天辛苦了"}]"""); value["subtitlePrefix"] = "今天辛苦了";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = Reply(value.ToJsonString()) } }) }.ToJsonString()) };
        }));
        var matcher = new AnchorMatcher(http, new Configuration { LlmApiBaseUrl = "https://api.example.test", LlmApiKey = "test-only", LlmModel = "test" });
        var anchors = await matcher.MatchAsync(speech, cues, cues, default);
        Assert.Equal(2, count);
        Assert.Equal(2, SubtitleAligner.Consensus(anchors.Select(anchor => new AlignmentPoint(0, anchor)).ToArray()).Length);
        Assert.All(anchors, anchor => Assert.Equal(149000, anchor.VideoMilliseconds - anchor.SubtitleMilliseconds));
    }

    [Fact]
    public void IndependentDialoguePointsConfirmTheMedianAndIgnoreAnIsolatedWrongScene()
    {
        var points = SubtitleAligner.Consensus([Point(0, 1, 148678), Point(1, 2, -300000), Point(2, 3, 148996)]);
        Assert.Equal(2, points.Length);
        Assert.Equal(148837, SubtitleAligner.Median(points.Select(point => point.VideoMilliseconds - point.SubtitleMilliseconds)));
    }

    [Fact]
    public void ConflictingProposalsDoNotRestrictTheRemainingSubtitleCandidates()
    {
        Assert.Null(SubtitleAligner.CandidateOffset([Point(0, 1, 150466).Anchor, Point(1, 2, 619458).Anchor]));
        Assert.Equal(148837, SubtitleAligner.CandidateOffset([Point(0, 1, 148678).Anchor, Point(1, 2, 148996).Anchor]));
    }

    [Fact]
    public void ARepeatedPointOrTwoConflictingOffsetGroupsCannotConfirmAlignment()
    {
        Assert.Empty(SubtitleAligner.Consensus([Point(0, 1, 100), Point(0, 2, 200)]));
        Assert.Empty(SubtitleAligner.Consensus([Point(0, 1, 100), Point(1, 1, 200)]));
        Assert.Empty(SubtitleAligner.Consensus([Point(0, 1, 100), Point(1, 2, 200), Point(2, 3, 10000), Point(3, 4, 10200)]));
        Assert.Empty(SubtitleAligner.Consensus([Point(0, 1, 100), Point(1, 2, 1000)]));
    }

    [Fact]
    public void SamplingStaysBoundedAndPositionRetryStartsNearby()
    {
        Assert.Equal(new[] { 50d, 500d, 440d, 800d, 860d, 200d, 110d, 260d }, SubtitleAligner.Positions(1000, null));
        Assert.Equal(new[] { 97d, 130d, 190d, 280d, 400d, 520d, 700d }, SubtitleAligner.Positions(1000, 100000));
        Assert.Single(SubtitleAligner.Positions(20, null));
        Assert.Equal(new[] { 56d }, SubtitleAligner.Positions(61, 59000));
    }

    [Theory]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", 7)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", 37)]
    [InlineData("mpegts", 7)]
    public void NonZeroMediaStartUsesTheStreamsActualDuration(string container, double formatDuration)
    {
        using var root = JsonDocument.Parse(JsonSerializer.Serialize(new { format = new { format_name = container, start_time = "30", duration = formatDuration }, streams = new[] { new { codec_type = "video", start_time = "30", duration = "7" } } }));
        Assert.Equal(7, SubtitleAligner.PlaybackDuration(root.RootElement));
    }

    [Fact]
    public async Task XlmRobertaTokenIdsMatchThePinnedModelsTokenizer()
    {
        var cache = Path.Combine(Path.GetTempPath(), "subtitle-tokenizer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var assets = await SpeechAssets.PrepareAsync(cache, default);
            SpeechDetector.LoadRuntime(Path.Combine(Path.GetTempPath(), "jav-subtitles-test-runtime"));
            using var candidates = new SubtitleCandidates(assets, Cues());
            Assert.Equal(new long[] { 0, 41, 1294, 12, 6, 192661, 2 }, candidates.TokenIds("こんにちは"));
            Assert.Equal(new long[] { 0, 41, 1294, 12, 61168, 19543, 46503, 80003, 4502, 2 }, candidates.TokenIds("今天换衣服回去吧"));
            Assert.Equal(new long[] { 0, 41, 1294, 12, 35378, 8999, 2 }, candidates.TokenIds("Hello world"));
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { await Task.Delay(10000, token); return new(HttpStatusCode.OK); }
    }
}
