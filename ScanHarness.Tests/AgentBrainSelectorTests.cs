using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScanHarness.Services;
using ScanHarness.Services.Llm;
using Xunit;

namespace ScanHarness.Tests
{
    public class AgentBrainSelectorTests
    {
        private const string ModelsJson =
            "{\"object\":\"list\",\"data\":[" +
            "{\"id\":\"qwen2.5:3b\",\"object\":\"model\"}," +
            "{\"id\":\"nomic-embed-text:latest\",\"object\":\"model\"}," +
            "{\"id\":\"llama3.2:3b\",\"object\":\"model\"}]}";

        private static AgentBrainSelector SelectorFor(FakeHttpMessageHandler handler, LlmOptions? options = null)
        {
            options ??= new LlmOptions { ExcludeModelPatterns = { "embed" } };
            return new AgentBrainSelector(
                new KeywordAgentBrain(),
                new OpenAiCompatibleClient(handler.CreateClient()),
                Options.Create(options),
                NullLogger<AgentBrainSelector>.Instance);
        }

        [Fact]
        public async Task Lists_keyword_brain_first_then_models_without_excluded_ones()
        {
            var selector = SelectorFor(FakeHttpMessageHandler.Returning(ModelsJson));

            await selector.InitializeAsync();

            Assert.Equal(new[] { AgentBrainSelector.KeywordBrainId, "llama3.2:3b", "qwen2.5:3b" }, selector.Options.Select(o => o.Id));
            Assert.Null(selector.LlmStatus);
            Assert.True(selector.HasLlmBrains);
        }

        [Fact]
        public async Task Starts_with_keyword_brain_when_no_default_model_is_configured()
        {
            var selector = SelectorFor(FakeHttpMessageHandler.Returning(ModelsJson));

            await selector.InitializeAsync();

            Assert.Equal(AgentBrainSelector.KeywordBrainId, selector.CurrentId);
            Assert.IsType<KeywordAgentBrain>(selector.Current);
        }

        [Fact]
        public async Task Selects_the_configured_default_model_when_it_is_served()
        {
            var selector = SelectorFor(FakeHttpMessageHandler.Returning(ModelsJson), new LlmOptions { DefaultModel = "qwen2.5:3b" });

            await selector.InitializeAsync();

            Assert.Equal("qwen2.5:3b", selector.CurrentId);
            var brain = Assert.IsType<OpenAiCompatibleAgentBrain>(selector.Current);
            Assert.Equal("qwen2.5:3b", brain.Model);
        }

        [Fact]
        public async Task Missing_default_model_falls_back_to_keyword_brain()
        {
            var selector = SelectorFor(FakeHttpMessageHandler.Returning(ModelsJson), new LlmOptions { DefaultModel = "not-installed:7b" });

            await selector.InitializeAsync();

            Assert.Equal(AgentBrainSelector.KeywordBrainId, selector.CurrentId);
        }

        [Fact]
        public async Task Unreachable_endpoint_offers_only_the_keyword_brain_and_explains_why()
        {
            var selector = SelectorFor(FakeHttpMessageHandler.Throwing(new HttpRequestException("Connection refused")));

            await selector.InitializeAsync();

            Assert.Single(selector.Options);
            Assert.False(selector.HasLlmBrains);
            Assert.Contains("unreachable", selector.LlmStatus);
        }

        [Fact]
        public async Task Disabled_llm_does_not_call_the_endpoint_or_warn()
        {
            var handler = FakeHttpMessageHandler.Returning(ModelsJson);
            var selector = SelectorFor(handler, new LlmOptions { Enabled = false });

            await selector.InitializeAsync();

            Assert.Empty(handler.Requests);
            Assert.Single(selector.Options);
            Assert.Null(selector.LlmStatus);
        }

        [Fact]
        public async Task Initialize_only_queries_the_endpoint_once()
        {
            var handler = FakeHttpMessageHandler.Returning(ModelsJson);
            var selector = SelectorFor(handler);

            await selector.InitializeAsync();
            await selector.InitializeAsync();

            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task Select_switches_brains_and_ignores_unknown_ids()
        {
            var selector = SelectorFor(FakeHttpMessageHandler.Returning(ModelsJson));
            await selector.InitializeAsync();

            Assert.True(selector.Select("llama3.2:3b"));
            Assert.Equal("llama3.2:3b (LLM)", selector.Current.Name);

            Assert.False(selector.Select("unknown-model"));
            Assert.Equal("llama3.2:3b", selector.CurrentId);

            Assert.True(selector.Select(AgentBrainSelector.KeywordBrainId));
            Assert.IsType<KeywordAgentBrain>(selector.Current);
        }
    }
}
