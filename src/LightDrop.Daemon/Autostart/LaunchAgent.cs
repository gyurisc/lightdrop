using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;

namespace LightDrop.Daemon.Autostart;

/// <summary>
/// Starts <c>lightdrop daemon</c> at login on macOS, as a per-user LaunchAgent.
/// </summary>
/// <remarks>
/// A LaunchAgent rather than a LaunchDaemon: it runs as the logged-in user, whose Application
/// Support, Downloads and Local Network permission LightDrop uses, and installing it needs no admin
/// rights. Rendering and reading the plist are pure and tested; the <c>launchctl</c> calls are
/// verified by hand, like the mDNS transport, because a test would register a real login item.
/// </remarks>
public sealed class LaunchAgent(string homeDirectory)
{
    /// <summary>
    /// The job label, and the plist's file name. Kept from the hand-written plist the README-era
    /// instructions used, so <c>autostart on</c> replaces that one rather than adding a second.
    /// </summary>
    public const string Label = "com.lightdrop.daemon";

    public string PlistPath => Path.Combine(homeDirectory, "Library", "LaunchAgents", $"{Label}.plist");

    /// <summary>Where launchd sends the daemon's output; it has no terminal to write to.</summary>
    public string LogPath => Path.Combine(homeDirectory, "Library", "Logs", "lightdrop.log");

    /// <summary>The plist that runs <paramref name="executablePath"/> <c>daemon</c> at login.</summary>
    /// <remarks>
    /// <c>KeepAlive</c> restarts the daemon only when it exits unsuccessfully — a crash, or a port
    /// another daemon holds — never after a clean stop. launchd throttles the retries to one every
    /// ten seconds, so a daemon already running in a terminal costs a log line per attempt, and the
    /// background one takes over when that one stops.
    /// </remarks>
    public static string Render(string executablePath, string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);

        var plist = new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement(
                "plist",
                new XAttribute("version", "1.0"),
                new XElement(
                    "dict",
                    Key("Label"), new XElement("string", Label),
                    Key("ProgramArguments"),
                    new XElement("array", new XElement("string", executablePath), new XElement("string", "daemon")),
                    Key("RunAtLoad"), new XElement("true"),
                    Key("KeepAlive"), new XElement("dict", Key("SuccessfulExit"), new XElement("false")),
                    Key("StandardOutPath"), new XElement("string", logPath),
                    Key("StandardErrorPath"), new XElement("string", logPath))));

        // XDocument.ToString omits the declaration, which launchd does not need but plutil and
        // every hand-written plist carry.
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n{plist}\n";

        static XElement Key(string name) => new("key", name);
    }

    /// <summary>
    /// The executable a plist runs, or <c>null</c> if it is not one <see cref="Render"/> could
    /// have written.
    /// </summary>
    public static string? ReadExecutablePath(string plist)
    {
        try
        {
            // The DOCTYPE names Apple's DTD by URL; ignore it rather than fetch or parse it.
            using var reader = XmlReader.Create(
                new StringReader(plist), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });

            var arguments = XDocument.Load(reader).Root?.Element("dict")?
                .Elements("key")
                .FirstOrDefault(key => key.Value == "ProgramArguments")?
                .ElementsAfterSelf()
                .FirstOrDefault();

            return arguments?.Name == "array" ? arguments.Elements("string").FirstOrDefault()?.Value : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Writes the plist and loads it, which also starts the daemon now.</summary>
    /// <exception cref="InvalidOperationException">launchctl refused the job.</exception>
    [SupportedOSPlatform("macos")]
    public void Enable(string executablePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

        // Unloaded first so running `autostart on` again — after moving the binary, say — swaps
        // the job rather than failing with "already loaded".
        Unload();

        File.WriteAllText(PlistPath, Render(executablePath, LogPath));

        var (exitCode, output) = Launchctl("bootstrap", Domain, PlistPath);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"launchctl could not load {PlistPath} (exit {exitCode}): {output.Trim()}");
        }
    }

    /// <summary>Stops the daemon if launchd runs it and removes the plist.</summary>
    /// <returns><c>false</c> if autostart was already off.</returns>
    [SupportedOSPlatform("macos")]
    public bool Disable()
    {
        var wasLoaded = Unload();
        var existed = File.Exists(PlistPath);

        if (existed)
        {
            File.Delete(PlistPath);
        }

        return wasLoaded || existed;
    }

    [SupportedOSPlatform("macos")]
    public LaunchAgentStatus GetStatus()
    {
        var installed = File.Exists(PlistPath);
        var executablePath = installed ? ReadExecutablePath(File.ReadAllText(PlistPath)) : null;

        // `launchctl print` is meant for people, not parsing. It is only used for this hint, never
        // for a decision, so a format change degrades the status line rather than breaking anything.
        var (exitCode, output) = Launchctl("print", Service);
        var running = exitCode == 0 && output.Contains("state = running", StringComparison.Ordinal);
        var processId = running ? ReadProcessId(output) : null;

        return new LaunchAgentStatus(installed, executablePath, running, processId);
    }

    private static int? ReadProcessId(string launchctlPrint)
    {
        foreach (var line in launchctlPrint.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("pid = ", StringComparison.Ordinal)
                && int.TryParse(trimmed.AsSpan("pid = ".Length), out var pid))
            {
                return pid;
            }
        }

        return null;
    }

    /// <summary>Boots the job out, waiting until launchd has let go of it.</summary>
    /// <returns>Whether it was loaded.</returns>
    [SupportedOSPlatform("macos")]
    private static bool Unload()
    {
        if (!IsLoaded())
        {
            return false;
        }

        Launchctl("bootout", Service);

        // bootout returns before the job is fully gone; bootstrapping straight after it fails with
        // an I/O error. A few seconds covers launchd's SIGTERM-then-wait on a daemon shutting down.
        for (var attempt = 0; attempt < 100 && IsLoaded(); attempt++)
        {
            Thread.Sleep(100);
        }

        return true;
    }

    [SupportedOSPlatform("macos")]
    private static bool IsLoaded() => Launchctl("print", Service).ExitCode == 0;

    /// <summary>The logged-in user's GUI session: where a LaunchAgent belongs.</summary>
    private static string Domain => $"gui/{GetUserId()}";

    private static string Service => $"{Domain}/{Label}";

    private static (int ExitCode, string Output) Launchctl(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("/bin/launchctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not run /bin/launchctl.");

        // Both streams drained concurrently, or a full stderr pipe could block launchctl forever.
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, standardOutput.GetAwaiter().GetResult() + standardError);
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUserId();
}

/// <param name="Installed">The plist exists.</param>
/// <param name="ExecutablePath">What the plist runs, if it could be read.</param>
/// <param name="Running">launchd reports the job running now.</param>
/// <param name="ProcessId">Its process id, when launchd says.</param>
public sealed record LaunchAgentStatus(bool Installed, string? ExecutablePath, bool Running, int? ProcessId);
