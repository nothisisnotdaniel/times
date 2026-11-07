# 010 Checkpoint opens Context note

As a Times user, I want booking a checkpoint with a work item like `#1234` to open the matching Context note, so that my notes for that work are at hand the moment I start it.

## What

When a checkpoint with a work item (`#1234`) is booked, open the Context note tagged `#1234` in the Context desktop app, creating it if there's none (through Context's CLI, never by writing Context's files). A setting turns it on; if Context isn't installed, nothing happens. Builds on the settings dialog and Context's scriptable CLI.

## Why

Switching to a work item usually means looking up its notes as well. Going through Context's CLI keeps Times out of Context's files, and the setting keeps it optional for people without Context.
