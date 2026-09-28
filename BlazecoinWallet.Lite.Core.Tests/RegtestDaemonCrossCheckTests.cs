using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BlazecoinWallet.Lite;
using NBitcoin;

namespace BlazecoinWallet.Lite.Tests;

/// <summary>
/// THE money-grade cross-check: the real `blazecoind -regtest` funds an address derived by
/// LiteHdWallet, LiteTransactionBuilder signs a spend of that UTXO client-side, and the
/// DAEMON must accept it (sendrawtransaction) and mine it. Everything before this test is
/// NBitcoin verifying itself — here the consensus implementation is the judge.
///
/// Opt-in: set BLZ_REGTEST_BIN to the daemon's Release folder (the test is a silent pass
/// when unset, so CI without the C++ build stays green):
///   $env:BLZ_REGTEST_BIN = "C:\...\Blazecoin_Wallet_V2_Core\build_msvc\x64\Release"
/// </summary>
public class RegtestDaemonCrossCheckTests
{
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Fact]
    public async Task Daemon_accepts_and_mines_a_lite_signed_spend()
    {
        var bin = Environment.GetEnvironmentVariable("BLZ_REGTEST_BIN");
        if (string.IsNullOrEmpty(bin) || !File.Exists(Path.Combine(bin, "blazecoind.exe")))
            return; // opt-in harness — no daemon available on this runner

        var datadir = Path.Combine(Path.GetTempPath(), $"blz-lite-regtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(datadir);
        Process? daemon = null;
        try
        {
            daemon = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(bin, "blazecoind.exe"),
                // -bind=127.0.0.1 → LISTENING on loopback 18444 so the P2P broadcast leg
                // has a real node to push to (loopback binding, no firewall prompt).
                Arguments = $"-regtest -datadir=\"{datadir}\" -bind=127.0.0.1 -server=1 -txindex=1",
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;

            using var rpc = await ConnectRpcAsync(datadir);

            // Fund the daemon's own wallet, then the lite wallet's address.
            await rpc.CallAsync<JsonElement>("createwallet", "miner");
            var minerAddr = await rpc.CallAsync<string>("getnewaddress");
            await rpc.CallAsync<JsonElement>("generatetoaddress", 101, minerAddr);

            // Wallet-native regtest derivation (audit F6 — no more bypassing the wallet class).
            var wallet = LiteHdWallet.Restore(TestMnemonic, network: BlazecoinNetwork.Regtest);
            var liteKey = wallet.GetPrivateKey(0);
            var liteAddr = wallet.GetReceiveAddress(0);

            var fundTxId = await rpc.CallAsync<string>("sendtoaddress", liteAddr, 10.0m);
            await rpc.CallAsync<JsonElement>("generatetoaddress", 1, minerAddr);

            // Locate our UTXO in the funding transaction.
            var fundTx = await rpc.CallAsync<JsonElement>("getrawtransaction", fundTxId, true);
            int vout = -1;
            long sats = 0;
            foreach (var o in fundTx.GetProperty("vout").EnumerateArray())
            {
                var spk = o.GetProperty("scriptPubKey");
                if (spk.TryGetProperty("address", out var a) && a.GetString() == liteAddr)
                {
                    vout = o.GetProperty("n").GetInt32();
                    sats = (long)Math.Round(o.GetProperty("value").GetDecimal() * 100_000_000m);
                }
            }
            Assert.True(vout >= 0, "funding output not found");
            Assert.Equal(1_000_000_000, sats); // 10 BLZ

            // Sign a spend of that UTXO ENTIRELY client-side: 4 BLZ back to the miner,
            // change to the lite address, zero fee (the chain's normal case).
            var signed = LiteTransactionBuilder.BuildAndSign(
                new[] { new LiteUtxo(fundTxId, vout, sats, liteAddr) },
                minerAddr, 400_000_000, liteAddr,
                _ => liteKey, feeSatoshis: 0, network: BlazecoinNetwork.Regtest);

            // THE cross-check: the consensus implementation accepts our signature...
            var acceptedTxId = await rpc.CallAsync<string>("sendrawtransaction", signed.Hex);
            Assert.Equal(signed.TxId, acceptedTxId);

            // ...and mines it.
            await rpc.CallAsync<JsonElement>("generatetoaddress", 1, minerAddr);
            var minedTx = await rpc.CallAsync<JsonElement>("getrawtransaction", acceptedTxId, true);
            Assert.True(minedTx.GetProperty("confirmations").GetInt32() >= 1, "spend was not mined");

            // ── P2P broadcast leg: spend the CHANGE output, but deliver it over the raw
            // P2P protocol (magic handshake + unsolicited tx push) instead of RPC — the
            // total-backend-outage fallback, proven against the real node. ──
            var spendTx = await rpc.CallAsync<JsonElement>("getrawtransaction", acceptedTxId, true);
            int changeVout = -1;
            long changeSats = 0;
            foreach (var o in spendTx.GetProperty("vout").EnumerateArray())
            {
                var spk = o.GetProperty("scriptPubKey");
                if (spk.TryGetProperty("address", out var a) && a.GetString() == liteAddr)
                {
                    changeVout = o.GetProperty("n").GetInt32();
                    changeSats = (long)Math.Round(o.GetProperty("value").GetDecimal() * 100_000_000m);
                }
            }
            Assert.True(changeVout >= 0, "change output not found");

            var p2pSigned = LiteTransactionBuilder.BuildAndSign(
                new[] { new LiteUtxo(acceptedTxId, changeVout, changeSats, liteAddr) },
                minerAddr, 100_000_000, liteAddr,
                _ => liteKey, feeSatoshis: 0, network: BlazecoinNetwork.Regtest);

            var broadcaster = new BlazecoinWallet.Lite.Data.P2PBroadcaster(BlazecoinNetwork.Regtest);
            var acceptedBy = await broadcaster.TryBroadcastAsync(
                p2pSigned.Hex, new[] { "127.0.0.1:18444" }, TimeSpan.FromSeconds(15));
            Assert.Equal("127.0.0.1:18444", acceptedBy);

            // The node must now hold the P2P-delivered tx in its mempool — then mine it.
            var mempool = await rpc.CallAsync<List<string>>("getrawmempool");
            Assert.Contains(p2pSigned.TxId, mempool);
            await rpc.CallAsync<JsonElement>("generatetoaddress", 1, minerAddr);
            var p2pMined = await rpc.CallAsync<JsonElement>("getrawtransaction", p2pSigned.TxId, true);
            Assert.True(p2pMined.GetProperty("confirmations").GetInt32() >= 1, "P2P-delivered spend was not mined");

            // ── Trustless verification leg (C1/M3) against the REAL daemon: the funding tx
            // (acceptedTxId, our change output) must (a) hash to its txid — binding its true
            // value — and (b) carry a valid merkle+PoW inclusion proof. This exercises the
            // full LiteTxVerifier path (raw-tx value binding + MerkleBlock verification)
            // through real getrawtransaction / gettxoutproof, not a synthesised proof. ──
            var verifier = new BlazecoinWallet.Lite.Data.LiteTxVerifier(
                new RegtestChainReader(rpc), BlazecoinNetwork.Regtest);
            var verification = await verifier.VerifyAsync(acceptedTxId, changeVout, changeSats, requireInclusionProof: true);
            Assert.True(verification.Verified, verification.Error);
            Assert.True(verification.IncludedInBlock, "merkle/PoW inclusion proof did not verify against the real block");
            Assert.Equal(changeSats, verification.RealSatoshis); // the txid-committed real value

            // And a value-lie is caught: claim a wrong amount, the verifier returns the REAL one.
            var lied = await verifier.VerifyAsync(acceptedTxId, changeVout, changeSats + 999_999, requireInclusionProof: false);
            Assert.True(lied.Verified);
            Assert.Equal(changeSats, lied.RealSatoshis); // ignores the inflated claim
        }
        finally
        {
            try { daemon?.Kill(entireProcessTree: true); daemon?.WaitForExit(10_000); } catch { }
            try { Directory.Delete(datadir, recursive: true); } catch { }
        }
    }

    /// <summary>Minimal JSON-RPC client over the regtest cookie (waits for the daemon to be ready).</summary>
    private static async Task<CookieRpc> ConnectRpcAsync(string datadir)
    {
        var cookiePath = Path.Combine(datadir, "regtest", ".cookie");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(cookiePath))
            {
                var rpc = new CookieRpc(await File.ReadAllTextAsync(cookiePath));
                try { await rpc.CallAsync<int>("getblockcount"); return rpc; }
                catch { rpc.Dispose(); }
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("regtest daemon RPC did not come up within 30s");
    }

    private sealed class CookieRpc : IDisposable
    {
        private readonly HttpClient _http;

        public CookieRpc(string cookie)
        {
            _http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:18443/") };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(cookie.Trim())));
        }

        public async Task<T> CallAsync<T>(string method, params object[] args)
        {
            var payload = JsonSerializer.Serialize(new { jsonrpc = "1.0", id = "lite", method, @params = args });
            var res = await _http.PostAsync("", new StringContent(payload, Encoding.UTF8, "application/json"));
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException($"RPC {method} failed: {err.GetRawText()}");
            return JsonSerializer.Deserialize<T>(doc.RootElement.GetProperty("result").GetRawText())!;
        }

        public void Dispose() => _http.Dispose();
    }

    /// <summary>Adapts the regtest CookieRpc to the two IChainReader methods the verifier
    /// needs (raw tx hex + merkle proof), straight from the real daemon.</summary>
    private sealed class RegtestChainReader(CookieRpc rpc) : BlazecoinWallet.Lite.Data.IChainReader
    {
        public bool SupportsChainVerification => true;
        public async Task<string?> GetRawTransactionHexAsync(string txId, CancellationToken ct = default)
            => await rpc.CallAsync<string>("getrawtransaction", txId, false);
        public async Task<string?> GetTxOutProofAsync(string txId, CancellationToken ct = default)
            // gettxoutproof takes an ARRAY of txids as its first param — wrap so covariance
            // doesn't spread it into individual string args.
            => await rpc.CallAsync<string>("gettxoutproof", new object[] { new[] { txId } });

        public bool SupportsHistory => false;
        public Task<BlazecoinWallet.Lite.Data.LiteAddressSummary?> GetAddressAsync(string a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BlazecoinWallet.Lite.Data.LiteChainUtxo>> GetUtxosAsync(string a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BlazecoinWallet.Lite.Data.LiteHistoryEntry>> GetHistoryAsync(string a, int p = 1, int ps = 25, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
