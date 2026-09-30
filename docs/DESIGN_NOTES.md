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

So `TcpConnectLatencyProbe` times a TCP handshake, which tracks the path well enough to rank
gateways and needs no privileges. It is not the tunnel's latency, and I made sure nothing in the
UI says it is. Putting the measurement behind `ILatencyProbe` was the right call for a second
reason I did not anticipate: it let me write `DeterministicLatencyProbe`, which is what makes the
standalone mode useful. Plausible, stable numbers for gateways that do not exist, with the pane
saying they are simulated.

Real gateways taught me two more things. The first probe timed `Socket.ConnectAsync(host, port)`,
which includes the DNS lookup and every failed attempt on an address family the network cannot
reach. Behind a network without IPv6, every gateway with an AAAA record measured about a second
slower than the one without. The probe now resolves first, keeps that out of the figure, and
tries IPv4 before IPv6.

The second was that my gateways expose only SSH and WireGuard, so the only TCP port to time was
SSH, and SSH sits behind a per-source connection limit. A few refreshes spent the allowance, a
SYN was dropped, and the figure grew by TCP's one second retransmission delay. I first tried
rejecting TCP on the WireGuard port with a reset, which would have given a round trip without a
service behind it, but the hosting provider drops outgoing resets. So the default is now
`IcmpLatencyProbe`. On Linux, `Ping` without root runs the system's `ping` utility, which itself
uses an unprivileged ICMP socket, so the privilege concern above does not apply on the platform
I run, and the SSH limit stays strict. Where no `ping` is installed, as in a minimal container,
the probe reports a failed measurement instead of throwing; a clean-container test run found
that it used to throw. The TCP
probe is still there for networks that filter ICMP.

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

The X25519 primitive comes from BouncyCastle. The .NET base class library has no X25519, and
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

The authentication filter is attached to the route group, not to each endpoint. A third peer
endpoint added later cannot forget it, which is how an endpoint ends up unprotected.

The first version had a shared static API key and `EnsureCreatedAsync`, both marked as
deliberate weaknesses of a lab. Putting the API on the internet made both of them wrong, so
they are gone; the section on going live below explains what replaced them.

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

## Going live: from a lab to four real gateways

The project started with a simulated tunnel and fictional gateways. It now runs against four
real ones: three fresh virtual servers and one existing server that also hosts the control
plane. Each decision below is one I expect to be asked about.

### Per-device tokens instead of a shared key

A shared key has to be in every client, so one leaked configuration lets anyone register as
many peers as they like on my servers, and the only way to stop them is to change the key for
everybody. I replaced it with a token per device and a token per gateway: 256 random bits,
stored as a SHA-256 hash, sent as a bearer credential. A device token can manage only that
device's own registration; a gateway token can read only that gateway's peer list. Revoking one
device removes its peers and touches nothing else. SHA-256 without a salt is enough because the
input is random: salting and slow hashing defend guessable passwords, and nobody guesses 256
bits. The cost is an enrolment step, done from a command line on the host.

### Admin on the command line, not over HTTP

Enrolling and revoking devices and registering gateways are `admin` subcommands of the API
binary, run inside the container over SSH. That means there is no admin endpoint to attack and
no admin credential to leak: reaching the commands already requires a shell on the host, which
is a stronger check than anything the API could add. The cost is that a revocation made there
cannot wake the gateways' held polls, since it runs in another process, so it takes effect on
the poll's timeout, ten seconds at most.

### Gateways pull; the control plane never connects to them

The alternative was the control plane pushing peers to gateways over SSH. That would make the API
the holder of a root credential for every gateway, and one compromise of the API would be a
compromise of the fleet. With a pull, each gateway holds one narrow token and the API holds
nothing. I also made the agent validate what it receives: it admits a peer only if its addresses
are single hosts inside the gateway's tunnel range. Without that, a compromised API could hand
a peer `0.0.0.0/0` and have the gateway route every client's traffic to it. The agent is the
last place the gateway can refuse, so it refuses there.

### Long poll with an acknowledgement

A plain poll every few seconds produced a first handshake of five to six seconds. The client's
handshake reached the gateway before the gateway had admitted the key, WireGuard dropped it
silently, and the client retried after its fixed five second timeout. Polling faster only makes
that race less likely. The fix is a protocol: the agent long-polls with the version it last
applied, a registration wakes the held poll, and the agent's next request carries the version
it has just applied, which is the acknowledgement the registration waits for before answering
the client. The response says whether the gateway confirmed, and the wait is bounded so a dead
gateway delays a registration by a few seconds and no more. Measured on the real deployment,
registration to first handshake went from about six seconds to about one. The state is in
memory, which is right for one instance; several would need a shared store for the version and
the wake-up signal. Versions start from the clock at startup, so an acknowledgement from before
a restart can never confirm a registration made after it.

