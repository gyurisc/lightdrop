# Companion UI (phase A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `lightdrop ui` opens a browser page, served by the daemon it starts, showing daemon status and the peers discovered on the local network.

**Architecture:** The page is a single `index.html` embedded in the daemon assembly and served by one endpoint. It polls `/health` and `/api/peers`, which already exist — phase A adds no new API. `lightdrop ui` is `lightdrop daemon` plus a browser tab, with one branch for the port already being in use. An origin check on non-GET requests ships now, before any endpoint needs it.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs via `FrameworkReference`, xUnit. No new dependencies — this plan adds none, and adding one is out of scope.

**Spec:** `docs/superpowers/specs/2026-09-03-companion-ui-design.md`

## Global Constraints

- `dotnet build LightDrop.sln` must stay at **0 warnings**. Warnings are errors in this solution.
- All three projects set `IsAotCompatible`. Trim and AOT violations are build errors. Do not add reflection over types.
- `LightDrop.Core` stays platform independent: no ASP.NET Core, no filesystem, no OS APIs. **Nothing in this plan touches Core.**
- `LightDrop.Daemon` is a class library. Never give it `OutputType=Exe`.
- Kestrel stays bound to loopback (`127.0.0.1:5533`). **No task here may change that.**
- Every daemon test passes an explicit data directory (`TempDataDirectory`) and a `NoOpPeerDiscoveryTransport`, or it will touch the real user profile and open real multicast sockets.
- Use `[LoggerMessage]` source-generated logging if any logging is added. Do not use `ILogger.LogInformation(...)` directly.
- Follow the existing comment style: explain *why*, not *what*. The codebase documents rejected alternatives and consequences.

---

### Task 1: Reject non-GET requests from foreign origins

Loopback binding stops the LAN but not the browser. Without this, any page the user has open could POST to the daemon once phase B adds a pairing endpoint. It ships before anything needs it so no later endpoint has to remember.

**Files:**
- Create: `src/LightDrop.Daemon/Security/LoopbackOriginPolicy.cs`
- Modify: `src/LightDrop.Daemon/LightDropDaemon.cs` (in `Create`, between `builder.Build()` and `app.MapHealthEndpoints()`)
- Test: `tests/LightDrop.Daemon.Tests/LoopbackOriginPolicyTests.cs`

**Interfaces:**
- Consumes: `DaemonEndpointOptions` from `LightDrop.Core.Configuration` — use its `ClientAddress` property (a `Uri`, e.g. `http://127.0.0.1:5533/`).
- Produces: `LoopbackOriginPolicy.IsAllowed(string method, string? origin, string? host, DaemonEndpointOptions endpoint) -> bool` and the extension `WebApplication.UseLoopbackOriginCheck(DaemonEndpointOptions endpoint) -> WebApplication`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LightDrop.Daemon.Tests/LoopbackOriginPolicyTests.cs`:

```csharp
using LightDrop.Core.Configuration;
using LightDrop.Daemon.Security;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// Which requests the daemon will act on.
/// </summary>
/// <remarks>
/// Binding to loopback keeps the LAN out; it does nothing about the browser already running on
/// this machine. Any page the user has open can send a request to 127.0.0.1, so a state-changing
/// endpoint needs to know where the request came from.
/// </remarks>
public sealed class LoopbackOriginPolicyTests
{
    private static readonly DaemonEndpointOptions Endpoint = new() { Host = "127.0.0.1", Port = 5533 };

