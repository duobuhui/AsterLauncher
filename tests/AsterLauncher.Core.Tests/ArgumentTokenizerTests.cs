using AsterLauncher.Core;

namespace AsterLauncher.Core.Tests;

public sealed class ArgumentTokenizerTests
{
    [Fact]
    public void Parse_PreservesQuotedChinesePathAndSpaces()
    {
        var arguments = ArgumentTokenizer.Parse("run daily --config \"E:\\游戏 配置\\maa.json\"");

        Assert.Equal(["run", "daily", "--config", "E:\\游戏 配置\\maa.json"], arguments);
    }

    [Fact]
    public void JoinAndParse_RoundTripsDifficultArguments()
    {
        string[] source = ["run", "", "C:\\Games\\A B\\", "say\"hello", "中文 参数"];

        var rendered = ArgumentTokenizer.Join(source);
        var parsed = ArgumentTokenizer.Parse(rendered);

        Assert.Equal(source, parsed);
    }

    [Fact]
    public void Parse_RejectsUnclosedQuotes()
    {
        Assert.Throws<FormatException>(() => ArgumentTokenizer.Parse("run \"daily"));
    }
}

