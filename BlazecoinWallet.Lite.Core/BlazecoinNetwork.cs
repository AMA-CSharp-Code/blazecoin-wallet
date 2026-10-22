using NBitcoin;

namespace BlazecoinWallet.Lite;

/// <summary>
/// The Blazecoin mainnet as an NBitcoin <see cref="Network"/>, so the lite wallet can use
/// NBitcoin's audited key/address/signing machinery instead of hand-rolled crypto. All
/// values mirror the daemon's consensus source (Blazecoin_Wallet_V2_Core
/// src/kernel/chainparams.cpp, CMainParams) and are double-pinned by
/// <c>BlazecoinChain</c> + the test suite; the embedded genesis block is the REAL mainnet
/// genesis (fetched from a live daemon) so <c>Instance.GetGenesis()</c> cross-checks the
/// whole definition against the chain.
/// </summary>
public static class BlazecoinNetwork
{
    /// <summary>Raw mainnet genesis block (getblock &lt;hash&gt; 0 from the live daemon).</summary>
    private const string GenesisHex =
        "010000000000000000000000000000000000000000000000000000000000000000000000b8189634c52b533ca815d058eb4fc8ae" +
        "d4b3621058e718853813eb0646c1da76d00e7953f0ff0f1ea51409000101000000010000000000000000000000000000000000000" +
        "000000000000000000000000000ffffffff5e04ffff001d01044c55486177616969616e2053757266696e67204d6f64656c204368" +
        "6172676564205769746820417474656d70746564204d757264657220696e20416c6c6567656420526f616420526167652048697420" +
        "616e642052756effffffff0100f2052a010000004341040184710fa689ad5023690c80f3a49c8f13f8d45b8c857fbcbc8bc4a8e4d3" +
        "eb4b10f4d4604fa08dce601aaf0f470216fe1b51850b4acf21b179c45070ac7b03a9ac00000000";

    /// <summary>Mainnet genesis block id (chainparams.cpp assert; block ids are SHA256d, not scrypt).</summary>
    public const string GenesisHash = "5d871c1b6ea542c2bb8a3b3ac70028a591bbf81369e90c2446c1a2bbfb89459b";

    /// <summary>
    /// BIP44 coin type for derivation paths (m/44'/413'/…). Blazecoin has no SLIP-44
    /// registration; 413 is chosen for the block reward — a wallet-level convention, not
    /// consensus. Never change once wallets ship: restores depend on it.
    /// </summary>
    public const int Bip44CoinType = 413;

    /// <summary>Regtest genesis block id (fetched from `blazecoind -regtest`).</summary>
    public const string RegtestGenesisHash = "d062f3721f1261bf3542870d0399e67432554efb448bb6046a576128467a0062";

    /// <summary>Raw regtest genesis block (getblock &lt;hash&gt; 0 from `blazecoind -regtest`).</summary>
    private const string RegtestGenesisHex =
        "010000000000000000000000000000000000000000000000000000000000000000000000b8189634c52b533ca815d058eb4fc8ae" +
        "d4b3621058e718853813eb0646c1da76dae5494dffff7f200000000001" +
        "01000000010000000000000000000000000000000000000000000000000000000000000000ffffffff5e04ffff001d01044c5548" +
        "6177616969616e2053757266696e67204d6f64656c2043686172676564205769746820417474656d70746564204d757264657220" +
        "696e20416c6c6567656420526f616420526167652048697420616e642052756effffffff0100f2052a010000004341040184710f" +
        "a689ad5023690c80f3a49c8f13f8d45b8c857fbcbc8bc4a8e4d3eb4b10f4d4604fa08dce601aaf0f470216fe1b51850b4acf21b1" +
        "79c45070ac7b03a9ac00000000";

    private static readonly Lazy<Network> _instance = new(BuildMainnet);
    private static readonly Lazy<Network> _regtest = new(BuildRegtest);

    /// <summary>The registered Blazecoin mainnet network.</summary>
    public static Network Instance => _instance.Value;

    /// <summary>
    /// The registered Blazecoin regtest network (chainparams CRegTestParams: prefixes
    /// 111/196/239, magic fa bf b5 da, port 18444). Used ONLY by the daemon cross-check
    /// harness — production wallets are mainnet.
    /// </summary>
    public static Network Regtest => _regtest.Value;

