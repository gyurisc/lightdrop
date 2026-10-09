using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using LightDrop.Core.Configuration;
using LightDrop.Core.Contracts;
using LightDrop.Core.Transfers;
using LightDrop.Daemon.Peers;
using LightDrop.Daemon.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// The first file transfer: two real daemons, a real mutual-TLS peer listener, real files.
/// </summary>
/// <remarks>
/// <see cref="Sender"/> sends, <see cref="Receiver"/> receives. Pairing is done programmatically
/// (<see cref="TestDaemon.TrustAsync"/>): the ceremony is <see cref="PairingFlowTests"/>' subject,
/// and these tests are about what a pin permits once it exists. Every download folder is inside a
/// test's temp directory -- never the real one.
/// </remarks>
public sealed class FileTransferTests(PairingKeys keys) : IClassFixture<PairingKeys>
{
    private const string SenderId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ReceiverId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ThirdId = "cccccccccccccccccccccccccccccccc";
    private const string StrangerId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    private TrustedPeer Alice => new()
    {
        DeviceId = SenderId,
        DeviceName = "Alice",
        PublicKey = Convert.ToBase64String(keys.Alice.PublicKeyInfo),
        PairedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>Two daemons pinned to each other, the sender hearing the receiver on the network.</summary>
    private static async Task<(TestDaemon Sender, TestDaemon Receiver)> StartPairedAsync()
    {
        var sender = await TestDaemon.StartAsync(SenderId, "Desk PC");
        var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini");

        await sender.TrustAsync(receiver);
        await receiver.TrustAsync(sender);
        sender.Discover(receiver);

        return (sender, receiver);
    }

    private static string WriteFile(TestDaemon owner, string relativePath, byte[] contents)
    {
        var path = Path.Combine(owner.Directory.FullPath, "outbox", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // ---- The happy path, through /api/send exactly as `lightdrop send` drives it. ----

    [Fact]
    public async Task AFileSentToAPairedPeerLandsByteForByteInItsDownloadFolder()
    {
        var (sender, receiver) = await StartPairedAsync();
        await using (sender)
        await using (receiver)
        {
            var contents = RandomBytes(300_000, seed: 1);
            var path = WriteFile(sender, "notes.txt", contents);

            // Not there yet: the receiver creates it on demand.
            Assert.False(Directory.Exists(receiver.DownloadFolder));

            using var response = await sender.SendAsync(path, "mac mini");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var sent = (await response.Content.ReadFromJsonAsync(LightDropJsonContext.Default.SendResponse))!;
            Assert.Equal("notes.txt", sent.SavedAs);
            Assert.Equal(contents.Length, sent.Length);
            Assert.Equal(ReceiverId, sent.PeerDeviceId);
            Assert.Equal("Mac Mini", sent.PeerName);

            Assert.Equal(["notes.txt"], receiver.ReceivedFiles);
            Assert.Equal(contents, await File.ReadAllBytesAsync(Path.Combine(receiver.DownloadFolder, "notes.txt")));
        }
    }

    [Fact]
    public async Task AnEmptyFileIsAFileToo()
    {
        var (sender, receiver) = await StartPairedAsync();
        await using (sender)
        await using (receiver)
        {
            var path = WriteFile(sender, "empty.txt", []);

            using var response = await sender.SendAsync(path, "Mac Mini");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["empty.txt"], receiver.ReceivedFiles);
            Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(receiver.DownloadFolder, "empty.txt")));
        }
    }

    [Fact]
    public async Task AFileLargerThanKestrelsDefaultBodyLimitLands()
    {
        // Kestrel refuses bodies over 30 MB by default, and the peer listener caps every body at
        // 4 KB. SendFile lifts the cap for its own request only; this proves the lift reaches past
        // both. Written once to disk and hashed on both sides rather than held twice in memory.
        var (sender, receiver) = await StartPairedAsync();
        await using (sender)
        await using (receiver)
        {
            const int Length = (32 * 1024 * 1024) + 17;
            var path = WriteFile(sender, "big.bin", RandomBytes(Length, seed: 2));

            using var response = await sender.SendAsync(path, "Mac Mini");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var saved = Path.Combine(receiver.DownloadFolder, "big.bin");
            Assert.Equal(Length, new FileInfo(saved).Length);
            Assert.Equal(await HashAsync(path), await HashAsync(saved));
        }
    }

