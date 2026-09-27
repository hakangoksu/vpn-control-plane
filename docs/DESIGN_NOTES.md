# Design notes

These are the decisions I made while building this, and the alternatives I turned down. The
README says what the code does. This says why it is shaped the way it is, and where I think it
is weak.

## What I was trying to build

A VPN client is a small amount of cryptography surrounded by a lot of state handling, and the
state handling is the part that goes wrong. I wanted to build the whole shape of one: a desktop
UI, a backend that hands out tunnel parameters, and a platform layer that does the privileged
work, with a seam between them clear enough that the parts I cannot test are small and named.

I picked WireGuard as the tunnel protocol because its client side is simple enough to implement
honestly. A peer is a public key and an endpoint, there is no session to negotiate, and the
configuration file format is a dozen lines. That left me free to spend the effort on the parts I
actually wanted to practise.

## Three projects instead of one

I split it into `Core`, `Tunnel`, `Api` and `Desktop` rather than keeping the logic next to the
UI. The rule I held to is that `Core` references no UI framework, no web framework and no
external tooling. I did not do that for its own sake. I did it because I wanted to be able to
run the selection logic, the state machine and the key handling in a unit test with nothing
installed, and the only reliable way to keep that true is for the project not to be able to
reference the things that would break it.

The proof that the split is real is `VpnControl.Desktop.Tests`: it drives every view model with
no window, no display and no Avalonia runtime. If I ever let a brush or a dispatcher into a view
model, that project stops building.

## `IVpnTunnel`: keeping the untestable part small

The most useful decision in the repository is the smallest one. `IVpnTunnel` has three methods
and an event. Everything above it is portable C# with tests. Everything below it is `wg-quick`
shelling out to a tool, a privileged Windows service I did not write, or a simulation.

I kept it that narrow deliberately. A wider interface would have let platform concerns leak
upward, and then porting to a new platform becomes an audit of the whole codebase instead of
writing one class. It also meant I could write `SimulatedTunnel` first and build the entire
application against it before touching anything privileged.

`IsSimulated` is on the interface rather than inferred from the type. I went back and forth on
that, because it looks like the UI reaching into the implementation. It is not: the question
"can I trust this tunnel" is part of the contract, and the UI has to be able to ask it without
knowing which backends exist.

## The state machine as a table

My first version of the connection logic was a `ConnectionState` field on the manager, updated
wherever it needed changing. It worked, and I could not convince myself it was correct. There
were eight or nine assignments spread over four methods, and nothing stopped one of them setting
`Connected` on a path where the tunnel had not come up.

So I pulled it into `ConnectionStateMachine`, with the legal transitions as a dictionary and
every change going through one method. Three things fell out of that which I did not expect:

- The table is a specification. It is a public static property, so the README, the tests and the
  implementation read the same rules, and my exhaustive test over all thirty-six ordered pairs
  asserts against it rather than against a list I wrote by hand.
- Illegal transitions became useful errors. `ConnectAsync` does not check whether a session
  exists; it just asks for `Connecting`, and the machine throws if there is already a session.
  The caller who meant to move gateway has to say so by calling `SwitchToAsync`.
- Two states I would not have bothered with turned out to matter. `Switching` is separate from
  `Connecting` because a failed switch should be reported against a session the user thought was
  working, and the UI should not flash a disconnected look on the way. `Faulted` is separate from
  `Disconnected` so the window can keep showing the reason instead of quietly looking idle.

The transition I am most pleased with is `Disconnecting -> Faulted`. An interface that will not
come down is a leak, and reporting it as a clean disconnect would be the worst kind of wrong
answer. It costs one row in the table.

### What I rejected

I considered a full state machine library. For six states and fifteen transitions it would have
added a dependency and a DSL to learn, and the dictionary is nine lines that anybody can read.
I also considered making the states classes with behaviour, the state pattern proper. That is
the right answer when each state has substantially different behaviour; here the behaviour lives
in the orchestrator and the states are genuinely just labels, so classes would have been
ceremony.

## Selecting a gateway

The scoring rule is `latency_ms * (1 + LoadWeight * load/100)`. I tried adding a load penalty
first, `latency + k * load`, and disliked it: the units stop meaning anything, so I could not
look at a score and tell what it was saying. Multiplying keeps the score in milliseconds and
makes the penalty proportional, which also matches how it feels to use. Ten percent more load on
a distant gateway costs more real time than the same on a nearby one.

`MaxLoadPercent` is a separate cut-off rather than just a heavier weight, because a saturated
gateway is not a worse choice, it is one that will drop the connection.

