# ForkAnnouncementSigner

The operator's side of the lite wallets' fork-announcement channel. The wallet's side, and the
message format both share, is `BlazecoinWallet.Lite.Core/Fork/ForkAnnouncement.cs`.

## Why it exists

A lite wallet asks its gateway whether a fork is coming. A build too old to follow that fork
stops sending, so nobody spends on the wrong chain. The gateway is not trusted, so the wallet
stops sends only for an announcement signed by a key pinned in the build
(`ForkAnnouncement.PinnedKeys`). An unsigned announcement is a warning only.

## The keys

Two keys are pinned, created 2026-11-12. Either one authenticates an announcement.

| Key | Public key | Use |
|---|---|---|
| 1, primary | `028231e51223f74606b9545305bad166d498c358f46c9e9bd204146e87eae55efb` | The one to sign with |
| 2, spare | `03d81f8b4006c1f897ba1af3ae6c381cc432daddd8ead8036a21237c8c69e992b7` | Kept apart from key 1; used only if key 1 is lost |

The private keys are in no repository and must never be. Each is a file of 64 hex characters.
Keep the two in different places.

A build trusts only the keys it shipped with. The first lite release to carry these keys is the
first one that can be stopped; anything older shows a warning at most.

## Commands

Run from this folder. A private key is never typed or printed: it is read from a file.

    dotnet run -c Release -- pinned
    dotnet run -c Release -- pubkey --key-file <file>
    dotnet run -c Release -- sign --key-file <file> --name <fork> --height <n> --min-version <x.y.z>
    dotnet run -c Release -- verify --name <fork> --height <n> --min-version <x.y.z> --signature <hex>
    dotnet run -c Release -- newkey --out <file>

`sign` exits 0 only when this build's pinned keys accept the signature.

## Announcing a fork

1. Ship the lite release that follows the new fork (its name added to `LiteClientInfo.SupportedForks`).
2. Bring the key file to this machine. Run `sign` with the fork's name, activation height and
   the first wallet version that follows it. Check it says `accepted ... YES`.
3. Set these on BOTH gateways (`Fork:Name`, `Fork:Height`, `Fork:MinClientVersion`,
   `Fork:Signature`) and restart them.
4. Run `verify` with the values the gateway now serves in its status response.
5. Remove the key file from this machine.

The signature covers the name, the height and the version. Change any of the three and it must
be signed again. A fork height at or below a build's shipped checkpoint is ignored by that
build, signed or not.

## If a key is lost or exposed

- Lost: sign with the spare. Create a new key with `newkey`, pin it in place of the lost one,
  and ship a release.
- Exposed: whoever holds it can stop sends in every installed build that pins it, until those
  builds are replaced. It cannot move coins or read a wallet. Remove the key from `PinnedKeys`
  and ship a release at once.
