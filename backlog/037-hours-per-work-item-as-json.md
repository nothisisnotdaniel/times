# 037 Hours per work item as JSON

As a developer using Times with other tools, I want my booked hours per work item as JSON, so that Context and scripts can show where my time went.

## What

`times report --from <date> --to <date> --json` prints one JSON document: per day the arrival, leaving and breaks, and per work item (the `#1234` or `AB#1234` in a checkpoint) the booked minutes and the checkpoint texts. Without `--json` it prints a short plain table. Stable camelCase property names, documented in the README. Full path.

## Why

The work item dossier, the handover pack and the Claude Code MCP server in Context all need Times' hours. A stable JSON report is the one clean way in, without Context reading Times' files.
