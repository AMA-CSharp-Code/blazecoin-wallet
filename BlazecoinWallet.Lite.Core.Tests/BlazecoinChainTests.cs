namespace BlazecoinWallet.Lite.Tests;

using BlazecoinWallet.Lite;

/// <summary>
/// Pins the lite wallet's chain constants to the daemon's consensus values
/// (Blazecoin_Wallet_V2_Core src/kernel/chainparams.cpp, CMainParams). The lite wallet
/// signs transactions client-side, so a drifted prefix would produce addresses or keys
/// for the wrong network — these must never change without a matching daemon change.
/// </summary>
public class BlazecoinChainTests
{
    [Fact]
    public void Address_prefixes_match_mainnet_chainparams()
    {
        Assert.Equal(26, BlazecoinChain.PubKeyAddressPrefix);   // 'B…' addresses
        Assert.Equal(5, BlazecoinChain.ScriptAddressPrefix);
        Assert.Equal(154, BlazecoinChain.SecretKeyPrefix);      // 128 + 26
    }

    [Fact]
    public void Bip32_versions_are_bitcoin_standard()
    {
        Assert.Equal(new byte[] { 0x04, 0x88, 0xB2, 0x1E }, BlazecoinChain.ExtPublicKeyPrefix);
        Assert.Equal(new byte[] { 0x04, 0x88, 0xAD, 0xE4 }, BlazecoinChain.ExtSecretKeyPrefix);
    }

    [Fact]
    public void Network_magic_and_money_supply_match_consensus()
    {
        Assert.Equal(new byte[] { 0xFB, 0xC0, 0xB6, 0xDB }, BlazecoinChain.MessageMagic);
        Assert.Equal(55414, BlazecoinChain.P2PPort);
        Assert.Equal(30, BlazecoinChain.BlockSpacingSeconds);
        Assert.Equal(206_500_000_000_000_000L, BlazecoinChain.MaxMoney);
        Assert.False(BlazecoinChain.SegwitEnabled); // legacy-P2PKH only, by decision
    }
}