### One registration per device

Registering again, on any gateway, releases what the device held before. The client runs one
tunnel, so an older registration can only be left over from a crash, and leaving it would keep a
key admitted that nothing will use. It also caps what a stolen device token can consume at one
address.

### IPv6: routed into the tunnel even where the gateway cannot forward it

Three gateways have IPv6 and one does not. The easy option on the one without would be to leave
IPv6 out of the client's routes, and that would leak: the client's IPv6 traffic would go straight
out of its own network, outside the tunnel. So every client routes `::/0` into the tunnel and
gets an address from a unique local /64. Where the gateway has IPv6, it is NATed out; where it
does not, the gateway refuses it with an ICMPv6 "administratively prohibited", and the
application falls back to IPv4 at once instead of waiting for a timeout. Measured: the IPv6
attempt fails in under 300 ms on that gateway. RFC 6724 ranks a unique local source below IPv4,
so most connections never try IPv6 at all.

### Migrations instead of `EnsureCreated`

A deployment keeps its database across upgrades, and `EnsureCreated` cannot alter an existing
schema. The first new column would have failed at runtime. Migrations are applied at startup,
which suits one instance; several instances would move that into the deployment pipeline.

### What the gateway firewall refuses

Clients cannot reach each other, private and link-local ranges, or outbound SMTP. The
link-local block matters more than it looks: without it, any client could query the cloud
provider's metadata service at 169.254.169.254 from inside the provider's network. SMTP is
blocked because mail from a VPN address is almost always spam, and one spammer would get the
gateway's address onto blocklists that hurt every other client. Only addresses the control plane
issued may leave, so a client cannot spoof another's source address.

### Living next to an existing server

The fourth gateway also runs a mail server, websites and Docker, and I could not rewrite its
firewall. So the gateway rules live in their own nftables table, loaded by their own unit, that
only looks at traffic to or from `wg0`, and runs before the host's own rules so its refusals are
final. The host keeps ufw; the role adds three rules and removes nothing. I checked the host's
sites, mail ports and container networking before and after.

### Docker for the API, not for the gateways

The API runs in a container because it needs no privilege at all: no capabilities, a read-only
root filesystem, a loopback-only port. There the container is a real boundary. The gateway
components are different. WireGuard is a kernel interface, so a container running it needs the
host's network namespace and `CAP_NET_ADMIN`, and a process with both can rewrite the host's
firewall and routes from inside the container; the boundary would be nominal. unbound has to
listen on the tunnel address, which is the same constraint. Docker would also add a root daemon,
its own iptables rules next to the nftables tables here, and images that unattended upgrades do
not patch. So the gateway agent runs as a systemd service with the sandbox narrowed to what it
uses: only `CAP_NET_ADMIN`, a read-only filesystem, a system call filter and no way to gain
privileges, which is a tighter cage than a default container.

### Ansible, and secrets outside the repository

Ansible because what somebody else fills in to reuse this is an inventory, and because a second
run has to change nothing; I checked that it does not on the fresh hosts. Real hosts, the vault
and the inventory live in a directory outside the repository. The vault password is in the
desktop keyring rather than in a file beside the vault, so the encrypted secrets and their key
are never on disk together in the clear.

## What I know is missing

- No Windows tunnel. `WindowsServiceTunnel` throws from every member and documents what the real
  thing needs. I would rather ship a named gap than a backend that reports success without
  creating a tunnel.
- The kill switch routes everything into the tunnel but installs no firewall rules, so it does
  not survive the client being killed. Real enforcement means nftables or WFP filters from a
  privileged process, and that belongs on the privileged side of a split I have not built.
- The Linux client needs `CAP_NET_ADMIN` for `wg-quick`. A shipped client would have a small
  privileged helper; the demonstration runs the client in a container that has the capability
  and its own network namespace, so the host's routing is untouched.
- No address reclamation in the in-memory catalog. It never reuses an address, which is fine for
  a simulation and is the interesting half of the problem in a real control plane. The API does
  reclaim, by taking the lowest free index.
- No reconnect-on-drop. `TunnelStatistics.IsPeerAlive` is the check a watchdog would use, and
  there is no watchdog calling it.
- One control plane instance, with the long-poll state in memory. That is the right size for four
  gateways and would need a shared store to grow.
- The measurements in the README were taken from one place, a machine whose own traffic already
  goes through another WireGuard tunnel. They show what the design does, not what a user
  elsewhere would see; the command that produced them is next to them.
- The desktop client has no settings screen. Everything is configuration file or command line,
  which is fine for a lab and would not be for a user.