    /// <summary>
    /// NBitcoin's network grouping (mainnet/testnet/regtest per coin). Blazecoin has no
    /// live testnet, so that accessor throws rather than hand back a network that doesn't
    /// exist; regtest exists for the daemon cross-check harness.
    /// </summary>
    private sealed class BlazecoinNetworkSet : INetworkSet
    {
        public static readonly BlazecoinNetworkSet SetInstance = new();
        public string CryptoCode => "BLZ";
        public Network Mainnet => Instance;
        public Network Testnet => throw new NotSupportedException("Blazecoin has no live testnet network defined here.");
        public Network Regtest => BlazecoinNetwork.Regtest;
        public Network GetNetwork(ChainName chainName)
        {
            if (chainName == ChainName.Mainnet) return Instance;
            if (chainName == ChainName.Regtest) return BlazecoinNetwork.Regtest;
            throw new NotSupportedException($"Blazecoin network for '{chainName}' is not defined.");
        }
    }

    private static Network BuildMainnet()
    {
        // Already registered (e.g. parallel test hosts) — reuse rather than throw.
        var existing = Network.GetNetwork("blazecoin-main");
        if (existing != null) return existing;

        var builder = new NetworkBuilder()
            .SetName("blazecoin-main")
            .AddAlias("blazecoin-mainnet")
            .SetNetworkSet(BlazecoinNetworkSet.SetInstance)
            .SetChainName(ChainName.Mainnet)
            // Wire magic fb c0 b6 db is written little-endian from this uint.
            .SetMagic(0xDBB6C0FB)
            .SetPort(BlazecoinChain.P2PPort)
            .SetRPCPort(55413)
            .SetConsensus(new Consensus
            {
                SubsidyHalvingInterval = 1_051_200,
                PowTargetSpacing = TimeSpan.FromSeconds(BlazecoinChain.BlockSpacingSeconds),
                PowTargetTimespan = TimeSpan.FromHours(1),       // 120-block retarget window
                CoinbaseMaturity = 30,
                SupportSegwit = false,                            // legacy-only chain
                SupportTaproot = false,
                PowAllowMinDifficultyBlocks = false,
                PowNoRetargeting = false,
                PowLimit = new Target(new uint256("00000fffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")),
                ConsensusFactory = new ConsensusFactory(),
            })
            .SetBase58Bytes(Base58Type.PUBKEY_ADDRESS, new[] { BlazecoinChain.PubKeyAddressPrefix })
            .SetBase58Bytes(Base58Type.SCRIPT_ADDRESS, new[] { BlazecoinChain.ScriptAddressPrefix })
            .SetBase58Bytes(Base58Type.SECRET_KEY, new[] { BlazecoinChain.SecretKeyPrefix })
            .SetBase58Bytes(Base58Type.EXT_PUBLIC_KEY, BlazecoinChain.ExtPublicKeyPrefix)
            .SetBase58Bytes(Base58Type.EXT_SECRET_KEY, BlazecoinChain.ExtSecretKeyPrefix)
            .SetGenesis(GenesisHex);

        return builder.BuildAndRegister();
    }

    private static Network BuildRegtest()
    {
        var existing = Network.GetNetwork("blazecoin-reg");
        if (existing != null) return existing;

        var builder = new NetworkBuilder()
            .SetName("blazecoin-reg")
            .SetNetworkSet(BlazecoinNetworkSet.SetInstance)
            .SetChainName(ChainName.Regtest)
            .SetMagic(0xDAB5BFFA) // wire fa bf b5 da (chainparams CRegTestParams)
            .SetPort(18444)
            .SetRPCPort(18443)
            .SetConsensus(new Consensus
            {
                SubsidyHalvingInterval = 150,
                PowTargetSpacing = TimeSpan.FromSeconds(BlazecoinChain.BlockSpacingSeconds),
                PowTargetTimespan = TimeSpan.FromHours(1),
                CoinbaseMaturity = 30,
                SupportSegwit = false,
                SupportTaproot = false,
                PowAllowMinDifficultyBlocks = true,
                PowNoRetargeting = true,
                PowLimit = new Target(new uint256("7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")),
                ConsensusFactory = new ConsensusFactory(),
            })
            .SetBase58Bytes(Base58Type.PUBKEY_ADDRESS, new byte[] { 111 })
            .SetBase58Bytes(Base58Type.SCRIPT_ADDRESS, new byte[] { 196 })
            .SetBase58Bytes(Base58Type.SECRET_KEY, new byte[] { 239 })
            .SetBase58Bytes(Base58Type.EXT_PUBLIC_KEY, new byte[] { 0x04, 0x35, 0x87, 0xCF })
            .SetBase58Bytes(Base58Type.EXT_SECRET_KEY, new byte[] { 0x04, 0x35, 0x83, 0x94 })
            .SetGenesis(RegtestGenesisHex);

        return builder.BuildAndRegister();
    }
}
