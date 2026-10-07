---
name: offline-storage
description: >-
  Design offline-first local storage and sync for a mobile app — what is cached, how a
  mutation queue drains, and the conflict-resolution rule. Use when a mobile feature must
  work without a network connection.
x-generated-from: skills/mobile/offline-storage/SKILL.md
---

# Offline Storage

## When

Any mobile feature that must function without connectivity, or that must not lose a
user action taken while offline.

## Decisions to make first

1. **What is cached** — the read model the screen needs, and its freshness policy
   (TTL, on-focus refresh, manual pull).
2. **Mutation queue** — offline writes go to a durable local queue with a stable client id
   per operation. On reconnect, the queue drains in order.
3. **Idempotency** — every queued mutation carries a client-generated key so a retry after
   a partial failure does not double-apply. The server contract must accept it
   (`skills/workflow/create-contract`).
4. **Conflict-resolution rule** — decide explicitly, per entity:
   - last-write-wins (record `updated_at`), or
   - server-wins with a surfaced "your change was rejected", or
   - field-level merge (only for genuinely independent fields), or
   - user-resolved (present both versions).
   A feature without one of these is not done.

## Steps

1. Model the local store (SQLite / MMKV / AsyncStorage — per stack) for the read model
   and the mutation queue.
2. Implement the queue: enqueue on mutation, drain on connectivity regained, mark each
   item `pending | syncing | done | failed`.
3. Implement sync: pull latest, apply the conflict rule, then push the queue.
4. Surface state to the UI: offline badge, pending count, per-item failure.

## Validation

- Kill the network mid-flow: the action is queued, the UI reflects "pending", and it
  syncs on reconnect exactly once.
- Two devices edit the same record offline: the conflict rule resolves it predictably and
  the loser is informed.
- App killed with a non-empty queue: the queue survives a restart.

## Escalation

Offline sync is `reasoning` tier and touches concurrency. If the conflict rule needs a
product decision (which side wins, what the user sees), that is a human gate
(`workflow/human-gates.md`).
