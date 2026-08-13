# Kahuna.Storage.Gcs

Google Cloud Storage backup target for [Kahuna](https://github.com/kahunakv/kahuna). It stores
point-in-time-recovery manifests and backup artifacts — checkpoint trees and per-partition WAL
segments — in a GCS bucket instead of a directory on the node.

This ships as a separate package on purpose. `Kahuna.Core` is embedded directly by other projects, so
a cloud SDK referenced there would be inherited by every embedded consumer. The backup storage
contracts are an extension point, and this is an implementation of them.

## Requirements

- .NET 10
- `Kahuna.Core` 1.0.7 or later — earlier versions do not expose the artifact storage contracts

## Installing it in a host

The provider is built once at startup, and building it verifies the bucket. Assign it to the backup
storage provider and select the target by name.

```csharp
using Kahuna.Storage.Gcs;

BackupStorageProvider provider = await GcsBackupStorage.CreateProviderAsync(
    new GcsBackupStorageOptions
    {
        Bucket = "acme-kahuna-backups",
        Prefix = "prod/"
    },
    logger);

configuration.BackupTarget = GcsBackupStorage.TargetName;   // "gcs"
configuration.BackupDir = "/var/lib/kahuna/backups";        // still required
configuration.BackupScratchDir = "/var/lib/kahuna/scratch"; // mandatory for a bucket target
configuration.BackupStorageProvider = provider;
```

For an embedded host the same three properties exist on `EmbeddedKahunaOptions`.

On the server, the matching command-line options are `--pitr-backup-target gcs`,
`--pitr-backup-dir`, and `--pitr-backup-scratch-dir`. Kahuna refuses to start when a non-local target
has no provider registered or no scratch directory configured, so a misconfiguration fails at startup
rather than at the first scheduled backup.

## Authentication

Application Default Credentials is the intended path and the default: workload identity on GKE, the
metadata server on GCE, and `gcloud` user credentials on a developer machine. Nothing needs to be
configured for it.

`CredentialsPath` is a fallback for environments where ADC cannot be arranged. It accepts a
service-account key file and **only** a service-account key file — a credentials file can also
describe an external account, which tells the client to fetch tokens from a URL named inside the
file, so a file swapped underneath a node could redirect authentication. Narrowing what is accepted
closes that off. Prefer workload identity wherever it exists; a key file is a long-lived secret on
local disk.

The identity needs, on the bucket:

| Permission | Used for |
| --- | --- |
| `storage.buckets.get` | the startup bucket check |
| `storage.objects.create` | publishing manifests and artifacts |
| `storage.objects.get` | reading manifests, verification, restore |
| `storage.objects.list` | listings, orphan sweep, prefix delete |
| `storage.objects.delete` | retention and garbage collection |

`roles/storage.objectAdmin` covers the object permissions but **not** `storage.buckets.get`; pair it
with `roles/storage.legacyBucketReader`, or grant a custom role with all five.

## Bucket requirements

Two settings are verified at startup and the node refuses to start without them. Both are the
bucket-scale equivalent of a guarantee the local target already makes — it restricts a manifest to
owner-only *before* publishing it, so a manifest, which names every artifact and its digest, is never
briefly readable by anyone else.

- **Uniform bucket-level access.** With object ACLs still active an object can carry access grants
  the bucket policy does not describe.
- **Public access prevention (enforced).** Backup artifacts are a complete copy of the cluster's
  committed state, so a bucket that can be made public by a later IAM edit is one edit away from
  publishing it.

Either can be waived with `RequireUniformBucketLevelAccess` / `RequirePublicAccessPrevention`, which
makes accepting the risk a deliberate act rather than an oversight.

Kahuna never creates the bucket. Its location, storage class, and access policy are deployment
decisions, and a silently created bucket would have none of them made.

### Settings that quietly defeat reclamation

Object versioning, a soft-delete policy, and a retention policy each change what deletion means: a
deleted artifact stays recoverable and billable. Retention and garbage collection then run, delete
successfully, and reclaim nothing.

This is reported as a warning by default rather than refused, because GCS enables soft delete on new
buckets by default and refusing outright would reject most buckets an operator creates. Set
`DeletionPolicyReaction` to `Fail` where storage cost or an erasure obligation depends on deletes
taking effect, or to `Ignore` where the behaviour has been established some other way.

## Local scratch is mandatory

A persistence backend writes a checkpoint through the filesystem and can target nothing else, so a
bucket can never be the direct destination. A full backup is checkpointed into the scratch directory,
uploaded, and the scratch tree dropped. Restore is the mirror image, and reuses the staging area
Kahuna already had.

**Size the scratch volume for one whole full backup**, which transits it in its entirety. Kahuna
hardens the directory itself (owner-only, and it refuses a symlinked or group/world-writable root) —
backup bytes sit there in cleartext while in transit.

## Layout in the bucket

```
{prefix}{backupId:N}.manifest      the manifest — publishing it is what makes a backup exist
{prefix}{backupId:N}/checkpoint/…  full-backup checkpoint tree, relative paths preserved exactly
{prefix}{backupId:N}/partition_N.wal   incremental WAL segments
```

Manifests and artifacts share one prefix, exactly as they share one directory on the local target, so
keys are classified by name. Several clusters can share a bucket under different prefixes; a
neighbour's keys are invisible to this one.

## Configuration reference

| Option | Default | Meaning |
| --- | --- | --- |
| `Bucket` | *(required)* | Bucket holding manifests and artifacts |
| `Prefix` | `""` | Key prefix; empty is the bucket root |
| `CredentialsPath` | `null` | Service-account key file; null uses Application Default Credentials |
| `ResumableUploadThresholdBytes` | 8 MiB | Above this an artifact streams instead of being buffered |
| `ResumableChunkSizeBytes` | 8 MiB | Chunk size for artifacts that stream; rounded up to 256 KiB |
| `RequireUniformBucketLevelAccess` | `true` | Refuse to start without it |
| `RequirePublicAccessPrevention` | `true` | Refuse to start without it |
| `DeletionPolicyReaction` | `Warn` | Reaction to versioning / soft delete / retention policy |
| `FailOnManifestIdCollision` | `true` | Publish manifests create-if-absent |
| `EmulatorBaseUri` | `null` | Point at a GCS-compatible endpoint for testing |

Every upload uses Google's resumable protocol — the .NET client has no single-request upload path —
so `ResumableUploadThresholdBytes` does not select a protocol. It selects memory: below it an
artifact is buffered and handed over whole, above it the bytes stream and are never resident.

## Behaviour worth knowing

- **Nothing is published until it is complete.** An upload creates no object until its final chunk is
  committed, so an abandoned write leaves nothing behind and there is no partial-object debris class
  to sweep. An abandoned resumable session expires server-side.
- **Manifest publish is create-if-absent.** A backup id is a fresh GUID, so a collision means two
  writers share a prefix or a bucket was restored over a live one — and the manifest already there is
  the thing least worth destroying. This is stricter than the local target, which can only overwrite.
  Turn it off with `FailOnManifestIdCollision` if a deployment needs to rewrite a manifest in place.
- **Deletes converge rather than succeeding once.** There is no atomic prefix delete, so reclaiming a
  backup is a listing plus per-object deletes that can fail part-way. Re-running finishes the job.
- **Listings fail closed.** A listing failure propagates instead of being reported as an empty
  result. "No manifests" means "no backup owns anything", which would authorise the orphan sweep to
  reclaim every artifact in the bucket, so a transient outage must never be able to say it.
- **POSIX hardening is declared unsupported, not faked.** The store reports
  `SupportsPosixHardening: false`; the bucket-level protections above are what stands in for it.

## Tests

```sh
docker compose up -d
dotnet test
```

Without the emulator the suite still runs and the tests that need it are skipped rather than failed.

What the emulator does and does not prove is worth being precise about, and each of these was
checked against the image rather than assumed:

- It **does** implement listing with page tokens, range reads, resumable uploads, and per-object
  deletes.
- It **does not** enforce generation preconditions — a create-if-absent write over an existing object
  is accepted and overwrites it, where the real service answers 412. Manifest-collision behaviour is
  therefore pinned against a scripted transport, which verifies the request the adapter sends and what
  it makes of a 412 response.
- It **models no access control at all**. Credentials, IAM, and bucket policy are unauthenticated
  no-ops, so the bucket-hardening checks are switched off in the emulator tests and exercised
  separately against buckets that report no uniform access.

### Unverified

- **ADC / workload identity, IAM, and bucket policy.** Nothing here authenticates, so none of it is
  exercised until the adapter runs against a real bucket.
- **Real-service behaviour of generation preconditions.** The request and the 412 handling are
  verified; that GCS answers 412 is taken from its documented contract.
- **Multi-gigabyte artifacts.** The streaming path is verified byte-for-byte at a few megabytes, not
  at the scale a production checkpoint reaches.
- **Whether a shared bucket tolerates multiple writers.** Single-writer ownership is assumed, as it is
  on the local target.
- **An end-to-end backup and restore through `BackupService`.** Every contract member is exercised
  against a real storage client here, but the backup driver, verifier, restore engine, and retention
  passes are internal to `Kahuna.Core` and cannot be driven from this package. Kahuna's own suite
  covers them against an object-shaped store; a run against this one belongs in that repo.

## Licence

MIT
