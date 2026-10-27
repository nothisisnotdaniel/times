# Times: open questions

Decisions the factory would otherwise guess at. When one is settled, move the answer into
`CLAUDE.md` (or into a backlog item) and delete it here. Agents don't read this file.

## Domain

1. **Checkpoints.** What is a checkpoint? Is it a timestamp plus a note? Does it belong to a
   project or an Azure DevOps work item ID? Can it span a time range?
2. **Projects.** Does a check-in belong to a project? Can you switch projects without checking out?
   Where does the list of projects come from: typed in freely, a config file, or Azure DevOps?
3. **Rules.**
   - Do times round, for example to 5 or 15 minutes?
   - Are breaks deducted from worked time? Is a break a start/stop pair or a duration (`/break 15m`)?
   - What happens if the user checks in twice, or checks out without checking in?
   - What happens with a day where the user forgot to check out? Ask on the next start, or auto-close?
   - Can the user edit or delete entries afterwards?

## Azure DevOps transfer

4. **Mechanism.** Is it an export file the user uploads, or the app calling the Azure DevOps REST API?
5. **Auth (API only).** A personal access token from an environment variable, never written to disk?
   Which organisation and project, and where are they configured?
6. **What gets written.** A new work item per checkpoint, a comment, or the "Completed Work" hours
   on an existing item? Which work item type?
7. **Tracking.** How is a checkpoint marked as transferred, so it isn't sent twice?

## UI and commands

10. **Day view.** What should the screen above the input box show for a day: a timeline, a
    table of entries, totals?
11. **Day navigation.** Which commands move between days: `/prev`, `/next` and `/day 2026-10-27`?
    Keyboard shortcuts as well?

## Data

13. **Format versioning.** Should each day file carry a `"version"` field, so the format can change later?
14. **Time zones.** Should times be stored in local time with an offset, or in UTC?
15. **Sync.** Is the app-data folder only ever on one machine, or could it be synced (OneDrive and
    so on), which would mean handling conflicts?