    [Fact]
    public async Task ASecondFileOfTheSameNameIsNumberedAndTheFirstIsUntouched()
    {
        var (sender, receiver) = await StartPairedAsync();
        await using (sender)
        await using (receiver)
        {
            var first = WriteFile(sender, "one/report.pdf", [1, 1, 1]);
            var second = WriteFile(sender, "two/report.pdf", [2, 2]);
            var third = WriteFile(sender, "three/report.pdf", [3]);

            (await sender.SendAsync(first, "Mac Mini")).Dispose();
            using var secondResponse = await sender.SendAsync(second, "Mac Mini");
            using var thirdResponse = await sender.SendAsync(third, "Mac Mini");

            Assert.Equal("report (1).pdf", (await secondResponse.Content.ReadFromJsonAsync(LightDropJsonContext.Default.SendResponse))!.SavedAs);
            Assert.Equal("report (2).pdf", (await thirdResponse.Content.ReadFromJsonAsync(LightDropJsonContext.Default.SendResponse))!.SavedAs);
            Assert.Equal(["report (1).pdf", "report (2).pdf", "report.pdf"], receiver.ReceivedFiles);
            Assert.Equal([1, 1, 1], await File.ReadAllBytesAsync(Path.Combine(receiver.DownloadFolder, "report.pdf")));
            Assert.Equal([2, 2], await File.ReadAllBytesAsync(Path.Combine(receiver.DownloadFolder, "report (1).pdf")));
        }
    }

