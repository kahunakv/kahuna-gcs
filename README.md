# Kahuna.Storage.Gcs

This package lets [Kahuna](https://github.com/kahunakv/kahuna) keep its backups in a Google Cloud
Storage (GCS) bucket. Without this package, Kahuna can keep backups only in a directory on the node.

The package is separate from `Kahuna.Core` on purpose. Other projects embed `Kahuna.Core`. If
`Kahuna.Core` referenced the GCS SDK, each of those projects would get the SDK too.

New to the terms on these pages? Read the
[glossary](https://github.com/kahunakv/kahuna-gcs/blob/main/docs/glossary.md) first.

## Requirements

- .NET 10.
- `Kahuna.Core` 1.15.6 or later. The package references it directly. Versions before 1.0.7 do not
  have the backup storage contracts that this package implements.

## Quick start

```csharp
using Kahuna.Storage.Gcs;

BackupStorageProvider provider = await GcsBackupStorage.CreateProviderAsync(
    new GcsBackupStorageOptions { Bucket = "acme-kahuna-backups", Prefix = "prod/" },
    logger);

configuration.BackupTarget = GcsBackupStorage.TargetName;   // "gcs"
configuration.BackupDir = "/var/lib/kahuna/backups";
configuration.BackupScratchDir = "/var/lib/kahuna/scratch";
configuration.BackupStorageProvider = provider;
```

Before the first start, do the steps in [Installation](https://github.com/kahunakv/kahuna-gcs/blob/main/docs/installation.md).
Kahuna does not start if the bucket does not meet the
[bucket requirements](https://github.com/kahunakv/kahuna-gcs/blob/main/docs/bucket-requirements.md).

## Documentation

| Page | Contents |
| --- | --- |
| [Glossary](https://github.com/kahunakv/kahuna-gcs/blob/main/docs/glossary.md) | The terms that these pages use |
| [Installation](https://github.com/kahunakv/kahuna-gcs/blob/main/docs/installation.md) | How to connect the package to a Kahuna host |
| [Authentication](https://github.com/kahunakv/kahuna-gcs/blob/main/docs/authentication.md) | Credentials and the permissions that the bucket must give |
| [Bucket requirements](https://github.com/kahunakv/kahuna-gcs/blob/main/docs/bucket-requirements.md) | Bucket settings that Kahuna checks at startup |
| [Scratch directory](https://github.com/kahunakv/kahuna-gcs/blob/main/docs/scratch-directory.md) | Why a local directory is necessary, and how large to make it |
| [Bucket layout](https://github.com/kahunakv/kahuna-gcs/blob/main/docs/bucket-layout.md) | The object names that Kahuna writes |
| [Configuration](https://github.com/kahunakv/kahuna-gcs/blob/main/docs/configuration.md) | All options and their default values |
| [Behaviour](https://github.com/kahunakv/kahuna-gcs/blob/main/docs/behaviour.md) | How uploads, deletes, and listings act when a step fails |
| [Testing](https://github.com/kahunakv/kahuna-gcs/blob/main/docs/testing.md) | How to run the tests, and what the tests do not prove |

## Licence

MIT