Two decisions here were about testability, and both paid off immediately. `ServerSelector.Select`
is a pure function: no network, no clock, no logging. Ties, an entirely unreachable region and a
fully loaded fleet are all a matter of constructing a list. And the result carries the rejects
with a reason each, rather than silently dropping them. That started as a debugging convenience
and turned into the difference between "no server available" and an answer: when selection finds
nothing, the reasons are the whole story, and they are what the list pane shows in its tooltips.

Ties are broken by load, then latency, then identifier. The last step is not a preference. It is
there so the same input always gives the same output, which a test can rely on and a user will
not find surprising.

## Measuring latency, and admitting what the number is

This is the part where I had to stop and be honest with myself.

There is no good way for a client to measure a WireGuard gateway before it has been issued a
key. WireGuard never answers a packet it cannot authenticate, which is a good property and means
a UDP probe of its port cannot tell "unreachable" from "working correctly and staying silent".
ICMP needs elevated privileges on some platforms. A real handshake needs the key I have not been
given yet.

So `TcpConnectLatencyProbe` times a TCP handshake to port 443, which tracks the path well enough
to rank gateways and needs no privileges. It is not the tunnel's latency, and I made sure nothing
in the UI says it is. Putting the measurement behind `ILatencyProbe` was the right call for a
second reason I did not anticipate: it let me write `DeterministicLatencyProbe`, which is what
makes the standalone mode useful. Plausible, stable numbers for gateways that do not exist, with
the pane saying they are simulated.

## The catalog client and its two implementations

`IServerCatalogClient` exists so the manager never sees an `HttpClient`. `InMemoryServerCatalogClient`
is the implementation that justifies it: the whole application launches, fetches, ranks,
connects and disconnects with no backend running. It assigns addresses and issues peer
identifiers exactly as the API does, so nothing above it can tell the difference.

I put the retries in the client rather than in a `DelegatingHandler`, which is the more usual
place. The reason is that the decision about what counts as transient needs to know the catalog's
own semantics. A 404 for a gateway is an answer, not a failure, and must not be retried; a 404 on
a peer delete means there was nothing to delete, which is success from the caller's point of
view. A handler sees status codes without that context.

I wrote `RetryPolicy` by hand instead of taking Polly. In a service I would take Polly without
thinking about it. Here the mechanics are the subject, and injecting the delay function is what
lets the tests assert the backoff without waiting for it. I left jitter out and said so in the
comment: a real fleet needs it so clients retrying after an outage do not arrive in one wave,
and adding it would make the delays unpredictable in tests.

## Keys

A fresh key pair per session. It costs one scalar multiplication, and it means a key found on
disk later cannot be tied to a past session.

`WireGuardKeyPair` is disposable and its `Dispose` overwrites the private key buffer. This is
worth less in a managed runtime than it looks, since the garbage collector may have copied the
array during a compaction, so I want to be clear about what it buys: it narrows the window, it
does not close it. I still think it is correct, because the alternative is a buffer that lives
until the collector happens to reuse the page.

The X25519 primitive comes from BouncyCastle. The net8.0 base class library has no X25519, and
hand-rolling curve arithmetic for a lab project would have been the wrong kind of
ambitious.

`WireGuardConfig.ToRedactedConfigText()` exists because I could see myself adding a "show me the
configuration" button and printing the real thing. Having the safe version be the easy one to
reach for is cheaper than remembering.

## The API

Minimal API rather than controllers. For five endpoints the controller ceremony buys nothing,
and grouping the routes into an extension method per area keeps the startup file readable
without it. I did split them into `ServerEndpoints`, `PeerEndpoints` and `HealthEndpoints`,
because a minimal API is pleasant right up to the point where `Program.cs` is four hundred lines.

The API key filter is attached to the route group, not to each endpoint. A third peer endpoint
added later cannot forget it, which is how an endpoint ends up unprotected.

Two things I want to flag as deliberate weaknesses rather than oversights. The shared static API
key identifies nobody and cannot be revoked per device; it is there to show where a credential
attaches. And `EnsureCreatedAsync` instead of migrations is right for a database I throw away
and wrong for one anybody cares about, because it cannot alter an existing schema and will
silently leave an old one in place. Both are documented where they are used, not just here.

The uniqueness of a peer key per gateway is enforced by a database index, not only by the
handler's check. Two concurrent registrations can both read the same free address index before
either writes, so the index is the thing that actually prevents the duplicate, and the handler
translates the resulting `DbUpdateException` into a 409.

There is one comment in `Program.cs` that is longer than the code it explains, about reading the
connection string inside the DI callback rather than from `builder.Configuration`. That one cost
me time: two test classes were quietly sharing a database file because `WebApplicationFactory`
adds its configuration source after the builder has been read. I left the explanation long
because the symptom was confusing and the fix looks like a style preference.

## The desktop client

### Avalonia, not WPF

