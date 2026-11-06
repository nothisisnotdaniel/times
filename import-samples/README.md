# Old time data

Example files written by the old time tracking tool. They are the format reference for the
import command and serve as its test fixtures. The checkpoint titles are made up; times, types,
booked flags and durations are real.

## Files

- One file per day, named `yyyy-MM-dd.json` (for example `2026-10-28.json`). The date comes
  from the file name only.
- UTF-8 JSON, written on one line by System.Text.Json with its default escaping (`&` is
  `&`, `ü` is `ü`).
- The only top-level key is `Recordings`: an array of entries in the order they were booked.

## Entries

| Field | Meaning |
|---|---|
| `Time` | Local wall-clock time, `HH:mm:ss` (seconds are always `00` in practice). |
| `Type` | `0` arrival, `1` leaving, `2` break start, `3` break end, `4` checkpoint. No other values occur; any other value is an error. |
| `Description` | The checkpoint title; `null` for every other type. Contains no `#1234` work item numbers. |
| `Booked` | `true` if the checkpoint was booked, otherwise `null`. Maps to Times' booked flag. |
| `UsedTime` | `HH:mm:ss`, only on checkpoints: the time from this checkpoint to the next entry of any type. Derived, not needed to import; the import compares it with the entry times and warns on a mismatch. |

## Things the sample shows

- Two entries can share a time (arrival and the first checkpoint at 07:22, break end and the
  next checkpoint at 12:15). Keep the file order.
- A day can have more than one arrival and leaving: the sample leaves at 16:56, arrives again
  at 19:06 and leaves at 19:52.
- A checkpoint before a break runs until the break start (11:45 checkpoint, `UsedTime` 22 min,
  break at 12:07).
- The same title appears many times in a day (`Datenpumpe`).
