# TraceHarness

Runs the desktop wallet's `AncestryTracer` through the real `BlazecoindRpcService` against a
running daemon, from the command line, read-only. It is how the 2026-09-30 Provenance fix was
measured (time, reads, memory, both models' catalogues) without the GUI in the way.

    dotnet run -c Release -- <datadir> <rpcport> <wallet>                 # every unspent output
    dotnet run -c Release -- <datadir> <rpcport> <wallet> largest         # the biggest coin only
    dotnet run -c Release -- <datadir> <rpcport> <wallet> outpoint <txid> <vout> <satoshis>

Auth is the daemon's `.cookie` in `<datadir>`. `SEGCAP=<n>` in the environment overrides
`MaxSegmentsPerOutput` (used to measure the segment caches' share of memory). Not part of any
solution or release; nothing here is deployed.