    [Fact]
    public async Task ConcurrentFilesOfTheSameNameNeverOverwriteEachOther()
    {
        // The race the numbering has to survive: several transfers finishing at once, each
        // picking "the first free name". Every one must land under its own name, intact.
        var (sender, receiver) = await StartPairedAsync();
        await using (sender)
        await using (receiver)
        {
            const int Count = 6;
            var paths = Enumerable.Range(0, Count)
                .Select(i => WriteFile(sender, $"{i}/same.bin", RandomBytes(200_000, seed: 100 + i)))
                .ToArray();

            var responses = await Task.WhenAll(paths.Select(path => sender.SendAsync(path, "Mac Mini")));

            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            foreach (var response in responses)
            {
                response.Dispose();
            }

            Assert.Equal(Count, receiver.ReceivedFiles.Count);
            var received = receiver.ReceivedFiles
                .Select(name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(receiver.DownloadFolder, name)))))
                .Order(StringComparer.Ordinal);
            var originals = paths
                .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
                .Order(StringComparer.Ordinal);
            Assert.Equal(originals, received);
        }
    }

    [Fact]
    public async Task ReportsExactlyTheCommandsItsContainerCanDispatch()
    {
        // Exactly, not "contains": a capability with no handler behind it advertises a command
        // that would 404. That the list is projected from the handlers, rather than happening to
        // match them, is CommandServicesTests' job.
        await using var daemon = await TestDaemon.StartAsync(SenderId, "Desk PC");

        var health = await daemon.Api.GetFromJsonAsync("health", LightDropJsonContext.Default.HealthResponse);

        var handlers = daemon.App.Services.GetServices<Commands.ICommandHandler>()
            .Select(handler => handler.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(handlers, health!.Capabilities);
        Assert.Contains("SendFile", health.Capabilities);
    }

    // ---- What the sender refuses before any byte leaves. ----

    [Fact]
    public async Task SendingToAPairedPeerThatIsNotDiscoveredSaysSo()
    {
        var (sender, receiver) = await StartPairedAsync();
        await using (sender)
        await using (receiver)
        {
            receiver.Transport.Goodbye(SenderId);
            sender.Transport.Goodbye(ReceiverId);
            var path = WriteFile(sender, "notes.txt", [1, 2, 3]);

            using var response = await sender.SendAsync(path, "Mac Mini");

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync(LightDropJsonContext.Default.ApiError))!;
            Assert.Contains("Mac Mini", error.Error, StringComparison.Ordinal);
            Assert.Empty(receiver.ReceivedFiles);
        }
    }

    [Fact]
    public async Task AWebPageCannotSendAFile()
    {
        // /api/send reads any file this user can read. A page open in a browser posts to loopback
        // with its own Origin, and must be turned away before the request is looked at.
        var (sender, receiver) = await StartPairedAsync();
        await using (sender)
        await using (receiver)
        {
            var path = WriteFile(sender, "secret.txt", [1, 2, 3]);
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/send")
            {
                Content = JsonContent.Create(
                    new SendRequest { Path = path, Peer = "Mac Mini" }, LightDropJsonContext.Default.SendRequest),
            };
            request.Headers.Add("Origin", "http://evil.example");

            using var response = await sender.Api.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Empty(receiver.ReceivedFiles);
        }
    }

    [Fact]
    public async Task SendingToADiscoveredButUnpairedPeerIsRefused()
    {
        // Discovery is presence, not trust: hearing a device does not make it a destination.
        await using var sender = await TestDaemon.StartAsync(SenderId, "Desk PC");
        await using var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini");
        await receiver.TrustAsync(sender);
        sender.Discover(receiver);
        var path = WriteFile(sender, "notes.txt", [1, 2, 3]);

        using var response = await sender.SendAsync(path, "Mac Mini");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(receiver.ReceivedFiles);
    }

    [Theory]
    [InlineData("relative/notes.txt")]
    [InlineData("")]
    public async Task SendingRequiresAnAbsolutePath(string path)
    {
        var (sender, receiver) = await StartPairedAsync();
        await using (sender)
        await using (receiver)
        {
            using var response = await sender.SendAsync(path, "Mac Mini");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(receiver.ReceivedFiles);
        }
    }

    [Fact]
    public async Task SendingAFileThatDoesNotExistIsRefused()
    {
        var (sender, receiver) = await StartPairedAsync();
        await using (sender)
        await using (receiver)
        {
            using var response = await sender.SendAsync(Path.Combine(sender.Directory.FullPath, "missing.txt"), "Mac Mini");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(receiver.ReceivedFiles);
        }
    }

    [Fact]
    public async Task AFileGoesOnlyToThePinnedKeyNotToWhoeverAnswersAtTheAddress()
    {
        // Discovery is attacker-controlled: anyone can announce the receiver's id at their own
        // address. Here a third device that does trust the sender -- so it would happily accept
        // the file -- answers there. The sender must refuse it at the handshake, because its key
        // is not the one pinned for the receiver.
        var (sender, receiver) = await StartPairedAsync();
        await using (sender)
        await using (receiver)
        await using (var impostor = await TestDaemon.StartAsync(ThirdId, "Impostor"))
        {
            await impostor.TrustAsync(sender);
            sender.Transport.Announce(ReceiverId, "Mac Mini", port: impostor.Endpoint.PeerPort, address: "127.0.0.1");
            var path = WriteFile(sender, "secret.txt", [4, 2]);

            using var response = await sender.SendAsync(path, "Mac Mini");

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Empty(impostor.ReceivedFiles);
            Assert.Empty(receiver.ReceivedFiles);
        }
    }

    // ---- What the sender makes of a paired receiver's answers. ----

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\u001b]52;c;aGk=\u0007\u001b[2J: v\r\n\r\n")]
    [InlineData("HTTP/1.1 2\u001b]52;c;aGk=\u0007 OK\r\nContent-Length: 0\r\n\r\n")]
    public async Task AMalformedAnswerCannotWriteToTheSendersTerminal(string raw)
    {
        // The HTTP stack quotes the bytes it chokes on in its exception message, escapes and all,
        // and that message is part of the error `lightdrop send` prints.
        await using var receiver = ScriptedReceiver.Start(keys.Bob, exchange => exchange.WriteAsync(raw));
        await using var sender = await StartPinnedToBobAsync(receiver);
        var path = WriteFile(sender, "notes.txt", [1, 2, 3]);

        using var response = await sender.SendAsync(path, "Mac Mini");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync(LightDropJsonContext.Default.ApiError))!;
        Assert.DoesNotContain(error.Error, char.IsControl);
    }

    [Fact]
    public async Task TheNameAReceiverSaysItSavedAsCannotWriteToTheSendersTerminal()
    {
        await using var receiver = ScriptedReceiver.Start(
            keys.Bob, exchange => exchange.WriteAsync(ScriptedReceiver.Json(200, """{"savedAs":"\u001b]52;c;aGk=\u0007\u001b[2Jnotes.txt"}""")));
        await using var sender = await StartPinnedToBobAsync(receiver);
        var path = WriteFile(sender, "notes.txt", [1, 2, 3]);

        using var response = await sender.SendAsync(path, "Mac Mini");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = (await response.Content.ReadFromJsonAsync(LightDropJsonContext.Default.SendResponse))!;
        Assert.Equal("]52;c;aGk=[2Jnotes.txt", sent.SavedAs);
    }

    [Fact]
    public async Task AReceiversRefusalCannotWriteToTheSendersTerminal()
    {
        await using var receiver = ScriptedReceiver.Start(
            keys.Bob, exchange => exchange.WriteAsync(ScriptedReceiver.Json(400, """{"error":"no\u001b]52;c;aGk=\u0007\r\nthanks"}""")));
        await using var sender = await StartPinnedToBobAsync(receiver);
        var path = WriteFile(sender, "notes.txt", [1, 2, 3]);

        using var response = await sender.SendAsync(path, "Mac Mini");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync(LightDropJsonContext.Default.ApiError))!;

        // The refusal did arrive -- this is its text, cleaned -- rather than being dropped.
        Assert.Contains("]52;c;aGk=", error.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(error.Error, char.IsControl);
    }

    [Fact]
    public async Task AFileThatGrowsWhileBeingSentArrivesAsTheLengthItHadWhenTheSendBegan()
    {
        // What was declared is what is sent. Otherwise the receiver saves the declared length,
        // complete as far as it can tell, while the sender's stack fails on the extra bytes and
        // `lightdrop send` reports a failure for a file that did arrive. Windows refuses the
        // append outright (the sender holds the file open without write sharing); on macOS and
        // Linux nothing stops it, which is the case that matters.
        var original = RandomBytes(4096, seed: 4);
        var headRead = new TaskCompletionSource();
        var grown = new TaskCompletionSource();
        var received = new TaskCompletionSource<byte[]>();

        await using var receiver = ScriptedReceiver.Start(keys.Bob, async exchange =>
        {
            // The sender asked for 100-continue, so no body byte has been read from the file yet.
            headRead.SetResult();
            await grown.Task;
            await exchange.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n");
            received.SetResult(await exchange.ReadBodyAsync());
            await exchange.WriteAsync(ScriptedReceiver.Json(200, """{"savedAs":"grow.log"}"""));
        });
        await using var sender = await StartPinnedToBobAsync(receiver);
        var path = WriteFile(sender, "grow.log", original);

        var send = sender.SendAsync(path, "Mac Mini");
        await headRead.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await File.AppendAllTextAsync(path, "one more line\n");
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // The sharing mode said no: the file cannot grow, so there is nothing to show.
        }

        grown.SetResult();
        using var response = await send;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(original, await received.Task);
    }

    [Fact]
    public async Task APeerThatNeverAnswersTheHandshakeFailsTheSendInsteadOfHangingIt()
    {
        // A receiver gone to sleep, or behind a firewall dropping the port, is still listed for
        // minutes after it went quiet. The transfer has no timeout -- it may take an hour -- so
        // the dial must have its own. This listener's backlog accepts the TCP connection and
        // nothing ever answers the TLS handshake.
        var silent = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            await using var sender = await TestDaemon.StartAsync(SenderId, "Desk PC", new TrustedPeer
            {
                DeviceId = ReceiverId,
                DeviceName = "Mac Mini",
                PublicKey = Convert.ToBase64String(keys.Bob.PublicKeyInfo),
                PairedAt = DateTimeOffset.UnixEpoch,
            });
            sender.Transport.Announce(ReceiverId, "Mac Mini", port: ((IPEndPoint)silent.LocalEndpoint).Port, address: "127.0.0.1");
            var path = WriteFile(sender, "notes.txt", [1, 2, 3]);

            // Inside TestDaemon.Api's own 30-second timeout, which is what fails this test if the
            // daemon waits for ever.
            using var response = await sender.SendAsync(path, "Mac Mini");

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
        finally
        {
            silent.Stop();
        }
    }

    /// <summary>
    /// A sender that pinned <see cref="PairingKeys.Bob"/> as "Mac Mini" and hears it at
    /// <paramref name="receiver"/>.
    /// </summary>
    private async Task<TestDaemon> StartPinnedToBobAsync(ScriptedReceiver receiver)
    {
        var sender = await TestDaemon.StartAsync(SenderId, "Desk PC", new TrustedPeer
        {
            DeviceId = ReceiverId,
            DeviceName = "Mac Mini",
            PublicKey = Convert.ToBase64String(keys.Bob.PublicKeyInfo),
            PairedAt = DateTimeOffset.UnixEpoch,
        });

        sender.Transport.Announce(ReceiverId, "Mac Mini", port: receiver.Port, address: "127.0.0.1");
        return sender;
    }

    // ---- What the receiver enforces, called directly as a (possibly hostile) paired peer. ----

    [Theory]
    [InlineData("../../escape.txt", "escape.txt")]
    [InlineData("..\\..\\escape.txt", "escape.txt")]
    [InlineData("/etc/cron.d/evil", "evil")]
    [InlineData("C:\\Windows\\evil.exe", "evil.exe")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("a<b>:c?.txt", "a_b__c_.txt")]
    [InlineData("invoice\u202Etxt.exe", "invoicetxt.exe")]
    [InlineData("\u001b]52;c;aGk=\u0007clip.txt", "]52;c;aGk=clip.txt")]
    [InlineData("trailing. . .", "trailing")]
    public async Task AHostileFileNameLandsSanitizedInsideTheDownloadFolder(string claimed, string expected)
    {
        await using var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini", Alice);

        using var response = await CommandClient.SendFileAsync(keys.Alice, receiver.PeerAddress, claimed, [7, 7, 7]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync(LightDropJsonContext.Default.SendFileResult))!;
        Assert.Equal(expected, result.SavedAs);
        Assert.Equal([expected], receiver.ReceivedFiles);
        Assert.Equal([7, 7, 7], await File.ReadAllBytesAsync(Path.Combine(receiver.DownloadFolder, expected)));

        // Nothing anywhere else: the data directory holds exactly what the daemon itself wrote.
        var outside = Directory.EnumerateFiles(receiver.Directory.FullPath, "*", SearchOption.AllDirectories)
            .Where(file => !file.StartsWith(receiver.DownloadFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal);
        Assert.Equal(["config.json", "state.json"], outside);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("folder/")]
    [InlineData("\u202E")]
    [InlineData("")]
    public async Task ANameThatIsEmptyOnceMadeSafeIsRefused(string claimed)
    {
        await using var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini", Alice);

        using var response = await CommandClient.SendFileAsync(keys.Alice, receiver.PeerAddress, claimed, [1]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(receiver.ReceivedFiles);
    }

    [Fact]
    public async Task AnAbortedUploadLeavesNoFileBehind()
    {
        await using var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini", Alice);
        var release = new TaskCompletionSource();
        using var content = new StallingContent(RandomBytes(64 * 1024, seed: 3), declaredLength: 1024 * 1024, release.Task);

        var upload = CommandClient.SendAsync(
            keys.Alice, receiver.PeerAddress, "SendFile", CommandClient.Metadata("partial.bin", 1024 * 1024), content);

        // Wait until the receiver is part way through -- something exists in the folder -- so the
        // test proves a cleanup rather than a transfer that never started.
        await WaitUntilAsync(() => receiver.ReceivedFiles.Count > 0, "the receiver never started writing");

        release.SetResult();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => upload);

        await WaitUntilAsync(() => receiver.ReceivedFiles.Count == 0, "the partial file was left behind");
        Assert.Empty(receiver.ReceivedFiles);
    }

    [Fact]
    public async Task ABodyWithoutAContentLengthIsRefused()
    {
        // A chunked body has no length to check up front, and the declared length is what the
        // body limit is lifted to. Even one whose bytes happen to match is refused.
        await using var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini", Alice);
        using var content = new UnknownLengthContent([1, 2, 3, 4]);

        using var response = await CommandClient.SendAsync(
            keys.Alice, receiver.PeerAddress, "SendFile", CommandClient.Metadata("chunked.bin", 4), content);

        Assert.Equal(HttpStatusCode.LengthRequired, response.StatusCode);
        Assert.Empty(receiver.ReceivedFiles);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(10)]
    [InlineData(-1)]
    public async Task AContentLengthThatDisagreesWithTheDeclaredLengthIsRefused(long declared)
    {
        await using var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini", Alice);
        using var content = new ByteArrayContent(new byte[50]);

        using var response = await CommandClient.SendAsync(
            keys.Alice, receiver.PeerAddress, "SendFile", CommandClient.Metadata("liar.bin", declared), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(receiver.ReceivedFiles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not base64url!")]
    [InlineData("bm90IGpzb24")] // "not json"
    [InlineData("e30")] // "{}"
    public async Task MissingOrMalformedMetadataIsRefused(string? header)
    {
        await using var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini", Alice);
        using var content = new ByteArrayContent([1, 2, 3]);

        using var response = await CommandClient.SendAsync(keys.Alice, receiver.PeerAddress, "SendFile", header, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(receiver.ReceivedFiles);
    }

    [Fact]
    public async Task AnUnknownCommandIsNotFound()
    {
        await using var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini", Alice);

        using var response = await CommandClient.SendAsync(
            keys.Alice, receiver.PeerAddress, "FormatDisk", CommandClient.Metadata("x", 0), new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheBodyLimitIsLiftedForTheFileOnlyNotForTheRestOfTheListener()
    {
        await using var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini", Alice);

        // Well past the listener's 4 KB cap: lifted for this request.
        using (var file = await CommandClient.SendFileAsync(keys.Alice, receiver.PeerAddress, "f.bin", new byte[64 * 1024]))
        {
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        }

        // And still in force for everything else, after a large transfer as before one.
        using var client = new HttpClient(Pairing.PairingTls.CreateCapturingHandler(keys.Alice, new Pairing.PeerKeyCapture()))
        {
            BaseAddress = receiver.PeerAddress,
        };
        var padding = new string('x', (int)PeerListener.MaxRequestBodyBytes * 4);
        using var hello = await client.PostAsync(
            "pair/hello",
            new StringContent($"{{\"deviceId\":\"{padding}\",\"protocolVersion\":1}}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, hello.StatusCode);
    }

    // ---- The two gates in front of /commands. ----

    [Fact]
    public async Task DuringAnOpenPairingWindowAnUnpinnedCallerGetsThroughTlsButNotToSendFile()
    {
        // The window is why the application-layer check exists: the handshake admits any
        // certificate while it is open, so a stranger reaches HTTP. It must get no further.
        await using var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini", Alice);
        receiver.Transport.Announce(StrangerId, "Stranger", address: "127.0.0.1");
        using (var started = await receiver.StartPairingAsync("Stranger"))
        {
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        }

        using (var stranger = await CommandClient.SendFileAsync(keys.Impostor, receiver.PeerAddress, "drop.exe", [6, 6, 6]))
        {
            // An HTTP status at all is the proof it got through the handshake.
            Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        }

        Assert.Empty(receiver.ReceivedFiles);

        // The control: same listener, same window, the pinned peer gets through.
        using var pinned = await CommandClient.SendFileAsync(keys.Alice, receiver.PeerAddress, "fine.txt", [1]);
        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
    }

    [Fact]
    public async Task WithNoWindowOpenAnUnpinnedCallerIsRefusedAtTheHandshake()
    {
        await using var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini", Alice);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CommandClient.SendFileAsync(keys.Impostor, receiver.PeerAddress, "drop.exe", [6, 6, 6]));

        Assert.Empty(receiver.ReceivedFiles);
    }

    [Fact]
    public async Task AnUnpairedPeerCanNoLongerSend()
    {
        await using var receiver = await TestDaemon.StartAsync(ReceiverId, "Mac Mini", Alice);
        (await receiver.Api.DeleteAsync($"api/trusted/{SenderId}")).Dispose();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CommandClient.SendFileAsync(keys.Alice, receiver.PeerAddress, "late.txt", [1]));

        Assert.Empty(receiver.ReceivedFiles);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(failure);
            }

            await Task.Delay(25);
        }
    }

    /// <summary>Sends part of a body, then fails mid-stream when released: a sender that dies.</summary>
    private sealed class StallingContent(byte[] first, long declaredLength, Task release) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(first);
            await stream.FlushAsync();
            await release;
            throw new IOException("The sender gave up part way through.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }
    }

    /// <summary>A body with no Content-Length, so it goes chunked.</summary>
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