I built this on Arch Linux. WPF does not build or run there. I would rather have a client I can
run than a `.csproj` that only compiles on a machine I do not have.

Avalonia is the same idea, and I think the knowledge transfers cleanly: XAML views, compiled
bindings, data templates, value converters, styles with selectors, MVVM with a dispatcher. The
differences are the selector syntax and the assembly names. If the target is WPF, what I would need
to learn is where the equivalents live, not what they do.

### The dispatcher seam

Almost nothing calls a view model back on the UI thread. The tunnel raises its state change from
whichever thread brought the interface up, the connection manager forwards it unchanged, and the
logger writes from wherever the statement was reached. Touching an `ObservableCollection` from
any of those is an intermittent crash.

I could have called `Dispatcher.UIThread.Post` directly. Instead there is an `IUiDispatcher`
interface, with the whole Avalonia threading dependency in one small class. That is the single
decision that makes the view models testable: `ImmediateUiDispatcher` runs posted work inline,
and the test project drives everything with no Avalonia runtime at all. It also documents the
rule, because a view model with no UI types in it is a rule you can see.

### Where the commands live

I moved these twice. They ended up on `MainWindowViewModel`, not on `ConnectionViewModel`, and
the reason is that every interesting command needs to see more than one pane: connecting needs
the list's selection and the session's state. `ConnectionViewModel` is display only, and
`ServerListViewModel` owns no connection logic. The coordinator is the thing that decides what a
button press means.

The single primary button that becomes "switch to selected" when a session is already up came
out of that. With a tunnel running and a different gateway selected, "connect" is not what the
user means, and a second button they can only press in one state is clutter.

The kill switch is a command rather than a two-way bound property. Changing it on a live session
means rebuilding the tunnel, and a property setter has no honest way to do asynchronous work. I
would rather the toggle look slightly unusual in the XAML than have a setter that starts a task
and drops it.

### Polling, not pushing

The counters are polled once a second. That is not laziness about events: WireGuard exposes byte
counts to be read, not an event to subscribe to, so a client that offered a push API would be
polling underneath and hiding it. One second is what the numbers are worth, since they are
displayed to the nearest second and the nearest kibibyte.

The loop is a `PeriodicTimer` in an `async Task` I keep a reference to, not `async void`, so
disposal can wait for it instead of leaving it half way through a read. An `async void`
exception has nowhere to go but the finalizer thread.

### The log pane

I gave the window the application's own log rather than a separate stream of user-facing
messages. A client that fails and says only "could not connect" gives the user nothing, and
writing every interesting event twice, once for the log and once for the UI, would guarantee the
two disagree. `LogViewModel` implements `ILogSink`, a small logging provider forwards to it, and
the console provider stays registered alongside so the same lines reach a terminal.

The pane is capped at five hundred lines. A client left overnight with a reconnect loop would
otherwise grow until the process ran out of memory, and nobody scrolls back that far.

## Testing

No mocking library. The interesting behaviour in the connection manager is a sequence of calls
under failure, and I find that sequence far more readable as assertions on a hand-written double
that recorded what happened than as expectations set up on a mock. `FakeVpnTunnel` counts its
`Up` and `Down` calls, records the configurations it was handed, and fails on demand. That is
what the tests want to talk about.

The API tests run the real startup path through `WebApplicationFactory`, against a SQLite file
per factory rather than the in-memory provider. That is slower, and it means the tests cover the
routing, the model binding, the options binding, the endpoint filter and the actual SQL together.
Calling the handler methods directly would skip all of it, and in a minimal API that is where the
mistakes are.

The exhaustive state machine theory over all thirty-six ordered pairs is the test I would keep if
I could only keep one. It asserts against the published table rather than a list, so it stays
true as the table changes and fails when the table changes by accident.

## What I know is missing

- No Windows tunnel. `WindowsServiceTunnel` throws from every member and documents what the real
  thing needs. I would rather ship a named gap than a backend that reports success without
  creating a tunnel.
- The kill switch routes everything into the tunnel but installs no firewall rules, so it does
  not survive the client being killed. Real enforcement means nftables or WFP filters from a
  privileged process, and that belongs on the privileged side of a split I have not built.
- No address reclamation in the in-memory catalog. It never reuses an address, which is fine for
  a simulation and is the interesting half of the problem in a real control plane. The API does
  reclaim, by taking the lowest free index.
- No reconnect-on-drop. `TunnelStatistics.IsPeerAlive` is the check a watchdog would use, and
  there is no watchdog calling it.
- No measurements. Nothing here has been run under load, so there is no number in this repository
  claiming otherwise. Where a figure would help, the command that prints it is in the README
  instead.
- The desktop client has no settings screen. Everything is configuration file or command line,
  which is fine for a lab and would not be for a user.
