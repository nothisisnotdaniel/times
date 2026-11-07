# 011 Calendar to checkpoints

As a Times user, I want today's meetings from my calendar offered as checkpoints, so that I can book meeting time without typing each one.

## What

Read today's appointments through Context's CLI (`context current --json`) and offer each meeting as a checkpoint at its start time (title checked like typed names; `#1234` in the title picks the project). Nothing is booked without the user choosing it. If Context isn't installed, the command says so. Builds on Context's JSON output for the CLI.

## Why

Meetings are a big share of a workday and are already in the calendar. Offering them (never booking on its own) saves typing while keeping me in control of what gets booked.
