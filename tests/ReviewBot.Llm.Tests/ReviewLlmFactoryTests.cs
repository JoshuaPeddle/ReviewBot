using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReviewBot.Core.Domain;
using ReviewBot.Core.Llm;
using ReviewBot.Llm.Anthropic;
using ReviewBot.Llm.OpenAi;

namespace ReviewBot.Llm.Tests;

public sealed class ReviewLlmFactoryTests
{
    [Fact]
    public void CreateReturnsAnthropicProviderForAnthropicConfig()
    {
        using var provider = BuildProviderWithRealLlms();

        var llm = provider.GetRequiredService<IReviewLlmFactory>()
            .Create(new ModelConfig("anthropic", "claude-from-config", BaseUrlEnvVar: null));

        llm.Should().BeOfType<AnthropicReviewLlm>();
    }

    [Fact]
    public void CreateReturnsOpenAiProviderForOpenAiConfig()
    {
        using var provider = BuildProviderWithRealLlms();

        var llm = provider.GetRequiredService<IReviewLlmFactory>()
            .Create(new ModelConfig("openai", "gpt-from-config", BaseUrlEnvVar: null));

        llm.Should().BeOfType<OpenAiReviewLlm>();
    }

    [Fact]
    public void CreateThrowsForUnknownProviderWithSupportedProviders()
    {
        using var provider = BuildProviderWithRealLlms();

        var act = () => provider.GetRequiredService<IReviewLlmFactory>()
            .Create(new ModelConfig("local", "model", BaseUrlEnvVar: null));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unsupported LLM provider 'local'*anthropic*openai*");
    }

    [Fact]
    public void CreateHonorsPerCallModelNameOverride()
    {
        var anthropic = Substitute.For<IConfigurableReviewLlm>();
        var overridden = Substitute.For<IReviewLlm>();
        anthropic.ProviderName.Returns("anthropic");
        anthropic.WithModelName("claude-from-config").Returns(overridden);

        var services = new ServiceCollection();
        services.AddSingleton(new ReviewLlmProviderRegistration("anthropic", _ => anthropic));
        services.AddReviewLlmFactory();
        using var provider = services.BuildServiceProvider();

        var llm = provider.GetRequiredService<IReviewLlmFactory>()
            .Create(new ModelConfig("anthropic", "claude-from-config", BaseUrlEnvVar: null));

        llm.Should().BeSameAs(overridden);
        anthropic.Received(1).WithModelName("claude-from-config");
    }

    [Fact]
    public void CreateOnlyResolvesSelectedProvider()
    {
        var selected = Substitute.For<IConfigurableReviewLlm>();
        var overridden = Substitute.For<IReviewLlm>();
        selected.ProviderName.Returns("anthropic");
        selected.WithModelName("claude-from-config").Returns(overridden);
        var unusedWasResolved = false;

        var services = new ServiceCollection();
        services.AddSingleton(new ReviewLlmProviderRegistration("anthropic", _ => selected));
        services.AddSingleton(new ReviewLlmProviderRegistration("openai", _ =>
        {
            unusedWasResolved = true;
            throw new InvalidOperationException("Unused provider should not be resolved.");
        }));
        services.AddReviewLlmFactory();
        using var provider = services.BuildServiceProvider();

        var llm = provider.GetRequiredService<IReviewLlmFactory>()
            .Create(new ModelConfig("anthropic", "claude-from-config", BaseUrlEnvVar: null));

        llm.Should().BeSameAs(overridden);
        unusedWasResolved.Should().BeFalse();
    }

    [Fact]
    public void AddReviewLlmFactoryRegistersConcreteAndInterface()
    {
        var services = new ServiceCollection();

        services.AddReviewLlmFactory();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IReviewLlmFactory>().Should().BeOfType<ReviewLlmFactory>();
        provider.GetRequiredService<ReviewLlmFactory>().Should().BeSameAs(
            provider.GetRequiredService<IReviewLlmFactory>());
    }

    private static ServiceProvider BuildProviderWithRealLlms()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<AnthropicReviewLlm>>(NullLogger<AnthropicReviewLlm>.Instance);
        services.AddSingleton<ILogger<OpenAiReviewLlm>>(NullLogger<OpenAiReviewLlm>.Instance);
        services.AddAnthropicReviewLlm(options =>
        {
            options.ApiKey = "test-anthropic-key";
            options.ModelName = "claude-default";
        });
        services.AddOpenAiReviewLlm(options =>
        {
            options.ApiKey = "test-openai-key";
            options.ModelName = "gpt-default";
        });
        services.AddReviewLlmFactory();

        return services.BuildServiceProvider();
    }
}
