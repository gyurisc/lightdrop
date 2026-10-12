using System.Xml;
using System.Xml.Linq;
using LightDrop.Daemon.Autostart;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// The plist <c>lightdrop autostart on</c> writes. Loading it is verified by hand: a test that ran
/// <c>launchctl</c> would register a real login item on the machine running the suite.
/// </summary>
public sealed class LaunchAgentTests
{
    private const string Executable = "/Users/someone/lightdrop/lightdrop";
    private const string Log = "/Users/someone/Library/Logs/lightdrop.log";

    private static XElement Dict(string plist)
    {
        using var reader = XmlReader.Create(
            new StringReader(plist), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var root = XDocument.Load(reader).Root!;
        Assert.Equal("plist", root.Name.LocalName);
        return root.Element("dict")!;
    }

    /// <summary>The element following <c>&lt;key&gt;name&lt;/key&gt;</c> in a plist dict.</summary>
    private static XElement Value(XElement dict, string name) =>
        dict.Elements("key").Single(key => key.Value == name).ElementsAfterSelf().First();

    [Fact]
    public void RunsTheDaemonAtLogin()
    {
        var dict = Dict(LaunchAgent.Render(Executable, Log));

        Assert.Equal(LaunchAgent.Label, Value(dict, "Label").Value);
        Assert.Equal(
            [Executable, "daemon"],
            Value(dict, "ProgramArguments").Elements("string").Select(element => element.Value));
        Assert.Equal("true", Value(dict, "RunAtLoad").Name.LocalName);
    }

    [Fact]
    public void RestartsOnlyAfterAFailedExit()
    {
        // Restarting after a clean exit as well would fight every deliberate stop.
        var keepAlive = Value(Dict(LaunchAgent.Render(Executable, Log)), "KeepAlive");

        Assert.Equal("dict", keepAlive.Name.LocalName);
        Assert.Equal("false", Value(keepAlive, "SuccessfulExit").Name.LocalName);
    }

    [Fact]
    public void SendsOutputToTheLog()
    {
        var dict = Dict(LaunchAgent.Render(Executable, Log));

        Assert.Equal(Log, Value(dict, "StandardOutPath").Value);
        Assert.Equal(Log, Value(dict, "StandardErrorPath").Value);
    }

    [Theory]
    [InlineData(Executable)]
    [InlineData("/Users/a&b/<odd> \"name\"/light drop")]
    public void ReadsBackTheExecutableItWrote(string executable)
    {
        Assert.Equal(executable, LaunchAgent.ReadExecutablePath(LaunchAgent.Render(executable, Log)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<plist version=\"1.0\"><dict/></plist>")]
    [InlineData("<plist version=\"1.0\"><dict><key>ProgramArguments</key><string>x</string></dict></plist>")]
    public void ReadsNoExecutableFromAPlistItDidNotWrite(string plist)
    {
        Assert.Null(LaunchAgent.ReadExecutablePath(plist));
    }

    [Fact]
    public void LivesInTheUsersLaunchAgentsFolder()
    {
        var agent = new LaunchAgent("/Users/someone");

        Assert.Equal(
            Path.Combine("/Users/someone", "Library", "LaunchAgents", "com.lightdrop.daemon.plist"),
            agent.PlistPath);
        Assert.Equal(Path.Combine("/Users/someone", "Library", "Logs", "lightdrop.log"), agent.LogPath);
    }
}