    [Fact]
    public void AllowsReadsFromAnywhere()
    {
        // Reads expose nothing a local page could not already learn, and blocking them would
        // break the page itself.
        Assert.True(LoopbackOriginPolicy.IsAllowed("GET", "https://evil.example", "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void AllowsAWriteFromThePageItself()
    {
        Assert.True(LoopbackOriginPolicy.IsAllowed("POST", "http://127.0.0.1:5533", "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void RejectsAWriteFromAnotherSite()
    {
        // The attack this exists for: a page the user happens to have open posting to the daemon.
        Assert.False(LoopbackOriginPolicy.IsAllowed("POST", "https://evil.example", "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void RejectsAWriteFromTheSameHostOnAnotherPort()
    {
        // Another local server is a different origin, and on a shared machine a different user.
        Assert.False(LoopbackOriginPolicy.IsAllowed("POST", "http://127.0.0.1:9999", "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void RejectsAnOpaqueOrigin()
    {
        // Sandboxed iframes and file:// pages send the literal string "null".
        Assert.False(LoopbackOriginPolicy.IsAllowed("POST", "null", "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void AllowsAWriteWithNoOriginFromLoopback()
    {
        // This is the CLI. It sends no Origin header, and `lightdrop pair` will POST.
        Assert.True(LoopbackOriginPolicy.IsAllowed("POST", null, "127.0.0.1:5533", Endpoint));
    }

    [Fact]
    public void RejectsAWriteWithNoOriginFromAnotherHost()
    {
        // DNS rebinding: a name that resolves to 127.0.0.1 arrives with its own Host header.
        Assert.False(LoopbackOriginPolicy.IsAllowed("POST", null, "attacker.example", Endpoint));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LightDrop.Daemon.Tests --filter "FullyQualifiedName~LoopbackOriginPolicyTests"`

Expected: a compile error, `The type or namespace name 'Security' does not exist`. That is not a real failure — create the file below with method bodies of `throw new NotImplementedException();`, re-run, and confirm all 7 tests fail with `NotImplementedException` before implementing.

- [ ] **Step 3: Write the implementation**

Create `src/LightDrop.Daemon/Security/LoopbackOriginPolicy.cs`:

```csharp
using LightDrop.Core.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace LightDrop.Daemon.Security;

/// <summary>
/// Rejects state-changing requests that did not come from this machine's own LightDrop page.
/// </summary>
/// <remarks>
/// <strong>Loopback binding is not access control against a browser.</strong> It keeps every other
/// device on the network out, but any page the user happens to have open can send a request to
/// 127.0.0.1, and the browser will attach their cookies and run it. Once pairing gains a POST
/// endpoint, that would be enough for a hostile page to pair this machine with an attacker already
/// on the LAN.
/// <para>
/// Shipped before any endpoint needs it, deliberately: a check added alongside the first write
/// endpoint is a check the second one has to remember.
/// </para>
/// </remarks>
internal static class LoopbackOriginPolicy
{
    /// <summary>
    /// Whether the daemon should act on this request.
    /// </summary>
    /// <remarks>
    /// Reads are always allowed: they expose nothing a local page could not learn anyway, and the
    /// page itself is one. Writes must prove origin — by the <c>Origin</c> header when the browser
    /// sent one, and otherwise by <c>Host</c>, which is the CLI's case since it sends no
    /// <c>Origin</c> at all.
    /// </remarks>
    public static bool IsAllowed(string method, string? origin, string? host, DaemonEndpointOptions endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            return true;
        }

        var expected = endpoint.ClientAddress.Authority;

        if (!string.IsNullOrEmpty(origin))
        {
            // Parsing rather than string comparison so the literal "null" that sandboxed iframes
            // and file:// pages send fails here rather than matching something by accident.
            return Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
                && string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parsed.Authority, expected, StringComparison.OrdinalIgnoreCase);
        }

        // Checking Host rather than the connection's remote address closes DNS rebinding: a name
        // the attacker controls can resolve to 127.0.0.1, and such a request arrives on loopback
        // looking local while carrying the attacker's host name.
        return string.Equals(host, expected, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class LoopbackOriginMiddleware
{
    /// <summary>
    /// Applies <see cref="LoopbackOriginPolicy"/> to every request.
    /// </summary>
    /// <remarks>
    /// Registered ahead of routing so it also covers requests no route serves. A rejected request
    /// gets 403 and no body — there is nothing useful to tell a caller that should not be here.
    /// </remarks>
    public static WebApplication UseLoopbackOriginCheck(this WebApplication app, DaemonEndpointOptions endpoint)
    {
        app.Use(async (context, next) =>
        {
            if (!LoopbackOriginPolicy.IsAllowed(
                    context.Request.Method,
                    context.Request.Headers.Origin.ToString(),
                    context.Request.Host.Value,
                    endpoint))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        return app;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/LightDrop.Daemon.Tests --filter "FullyQualifiedName~LoopbackOriginPolicyTests"`

Expected: PASS, 7 tests.

- [ ] **Step 5: Wire the middleware into the daemon**

In `src/LightDrop.Daemon/LightDropDaemon.cs`, inside `Create`, add the `using LightDrop.Daemon.Security;` import and insert one line so the block reads:

```csharp
        var app = builder.Build();
        app.UseLoopbackOriginCheck(endpoint);
        app.MapHealthEndpoints();
        app.MapPeerEndpoints();
        return app;
```

- [ ] **Step 6: Write the failing integration test**

Append to `tests/LightDrop.Daemon.Tests/LoopbackOriginPolicyTests.cs` — a real request against a real Kestrel binding, because the unit tests above prove the rule and not that it is installed:

```csharp
public sealed class LoopbackOriginEndpointTests
{
    [Fact]
    public async Task RejectsACrossOriginWriteOverRealHttp()
    {
        // Asserted against a path no route serves: the check runs ahead of routing, and phase A
        // has no write endpoint of its own to aim at.
        using var directory = new TempDataDirectory();
        var endpoint = new DaemonEndpointOptions { Host = "127.0.0.1", Port = FreeTcpPort.Get() };
        using var cancellation = new CancellationTokenSource();

        var app = LightDropDaemon.Create(endpoint, directory.FullPath, new NoOpPeerDiscoveryTransport());
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(cancellation.Token);

            using var client = new HttpClient { BaseAddress = endpoint.ClientAddress };

            using var hostile = new HttpRequestMessage(HttpMethod.Post, "anything");
            hostile.Headers.Add("Origin", "https://evil.example");
            using var rejected = await client.SendAsync(hostile, cancellation.Token);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);

            // The same request without a foreign Origin gets as far as routing, which is what
            // proves the middleware is not simply blocking everything.
            using var local = new HttpRequestMessage(HttpMethod.Post, "anything");
            using var routed = await client.SendAsync(local, cancellation.Token);
            Assert.Equal(HttpStatusCode.NotFound, routed.StatusCode);

            await app.StopAsync(cancellation.Token);
        }
    }
}
```

Add these imports at the top of the file: `using System.Net;`, `using LightDrop.Daemon.Discovery;`, `using LightDrop.Daemon.Tests.TestSupport;`.

- [ ] **Step 7: Run the full daemon suite**

Run: `dotnet test tests/LightDrop.Daemon.Tests`

Expected: PASS, all tests. If `RejectsACrossOriginWriteOverRealHttp` fails with 404 on the first assertion, the middleware was registered after routing — move it directly under `builder.Build()`.

- [ ] **Step 8: Verify the build is clean**

Run: `dotnet build LightDrop.sln`

Expected: `Build succeeded.` with **0 warnings**.

- [ ] **Step 9: Commit**

```bash
git add src/LightDrop.Daemon/Security/LoopbackOriginPolicy.cs src/LightDrop.Daemon/LightDropDaemon.cs tests/LightDrop.Daemon.Tests/LoopbackOriginPolicyTests.cs
git commit -m "feat: reject non-GET requests from foreign origins

Loopback binding keeps every other device on the network out, and does nothing
about the browser running on this machine. Any page the user has open can send a
request to 127.0.0.1, so once pairing gains a POST endpoint that would be enough
for a hostile page to pair this machine with an attacker already on the LAN.

Shipped before any endpoint needs it, deliberately. A check added alongside the
first write endpoint is a check the second one has to remember.

Host is checked when no Origin is present rather than trusting the connection's
remote address, because a name the attacker controls can resolve to 127.0.0.1 --
such a request arrives on loopback looking local. The CLI sends no Origin and its
Host is the loopback address, so it is unaffected."
```

---

### Task 2: Serve the page

**Files:**
- Create: `src/LightDrop.Daemon/Ui/index.html`
- Create: `src/LightDrop.Daemon/Endpoints/UiEndpoints.cs`
- Modify: `src/LightDrop.Daemon/LightDrop.Daemon.csproj` (add an `EmbeddedResource` item group)
- Modify: `src/LightDrop.Daemon/LightDropDaemon.cs` (add `app.MapUiEndpoints();`)
- Test: `tests/LightDrop.Daemon.Tests/UiEndpointTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 beyond the middleware already being installed.
- Produces: `IEndpointRouteBuilder.MapUiEndpoints() -> IEndpointRouteBuilder`, serving `GET /` as `text/html; charset=utf-8`.

- [ ] **Step 1: Write the failing test**

Create `tests/LightDrop.Daemon.Tests/UiEndpointTests.cs`:

```csharp
using System.Net;
using LightDrop.Core.Configuration;
using LightDrop.Daemon.Discovery;
using LightDrop.Daemon.Tests.TestSupport;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// The page the daemon serves at its root.
/// </summary>
/// <remarks>
/// Embedded in the assembly rather than published as a file, because LightDrop ships as one
/// executable. A missing embedded resource is a runtime failure, not a build one, so it is worth
/// a test that actually fetches it.
/// </remarks>
public sealed class UiEndpointTests
{
    [Fact]
    public async Task ServesThePageAtTheRoot()
    {
        using var directory = new TempDataDirectory();
        var endpoint = new DaemonEndpointOptions { Host = "127.0.0.1", Port = FreeTcpPort.Get() };
        using var cancellation = new CancellationTokenSource();

        var app = LightDropDaemon.Create(endpoint, directory.FullPath, new NoOpPeerDiscoveryTransport());
        await using (app.ConfigureAwait(false))
        {
            await app.StartAsync(cancellation.Token);

            using var client = new HttpClient { BaseAddress = endpoint.ClientAddress };
            using var response = await client.GetAsync("/", cancellation.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync(cancellation.Token);
            Assert.Contains("LightDrop", body, StringComparison.Ordinal);

            // Proves the real page was served rather than an empty stream: the peer list is the
            // element the page exists for.
            Assert.Contains("id=\"peers\"", body, StringComparison.Ordinal);

            await app.StopAsync(cancellation.Token);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/LightDrop.Daemon.Tests --filter "FullyQualifiedName~UiEndpointTests"`

Expected: FAIL with `404 NotFound` — the assertion on `HttpStatusCode.OK`.

- [ ] **Step 3: Create the page**

Create `src/LightDrop.Daemon/Ui/index.html`:

```html
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>LightDrop</title>
<style>
  :root { color-scheme: light dark; }
  body {
    font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif;
    max-width: 44rem; margin: 3rem auto; padding: 0 1.5rem;
  }
  h1 { font-size: 1.4rem; margin: 0 0 .25rem; }
  .status { color: #666; margin-bottom: 2rem; }
  .status.down { color: #b3261e; }
  table { width: 100%; border-collapse: collapse; }
  th { text-align: left; font-weight: 600; font-size: .8rem; text-transform: uppercase;
       letter-spacing: .04em; color: #666; border-bottom: 1px solid #8884; padding: .5rem 0; }
  td { padding: .6rem 0; border-bottom: 1px solid #8882; }
  .muted { color: #666; }
  .empty { color: #666; padding: 1.5rem 0; }
</style>
</head>
<body>
  <h1>LightDrop</h1>
  <p class="status" id="status">Connecting…</p>

  <h2 style="font-size:1rem">Nearby devices</h2>
  <table>
    <thead><tr><th>Device</th><th>Platform</th><th>Address</th></tr></thead>
    <tbody id="peers"></tbody>
  </table>
  <p class="empty" id="empty" hidden>No devices found yet.</p>

<script>
const status = document.getElementById('status');
const peers = document.getElementById('peers');
const empty = document.getElementById('empty');

function text(value) {
  const cell = document.createElement('td');
  // textContent, never innerHTML: every value here originated in an mDNS record put on the
  // network by someone else.
  cell.textContent = value ?? '';
  return cell;
}

async function refresh() {
  try {
    const [healthResponse, peersResponse] = await Promise.all([
      fetch('health'), fetch('api/peers')
    ]);
    if (!healthResponse.ok || !peersResponse.ok) throw new Error('bad status');

    const health = await healthResponse.json();
    const peerList = await peersResponse.json();
    const list = peerList.peers ?? [];

    const discovery = peerList.discoveryRunning ? 'discovering' : 'discovery stopped';
    status.textContent = `${health.deviceName} · ${discovery} · ${list.length} nearby`;
    status.classList.remove('down');

    peers.replaceChildren(...list.map(peer => {
      const row = document.createElement('tr');
      row.append(text(peer.deviceName), text(peer.platform), text(peer.address));
      return row;
    }));
    empty.hidden = list.length > 0;
  } catch {
    // Say so rather than leaving a stale list on screen looking current.
    status.textContent = 'Daemon unreachable.';
    status.classList.add('down');
  }
}

refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
```

- [ ] **Step 4: Embed it in the assembly**

In `src/LightDrop.Daemon/LightDrop.Daemon.csproj`, add a new item group after the `ProjectReference` one:

```xml
  <ItemGroup>
    <!-- Compiled into the assembly rather than published beside it: LightDrop ships as a single
         executable, so there is no content directory to serve from. LogicalName is pinned so the
         resource name does not move if the folder is renamed. -->
    <EmbeddedResource Include="Ui/index.html" LogicalName="LightDrop.Daemon.Ui.index.html" />
  </ItemGroup>
```

- [ ] **Step 5: Write the endpoint**

Create `src/LightDrop.Daemon/Endpoints/UiEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace LightDrop.Daemon.Endpoints;

internal static class UiEndpoints
{
    private const string ResourceName = "LightDrop.Daemon.Ui.index.html";

    /// <summary>
    /// Maps <c>GET /</c>: the page a browser loads.
    /// </summary>
    /// <remarks>
    /// Read once at startup and held in memory. The page is a few kilobytes, it cannot change
    /// while the process runs, and loading it here means a missing embedded resource fails at
    /// startup rather than on the first request.
    /// </remarks>
    public static IEndpointRouteBuilder MapUiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var page = LoadPage();

        endpoints.MapGet("/", FileContentHttpResult () =>
            TypedResults.Bytes(page, "text/html; charset=utf-8"))
            .WithName("Ui");

        return endpoints;
    }

    private static byte[] LoadPage()
    {
        using var stream = typeof(UiEndpoints).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded page '{ResourceName}' is missing from the daemon assembly. It is " +
                "declared as an EmbeddedResource in LightDrop.Daemon.csproj.");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
```

- [ ] **Step 6: Map it**

In `src/LightDrop.Daemon/LightDropDaemon.cs`, inside `Create`, the block becomes:

```csharp
        var app = builder.Build();
        app.UseLoopbackOriginCheck(endpoint);
        app.MapUiEndpoints();
        app.MapHealthEndpoints();
        app.MapPeerEndpoints();
        return app;
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test tests/LightDrop.Daemon.Tests --filter "FullyQualifiedName~UiEndpointTests"`

Expected: PASS.

If it fails on the embedded resource being missing, run `dotnet build src/LightDrop.Daemon` and check the `LogicalName` in the csproj matches `ResourceName` exactly, character for character.

- [ ] **Step 8: See the page against a real daemon**

The field names above are taken from `HealthResponse` (`deviceName`) and `PeerListResponse` / `DiscoveredPeer` (`peers`, `discoveryRunning`, `deviceName`, `platform`, `address`), serialized camelCase by `LightDropJsonContext`. They are correct as written — this step is to see it working, not to fix names.

Run `dotnet run --project src/LightDrop.Cli -- daemon` in one terminal, then open <http://127.0.0.1:5533> in a browser.

Expect the device name, `discovering`, and a peer count. If a second machine on the network is running a daemon it appears in the table within a few seconds. Stop with Ctrl+C.

- [ ] **Step 9: Run the full suite and check the build**

Run: `dotnet build LightDrop.sln && dotnet test LightDrop.sln`

Expected: `Build succeeded.` with 0 warnings, and all tests passing.

- [ ] **Step 10: Commit**

```bash
git add src/LightDrop.Daemon/Ui/index.html src/LightDrop.Daemon/Endpoints/UiEndpoints.cs src/LightDrop.Daemon/LightDrop.Daemon.csproj src/LightDrop.Daemon/LightDropDaemon.cs tests/LightDrop.Daemon.Tests/UiEndpointTests.cs
git commit -m "feat: serve a page showing daemon status and nearby devices

One HTML file with inline CSS and JS, embedded in the daemon assembly and served
by one endpoint. No wwwroot, no static-file middleware, no build step -- LightDrop
ships as a single executable, so there is no content directory to serve from.

The page adds no API. It polls the health and peers endpoints that already exist,
every two seconds, and says the daemon is unreachable rather than leaving a stale
list on screen looking current. No WebSocket: that is M4, and polling loopback
costs nothing.

Peer values are written with textContent rather than innerHTML. Every one of them
originated in an mDNS record that somebody else put on the network."
```

---

### Task 3: `lightdrop ui`

**Files:**
- Create: `src/LightDrop.Cli/Commands/UiCommand.cs`
- Modify: `src/LightDrop.Cli/Program.cs:37` (register the command)
- Test: `tests/LightDrop.Daemon.Tests/` — none. See step 1.

**Interfaces:**
- Consumes: `LightDropDaemon.Create(DaemonEndpointOptions?, string?, IPeerDiscoveryTransport?) -> WebApplication`; `DaemonCommand.DataDirectoryEnvironmentVariable` (a `public const string` on the internal `DaemonCommand`, so it is reachable within the CLI assembly).
- Produces: an `ICliCommand` named `ui`.

- [ ] **Step 1: Understand what is and is not tested here**

There is no test project for `LightDrop.Cli`, and this command's two real behaviours are starting a web host and launching a browser — neither is worth a new test project for a tool with one user. **The address-in-use detection is the one piece with logic**, and it is tested through the daemon suite in step 5. The rest is verified by hand in step 7.

Do not add a `LightDrop.Cli.Tests` project. That is out of scope for this plan.

- [ ] **Step 2: Write the failing test for address-in-use detection**

`lightdrop ui` needs to recognise the port already being taken. That is the only piece of this command with logic in it, so it comes first. Create `tests/LightDrop.Daemon.Tests/AddressInUseTests.cs`:

```csharp
using System.Net.Sockets;

namespace LightDrop.Daemon.Tests;

/// <summary>
/// Recognising the port already being taken.
/// </summary>
/// <remarks>
/// This is the one branch in <c>lightdrop ui</c>: a bind failure is assumed to be our own daemon,
/// so the browser opens against it instead. Kestrel wraps the socket error, so the check has to
/// walk the chain rather than match the outermost type.
/// </remarks>
public sealed class AddressInUseTests
{
    [Fact]
    public void RecognisesAWrappedAddressInUseError()
    {
        // The shape Kestrel actually throws: an IOException wrapping the socket error.
        var wrapped = new IOException(
            "Failed to bind to address http://127.0.0.1:5533: address already in use.",
            new SocketException((int)SocketError.AddressAlreadyInUse));

        Assert.True(LightDropDaemon.IsAddressInUse(wrapped));
    }

    [Fact]
    public void RecognisesABareSocketError()
    {
        Assert.True(LightDropDaemon.IsAddressInUse(new SocketException((int)SocketError.AddressAlreadyInUse)));
    }

    [Fact]
    public void DoesNotSwallowOtherSocketErrors()
    {
        // Permission denied must not be reported as "already running" -- that would send the user
        // to a browser tab instead of telling them the real problem.
        var denied = new IOException("Failed to bind.", new SocketException((int)SocketError.AccessDenied));

        Assert.False(LightDropDaemon.IsAddressInUse(denied));
    }

    [Fact]
    public void DoesNotSwallowUnrelatedFailures()
    {
        Assert.False(LightDropDaemon.IsAddressInUse(new InvalidOperationException("something else")));
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test tests/LightDrop.Daemon.Tests --filter "FullyQualifiedName~AddressInUseTests"`

Expected: a compile error that `IsAddressInUse` does not exist. Add the method in the next step, re-run, and confirm all 4 tests pass before writing the command.

- [ ] **Step 4: Add the helper**

In `src/LightDrop.Daemon/LightDropDaemon.cs`, add `using System.Net.Sockets;` and this public method next to `RunAsync`:

```csharp
    /// <summary>
    /// Whether a startup failure was the port already being in use.
    /// </summary>
    /// <remarks>
    /// Kestrel reports this as an <see cref="IOException"/> wrapping the socket error, so the
    /// chain has to be walked rather than the outermost type matched. Narrow on purpose: a
    /// permission failure must not be reported as "already running", which would send the user to
    /// a browser tab instead of telling them what actually went wrong.
    /// </remarks>
    public static bool IsAddressInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }
```

- [ ] **Step 5: Write the command**

Create `src/LightDrop.Cli/Commands/UiCommand.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using LightDrop.Core.Configuration;

namespace LightDrop.Cli.Commands;

/// <summary>
/// <c>lightdrop ui</c> — runs the daemon and opens the page in a browser.
/// </summary>
/// <remarks>
/// Deliberately the same code path as <c>lightdrop daemon</c> with a browser tab on top, rather
/// than a client that probes for a running daemon and attaches to one. Probing first races when
/// two invocations both find nothing and both try to bind, and an attach mode would be a second
/// way for the daemon to be running.
/// <para>
/// The one branch is for the double-click case: launched from a shortcut or an app bundle, a bind
/// failure would otherwise mean nothing visible happens at all.
/// </para>
/// </remarks>
internal sealed class UiCommand(DaemonEndpointOptions endpoint) : ICliCommand
{
    public string Name => "ui";

    public string Description => "Open the LightDrop page in a browser, starting the daemon if needed.";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken)
    {
        var dataDirectory = Environment.GetEnvironmentVariable(DaemonCommand.DataDirectoryEnvironmentVariable);

        var app = Daemon.LightDropDaemon.Create(
            endpoint, string.IsNullOrWhiteSpace(dataDirectory) ? null : dataDirectory);

        await using (app.ConfigureAwait(false))
        {
            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (Daemon.LightDropDaemon.IsAddressInUse(ex))
            {
                // Assumed to be our own daemon rather than verified with a probe. If it is not,
                // the cost is a browser tab that does not load -- which says nearly as much, for
                // one user on a fixed port.
                Console.WriteLine($"A LightDrop daemon is already running. Opening {endpoint.ClientAddress}");
                OpenBrowser(endpoint.ClientAddress);
                return 0;
            }

            Console.WriteLine($"LightDrop is running at {endpoint.ClientAddress} — press Ctrl+C to stop.");
            OpenBrowser(endpoint.ClientAddress);

            // Closing the browser tab does not stop this. The daemon is the point of the process;
            // discovery has to keep running for the machine to stay visible to its peers.
            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    /// <remarks>
    /// Never fatal. A machine with no registered browser, or a desktop session that cannot be
    /// reached, should still leave the daemon running with its address on screen.
    /// </remarks>
    private static void OpenBrowser(Uri address)
    {
        try
        {
            // UseShellExecute is what hands the URL to the OS handler -- ShellExecute on Windows,
            // `open` on macOS. Without it the runtime tries to execute the URL as a program.
            using var browser = Process.Start(new ProcessStartInfo(address.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            Console.WriteLine($"Could not open a browser. Go to {address} yourself.");
        }
    }
}
```

- [ ] **Step 6: Register the command**

In `src/LightDrop.Cli/Program.cs`, below the existing registrations at line 37:

```csharp
services.AddSingleton<ICliCommand, DaemonCommand>();
services.AddSingleton<ICliCommand, HealthCommand>();
services.AddSingleton<ICliCommand, PeersCommand>();
services.AddSingleton<ICliCommand, UiCommand>();
```

- [ ] **Step 7: Verify by hand**

Run: `dotnet run --project src/LightDrop.Cli -- ui`

Expect a browser tab at `http://127.0.0.1:5533/` showing the device name and any nearby machines. Then, leaving it running, in a second terminal:

Run: `dotnet run --project src/LightDrop.Cli -- ui`

Expect `A LightDrop daemon is already running.`, a second browser tab, and an immediate exit. Stop the first with Ctrl+C and confirm it shuts down cleanly.

- [ ] **Step 8: Check the build and the trimmed publish**

Run: `dotnet build LightDrop.sln && dotnet test LightDrop.sln`

Expected: 0 warnings, all tests passing.

Run: `dotnet publish src/LightDrop.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true`

Expected: **zero trim warnings.** The embedded resource and `Process.Start` are both trim-safe, but this is the check that proves it rather than assuming.

- [ ] **Step 9: Commit**

```bash
git add src/LightDrop.Cli/Commands/UiCommand.cs src/LightDrop.Cli/Program.cs src/LightDrop.Daemon/LightDropDaemon.cs tests/LightDrop.Daemon.Tests/AddressInUseTests.cs
git commit -m "feat: add lightdrop ui

The same code path as \`lightdrop daemon\` with a browser tab on top, rather than a
client that probes for a running daemon and attaches to one. Probing first races
when two invocations both find nothing and both try to bind, and an attach mode
would be a second way for the daemon to be running.

One branch: a bind failure from the port being taken is assumed to be our own
daemon, so the browser opens against it and the process exits. Assumed rather
than verified -- if it is something else, the cost is a tab that does not load,
which for one user on a fixed port says nearly as much. The branch exists for the
double-click case, where a bind failure would otherwise mean nothing visible
happens at all.

The detection is narrow deliberately: a permission failure must not be reported
as \"already running\", which would send the user to a browser tab instead of the
real problem.

Closing the tab does not stop the process. The daemon is the point of it, and
discovery has to keep running for this machine to stay visible to its peers."
```

---

### Task 4: Launchers and documentation

**Files:**
- Create: `packaging/macos/make-app-bundle.sh`
- Create: `packaging/windows/create-shortcut.ps1`
- Modify: `README.md`
- Modify: `docs/DECISIONS.md` (append a new numbered entry)
- Modify: `docs/Architecture.md` (add the UI as a daemon client)

**Interfaces:**
- Consumes: the `ui` verb from Task 3.
- Produces: nothing other tasks depend on. This is the last task.

- [ ] **Step 1: Write the macOS bundle script**

Create `packaging/macos/make-app-bundle.sh`:

```bash
#!/usr/bin/env bash
# Builds LightDrop.app -- a double-clickable launcher for `lightdrop ui`.
#
# An .app bundle is a directory with a required shape, not a compiled artifact, so this needs no
# Xcode and no Swift. The bundle launches the binary; it does not contain it, which keeps the
# single-executable story intact.
#
# Usage: ./make-app-bundle.sh /path/to/lightdrop [output-directory]
set -euo pipefail

BINARY="${1:?usage: make-app-bundle.sh /path/to/lightdrop [output-directory]}"
OUTPUT="${2:-$PWD}"

if [ ! -x "$BINARY" ]; then
  echo "No executable at $BINARY" >&2
  echo "Publish one first, then pass its path:" >&2
  echo "  dotnet publish src/LightDrop.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true" >&2
  exit 1
fi

BINARY="$(cd "$(dirname "$BINARY")" && pwd)/$(basename "$BINARY")"
APP="$OUTPUT/LightDrop.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>LightDrop</string>
  <key>CFBundleIdentifier</key><string>dev.lightdrop.launcher</string>
  <key>CFBundleVersion</key><string>1.0</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>LightDrop</string>
</dict>
</plist>
PLIST

cat > "$APP/Contents/MacOS/LightDrop" <<LAUNCHER
#!/bin/sh
exec "$BINARY" ui
LAUNCHER

chmod +x "$APP/Contents/MacOS/LightDrop"

echo "Built $APP"
echo "It launches: $BINARY ui"
```

- [ ] **Step 2: Make it executable and test it**

```bash
chmod +x packaging/macos/make-app-bundle.sh
dotnet publish src/LightDrop.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true
./packaging/macos/make-app-bundle.sh src/LightDrop.Cli/bin/Release/net10.0/osx-arm64/publish/lightdrop /tmp
open /tmp/LightDrop.app
```

Expected: a browser tab opens at `http://127.0.0.1:5533/`. If Gatekeeper blocks the published binary, clear the quarantine attribute and try again:

```bash
xattr -d com.apple.quarantine src/LightDrop.Cli/bin/Release/net10.0/osx-arm64/publish/lightdrop
```

(This applies to a binary that was downloaded. One built locally normally has no quarantine attribute.)

- [ ] **Step 3: Write the Windows shortcut script**

Create `packaging/windows/create-shortcut.ps1`:

```powershell
# Creates a Start Menu shortcut to `lightdrop ui`, pinnable to the taskbar.
#
# A shortcut rather than an installer: LightDrop is one executable that needs no installation, and
# this only records where the user already put it.
#
# Usage: .\create-shortcut.ps1 -Binary C:\tools\lightdrop.exe

param(
    [Parameter(Mandatory = $true)][string]$Binary
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Binary)) {
    throw "No executable at $Binary. Publish one first with: dotnet publish src/LightDrop.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true"
}

$Binary = (Resolve-Path -LiteralPath $Binary).Path
$linkPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'LightDrop.lnk'

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($linkPath)
$shortcut.TargetPath = $Binary
$shortcut.Arguments = 'ui'
$shortcut.WorkingDirectory = Split-Path -Parent $Binary
$shortcut.Description = 'Open the LightDrop page'
$shortcut.Save()

Write-Host "Created $linkPath"
Write-Host "It launches: $Binary ui"
```

- [ ] **Step 4: Document both in the README**

Add a section to `README.md` after the existing usage instructions:

```markdown
## The page

`lightdrop ui` runs the daemon and opens a browser at <http://127.0.0.1:5533>, showing this
device's status and the machines it can see nearby. Ctrl+C stops it — closing the browser tab does
not, because the daemon has to keep running for this machine to stay visible to its peers.

If a daemon is already running it opens the page against that one and exits.

### Launching it without a terminal

**macOS** — build a double-clickable bundle:

```bash
./packaging/macos/make-app-bundle.sh path/to/lightdrop ~/Applications
```

**Windows** — add a Start Menu entry, then pin it to the taskbar:

```powershell
.\packaging\windows\create-shortcut.ps1 -Binary C:\tools\lightdrop.exe
```

Neither installs anything. They point at the executable wherever you already keep it.
```

- [ ] **Step 5: Record the decision**

Append to `docs/DECISIONS.md`, continuing the existing numbering (the last entry is #23, so this is #24):

```markdown
---

### 24. The UI is a web page, and there is no tray icon

LightDrop needed something to look at. The cheap answer and the expensive one differ by a large
dependency and roughly triple the binary, so it was worth deciding rather than drifting.

**A page served by the Kestrel already running.** One `index.html` embedded in the assembly, served
by one endpoint, polling the health and peers endpoints that already exist. It adds no dependency,
no size and no trim risk, and both platforms work the day it is written — which is what makes
"macOS later" free rather than a second project. Avalonia is the right answer if a polished native
app is the product; here it buys polish against value that does not exist until file transfer does.
A webview shell would wrap this same HTML, so starting in the browser forecloses nothing.

**No tray icon.** It is two separate implementations. On Windows `NotifyIcon` brings a Windows-only
target framework and sits badly with trimming, so doing it properly means `Shell_NotifyIcon` through
P/Invoke, with a hidden window and a message pump. On macOS it means `NSStatusItem`, so AppKit
interop and a run loop that wants the main thread — there is no small path to the second one. All of
it buys "the daemon is running", which the page says as it loads. A shortcut and a minimal `.app`
bundle cover launching it, and a tray earns its place at M3 alongside notifications, when a webview
or Avalonia shell would supply one for free.

**An origin check shipped before anything needed it.** Loopback binding keeps other devices out and
does nothing about the browser on this machine: any page the user has open can post to 127.0.0.1.
Phase A has no write endpoint, so the check guards nothing today — that is the point. A check added
alongside the first write endpoint is a check the second one has to remember.
```

- [ ] **Step 6: Add the UI to the architecture doc**

In `docs/Architecture.md`, add a paragraph to the section describing the daemon's responsibilities:

```markdown
The daemon also serves the page `lightdrop ui` opens: one `index.html` embedded in the assembly,
plus an origin check that rejects non-GET requests which did not come from it. The page is a client
of the same loopback HTTP the CLI uses — it adds no API of its own and no LAN-reachable surface.
```

- [ ] **Step 7: Verify nothing broke**

Run: `dotnet build LightDrop.sln && dotnet test LightDrop.sln`

Expected: 0 warnings, all tests passing. (This task changes no C#, so this is a guard against an accidental edit.)

- [ ] **Step 8: Commit**

```bash
git add packaging README.md docs/DECISIONS.md docs/Architecture.md
git commit -m "feat: add launchers for the UI, and record the decision

A Windows shortcut and a macOS .app bundle, so the page can be opened without a
terminal. An .app bundle is a directory with a required shape rather than a
compiled artifact, so this needs no Xcode and no Swift, and neither launcher
installs anything -- they point at the executable wherever the user already keeps
it.

DECISIONS #24 records why the UI is a web page rather than a desktop framework,
why there is no tray icon yet, and why the origin check shipped in a phase that
has no write endpoint for it to guard."
```

---

## What phase A does not do

Deliberately out of scope, per the spec:

- **Pairing.** The ceremony needs the TLS session from M2 step 2. Phase B adds it to the same page.
- **A trusted peers list.** Nothing can be trusted until pairing works, so the list would always be empty.
- **Unpairing from the page.** CLI only — the M2 design makes replacing a pinned key deliberately explicit, and a button undoes that.
- **Tray icon, notifications, drag-and-drop, sending files.** M3 and later.
- **A `LightDrop.Cli.Tests` project.** Do not add one.
