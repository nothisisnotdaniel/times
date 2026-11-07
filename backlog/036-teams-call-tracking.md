# 036 Teams call tracking

As a Times user, I want calls and meetings in Teams booked as a Call checkpoint automatically, so that meeting time is tracked without me remembering to switch.

## What

Connect to the new Teams app's local third-party API (WebSocket on localhost only, built-in `ClientWebSocket`, no new packages), turned on by a command with an alias and paired once in Teams. The pairing token is stored in the data folder like other private data, never shown or logged. When a meeting or call starts, book a `Call` checkpoint (with the meeting title if Teams sends one, checked like typed names); when it ends, book the checkpoint that was open before again. If Teams is not running or the connection drops, show a grey status hint and retry quietly; never crash or block typing. Before the spec, check against a real Teams which messages it sends and whether 1:1 calls are reported, and write that into the spec. Takes the full path with high risk. Builds on background events in the loop.

## Why

Calls interrupt work at short notice and are often forgotten in the booking. Teams offers a local API for this, and a careful design (local only, private pairing token, quiet retries) makes it safe; that's why it takes the full path at high risk and needs a real Teams check first.
