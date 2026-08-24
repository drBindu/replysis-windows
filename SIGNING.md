# Signing Replysis for Windows

Releases build unsigned today. Everything needed to sign them is already in
`.github/workflows/release-windows.yml` — adding the secrets below is the whole
change; no code or workflow edits.

---

## What signing does and does not buy

It does **not** remove the SmartScreen prompt on a new release. Nothing does, for
a direct download. Microsoft removed the automatic reputation grant for EV
certificates in 2024, so every certificate — Azure, OV, EV — now earns trust the
same way: by accumulating clean downloads. There is no documented escalation
path and no way to buy past it.

What it buys is that the prompt **ends**:

| | First install | After reputation accrues |
|---|---|---|
| Unsigned | warns | **still warns, forever** |
| Signed | warns | **stops** |

Reputation attaches to a certificate. With no certificate there is nothing for it
to attach to, so an unsigned build warns on its ten-thousandth download exactly as
it did on its first. The prompt also names the publisher once signed — *Varoxel
LLC* rather than *Unknown publisher*, which is a different proposition for
somebody about to pay.

The Microsoft Store is the only channel with no prompt at all; it does not use
SmartScreen reputation. Direct download stays worth having because a fix ships in
minutes rather than waiting on Store review.

---

## Recommended: Azure Trusted Signing

Roughly $10/month, run by Microsoft. Since no certificate skips reputation, the
$300–600/year options buy nothing SmartScreen recognises.

It issues **no PFX file** — which is why the older PFX-only workflow could not
have accepted it, and why this was wired up before buying rather than after.

**Setup**

1. Create a Trusted Signing account in Azure and verify **Varoxel LLC**
   (identity verification takes a few days — start it first).
2. Create a certificate profile under that account.
3. Create a service principal (app registration) and grant it the
   **Trusted Signing Certificate Profile Signer** role on the account.
4. Add these repository secrets — Settings → Secrets and variables → Actions:

| Secret | Value |
|---|---|
| `AZURE_SIGN_ENDPOINT` | Region endpoint, e.g. `https://eus.codesigning.azure.net/` |
| `AZURE_SIGN_ACCOUNT` | Trusted Signing account name |
| `AZURE_SIGN_PROFILE` | Certificate profile name |
| `AZURE_TENANT_ID` | Service principal tenant |
| `AZURE_CLIENT_ID` | Service principal application id |
| `AZURE_CLIENT_SECRET` | Service principal secret |

That is all. The next release signs itself. The workflow builds the metadata
file and passes `--azureTrustedSignFile` to `vpk pack`.

---

## Alternative: a PFX certificate

Still supported, and used automatically if the Azure secrets are absent:

| Secret | Value |
|---|---|
| `WINDOWS_CERT_PFX` | The `.pfx`, base64-encoded |
| `WINDOWS_CERT_PASSWORD` | Its password |

Encode with:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx")) | Set-Clipboard
```

---

## How the workflow decides

In order, first match wins:

1. `AZURE_SIGN_ACCOUNT` present → Azure Trusted Signing
2. `WINDOWS_CERT_PFX` present → PFX via signtool
3. neither → **unsigned**, and the build log says so plainly

Every release prints the installer's real signature status at the end, so an
unsigned build cannot be mistaken for a signed one — the `Import the signing
certificate` step used to report success whether or not a certificate existed,
which is exactly how an unsigned build ships without anyone noticing.

---

## Two things that will cost you if unknown

**Use one certificate for every release.** Reputation is per-certificate.
Switching CAs, or letting one lapse, resets it to zero and the prompts return.

**Renewal resets it too.** A renewed certificate has a new thumbprint and starts
from nothing, even with an identical publisher. Expect prompts to reappear
briefly after a renewal, and do not renew immediately before a launch.

---

## Checking what actually shipped

```powershell
Get-AuthenticodeSignature .\Replysis-win-Setup.exe | Format-List Status, SignerCertificate
```

`NotSigned` means unsigned. `Valid` with a subject naming Varoxel LLC means the
certificate was applied.
