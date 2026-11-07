# 039 Licence key and Pro gate

As a buyer of Suite Pro, I want to activate my licence key once and then use Pro offline, so that paying is simple and Pro never stops working on a plane.

## What

`times pro activate <key>` calls Lemon Squeezy's public licence API once (no API secret in the app) and saves key, machine id and expiry date: the key in the OS keychain, the rest in a shared activation file that Context reads too. `times pro status` shows the state. A Pro feature is unlocked when the app's build date is before the key's expiry date, so expired keys keep every version released while they were valid. A silent online re-check at most once a month, with a 30-day grace when it fails. `times pro deactivate` frees the machine slot. Without a key, Pro commands say in one line what Pro is and where to buy it. Full path, high risk: secrets, network and a shared data format.

## Why

Suite Pro (€39 once, one year of updates) needs one licence check that both apps share. Lemon Squeezy's licence API means no server of our own and no running cost, and an offline-first check keeps it friendly for honest users. See the Suite Pro sales plan.
