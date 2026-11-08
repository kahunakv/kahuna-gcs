# Installation

This page tells you how to connect the GCS backup target to a Kahuna host. For the terms, see the
[glossary](glossary.md).

## Before you start

1. Create the bucket. Kahuna never creates it. See [Bucket requirements](bucket-requirements.md).
2. Give the host identity the necessary permissions. See [Authentication](authentication.md).
3. Make a local scratch directory. See [Scratch directory](scratch-directory.md).

## On the Kahuna server

The Kahuna server loads the target from an extension assembly. You do not write code.

1. Put the extension bundle into a directory on each node. The bundle is the DLL, its `.deps.json`
   file and the Google client libraries. Get it in one of two ways:

   - Download `kahuna-storage-gcs-<version>.zip` from the GitHub release, and extract it.
   - Build it from the source:

     ```sh
     scripts/publish-bundle.sh -o /opt/kahuna/backup-gcs
     ```

   Do not use the DLL from the NuGet package. The server reads the `.deps.json` file next to the DLL
   to find the Google dependencies.

   The bundle has no copy of `Kahuna.Core`, Kommander, `Grpc.*`, `Google.Protobuf` or the RocksDB and
   SQLite native files, because the server supplies them. The script fails if a copy gets into the
   bundle. A plain `dotnet publish` also gives a bundle that loads, but that bundle has all these
   copies and is approximately 150 MB.

2. Start the server with these options:

   ```sh
   kahuna-server --extension-assembly /opt/kahuna/backup-gcs/Kahuna.Storage.Gcs.dll \
                 --pitr-backup-dir /var/lib/kahuna/backups \
                 --pitr-backup-target gcs \
                 --pitr-backup-scratch-dir /var/lib/kahuna/scratch \
                 --extension-option gcs.bucket=acme-kahuna-backups gcs.prefix=prod/
   ```

The `gcs.*` keys of `--extension-option` are the settings of the target. For all keys, see
[Configuration](configuration.md). The server refuses to start if a `gcs.*` key is not known, so a
typing error cannot turn off a bucket protection.

The startup log shows the loaded file with its SHA-256 and `backup:gcs`. Give every node of the
cluster the same file and the same settings.

Build the package against a `Kahuna.Core` version that is equal to or lower than the version of the
server. If the package needs a newer version, the server does not start, and the message names both
versions.

## In an embedding host

An application that embeds `Kahuna.Core` adds the NuGet package. The package declares `Kahuna.Core`
as a full dependency, so the application gets it at build time and at run time:

```sh
dotnet add package Kahuna.Storage.Gcs
```

Then it registers the provider with its own options, before it builds the node:

```csharp
using Kahuna.Storage.Gcs;

EmbeddedKahunaOptions options = new()
{
    BackupDir = "/var/lib/kahuna/backups",
    BackupTarget = GcsBackupStorageProvider.Name,   // "gcs"
    BackupScratchDir = "/var/lib/kahuna/scratch",
    // ...
};

options.StorageProviders.Register(new GcsBackupStorageProvider(new GcsBackupStorageOptions
{
    Bucket = "acme-kahuna-backups",
    Prefix = "prod/"
}));

await using EmbeddedKahunaNode node = new(options, loggerFactory);
await node.StartAsync(ct);
```

A provider built with options refuses the `gcs.*` extension options, so each setting has one source.

## Startup checks

Kahuna does not start if one of these conditions is true:

- No provider is registered for the target name.
- No scratch directory is set. This target needs one.
- A `gcs.*` setting is not known, or its value is not valid.
- The credentials cannot be found, or the key file is not a service-account key.
- The bucket does not meet the [bucket requirements](bucket-requirements.md).

The first three checks run when the node is built. The last two run when the node starts its backup
storage, before it joins its cluster. Thus, a configuration error shows at startup, not at the first
scheduled backup.
