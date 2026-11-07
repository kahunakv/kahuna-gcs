# Installation

This page tells you how to connect the GCS backup target to a Kahuna host. For the terms, see the
[glossary](glossary.md).

## Before you start

1. Create the bucket. Kahuna never creates it. See [Bucket requirements](bucket-requirements.md).
2. Give the host identity the necessary permissions. See [Authentication](authentication.md).
3. Make a local scratch directory. See [Scratch directory](scratch-directory.md).

## Add the package

```sh
dotnet add package Kahuna.Storage.Gcs
```

## Create the provider

Create the provider one time, at startup. `CreateProviderAsync` checks the bucket settings before it
returns.

```csharp
using Kahuna.Storage.Gcs;

BackupStorageProvider provider = await GcsBackupStorage.CreateProviderAsync(
    new GcsBackupStorageOptions
    {
        Bucket = "acme-kahuna-backups",
        Prefix = "prod/"
    },
    logger);
```

For all options, see [Configuration](configuration.md).

## Select the target

Set these properties on the Kahuna configuration:

| Property | Value |
| --- | --- |
| `BackupTarget` | `GcsBackupStorage.TargetName`. Its value is `"gcs"`. |
| `BackupDir` | A local directory. It is still necessary when the target is a bucket. |
| `BackupScratchDir` | A local directory. It is necessary for a bucket target. |
| `BackupStorageProvider` | The provider from the step before. |

```csharp
configuration.BackupTarget = GcsBackupStorage.TargetName;
configuration.BackupDir = "/var/lib/kahuna/backups";
configuration.BackupScratchDir = "/var/lib/kahuna/scratch";
configuration.BackupStorageProvider = provider;
```

An embedded host sets the same properties on `EmbeddedKahunaOptions`.

## Server command-line options

| Option | Property |
| --- | --- |
| `--pitr-backup-target gcs` | `BackupTarget` |
| `--pitr-backup-dir` | `BackupDir` |
| `--pitr-backup-scratch-dir` | `BackupScratchDir` |

## Startup checks

Kahuna does not start if one of these conditions is true:

- The target is not local, and no provider is registered.
- The target is not local, and no scratch directory is set.
- The bucket does not meet the [bucket requirements](bucket-requirements.md).

Thus, a configuration error shows at startup, not at the first scheduled backup.
