# Scratch directory

A bucket target needs a local scratch directory. Set it with `BackupScratchDir` or
`--pitr-backup-scratch-dir`.

## Why it is necessary

The Kahuna storage engine can write a checkpoint only to a file system. It cannot write directly to a
bucket. Thus:

1. A full backup writes its checkpoint into the scratch directory.
2. Kahuna uploads the checkpoint to the bucket.
3. Kahuna deletes the checkpoint from the scratch directory.

A restore does these steps in the opposite order. It uses the staging area that Kahuna already has.

## Size

Make the scratch volume large enough for one complete full backup. The full backup goes through the
scratch directory in one piece.

## Security

Backup files are not encrypted while they are in the scratch directory. Kahuna protects the
directory:

- Kahuna makes the directory accessible to its owner only.
- Kahuna refuses a root directory that is a symbolic link.
- Kahuna refuses a root directory that the group or other users can write.
