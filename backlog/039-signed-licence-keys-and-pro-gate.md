# 039 Signed licence keys and Pro gate

As the maker of the suite, I want to hand out Suite Pro licence keys myself and have both apps check them offline, so that I can sell Pro now without a payment provider and plug one in later without breaking any key.

## What

A licence key is a short text token (for example `CTX1-…`, base64url): a versioned payload (format version, key id of the signing key, licence id, licensee name, edition `suite-pro`, updates-until date, issue date) plus an ECDSA P-256 signature over it (built into .NET, no new package). Times and Context ship the public key(s) by key id; nothing secret is in either app or repo.

- **Issue (developer only, never shipped):** a small tool in this repo (its own project, left out of the release and the .NET tool) `issue --name <name> --until <yyyy-MM-dd>` signs a key with the private key read from the developer's OS keychain (a `create-key` command makes it there once and prints the public key to paste into the code), prints the key and appends licence id, name, dates to a ledger file outside the repo. It refuses to run without the keychain key; tests use a test key pair.
- **Activate:** `times pro activate <key>` verifies the signature offline (no network), refuses a bad, unknown-key-id or revoked key with one clear line, then saves the key in the OS keychain and licence id, licensee and updates-until in a shared activation file (in a suite folder both apps read; `0600`). `times pro status` shows licensee and updates-until; `times pro deactivate` removes both.
- **Gate:** one `Pro.IsUnlocked` check: a valid activated key whose updates-until is on or after the app's build date, so a key keeps every version released while it was valid. Without one, a Pro command says in one line what Pro is and how to get it.
- **Revocation:** a list of revoked licence ids compiled into the app (empty for now), so a leaked key stops working in later releases.
- **Later providers:** the key id and format version let a provider's own keys (chosen by Context#399) be added next to these as another kind; manually issued keys keep working.

Full path, high risk: crypto, secrets and a shared data format. Context#108 uses the same format and file.

## Why

Lemon Squeezy is closing, so the provider is open (Context#399). Signed offline keys need no server, no running cost and no provider, work on a plane, and let you sell Pro by hand (invoice, then send a key) from day one.

## Notes

Rewritten on 2026-11-19: was Lemon Squeezy's licence API with a monthly online re-check; the user asked for keys they can hand out manually, with a provider added later.
